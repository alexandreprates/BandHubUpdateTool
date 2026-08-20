using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace BandHub.FirmwareUpdate.Core;

public sealed class ReleaseArtifact
{
    [JsonProperty("component", Required = Required.Always)]
    public string Component { get; set; } = string.Empty;

    [JsonProperty("target", Required = Required.Always)]
    public string Target { get; set; } = string.Empty;

    [JsonProperty("firmwareVersion", Required = Required.Always)]
    public uint FirmwareVersion { get; set; }

    [JsonProperty("minimumPeerVersion", Required = Required.Always)]
    public uint MinimumPeerVersion { get; set; }

    [JsonProperty("maximumPeerVersion", Required = Required.Always)]
    public uint MaximumPeerVersion { get; set; }

    [JsonProperty("asset", Required = Required.Always)]
    public string Asset { get; set; } = string.Empty;

    [JsonProperty("size", Required = Required.Always)]
    public long Size { get; set; }

    [JsonProperty("sha256", Required = Required.Always)]
    public string Sha256 { get; set; } = string.Empty;

    [JsonIgnore]
    public FirmwareTarget ParsedTarget => Target switch
    {
        "controller-gh3" => FirmwareTarget.ControllerGh3,
        "controller-gh5" => FirmwareTarget.ControllerGh5,
        "dongle-devkit-pc" => FirmwareTarget.DongleDevKitPc,
        "dongle-zero-pc" => FirmwareTarget.DongleZeroPc,
        _ => throw new InvalidDataException($"Unknown release target '{Target}'."),
    };
}

public sealed class ReleaseManifest
{
    [JsonProperty("schemaVersion", Required = Required.Always)]
    public int SchemaVersion { get; set; }

    [JsonProperty("releaseVersion", Required = Required.Always)]
    public string ReleaseVersion { get; set; } = string.Empty;

    [JsonProperty("channel", Required = Required.Always)]
    public string Channel { get; set; } = string.Empty;

    [JsonProperty("minimumOtaProtocol", Required = Required.Always)]
    public int MinimumOtaProtocol { get; set; }

    [JsonProperty("maximumOtaProtocol", Required = Required.Always)]
    public int MaximumOtaProtocol { get; set; }

    [JsonProperty("artifacts", Required = Required.Always)]
    public List<ReleaseArtifact> Artifacts { get; set; } = new();

    public static ReleaseManifest ParseAndVerify(
        byte[] json,
        byte[] signature,
        byte[] publicKey)
    {
        if (!FirmwarePackage.VerifyP256(json, signature, publicKey))
        {
            throw new InvalidDataException("Release manifest signature is invalid.");
        }
        var manifest = JsonConvert.DeserializeObject<ReleaseManifest>(
            System.Text.Encoding.UTF8.GetString(json),
            new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error })
            ?? throw new InvalidDataException("Release manifest is empty.");
        manifest.Validate();
        return manifest;
    }

    public ReleaseArtifact Find(FirmwareTarget target)
    {
        var matches = Artifacts.Where(artifact => artifact.ParsedTarget == target).ToList();
        if (matches.Count != 1)
        {
            throw new InvalidDataException($"Release must contain exactly one artifact for {target}.");
        }
        return matches[0];
    }

    private void Validate()
    {
        if (SchemaVersion != 1 || Channel != "stable" || string.IsNullOrWhiteSpace(ReleaseVersion) ||
            MinimumOtaProtocol != 1 || MaximumOtaProtocol != 1 || Artifacts.Count == 0)
        {
            throw new InvalidDataException("Release manifest metadata is unsupported.");
        }
        var targets = new HashSet<FirmwareTarget>();
        var assets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in Artifacts)
        {
            var target = artifact.ParsedTarget;
            if (artifact.FirmwareVersion == 0 || artifact.Size <= FirmwarePackage.HeaderBytes ||
                artifact.MinimumPeerVersion > artifact.MaximumPeerVersion ||
                string.IsNullOrWhiteSpace(artifact.Asset) || artifact.Asset == "." ||
                artifact.Asset == ".." || artifact.Asset.Contains("/") ||
                artifact.Asset.Contains("\\") ||
                artifact.Sha256.Length != 64 ||
                artifact.Sha256.Any(value => !Uri.IsHexDigit(value)) ||
                !targets.Add(target) || !assets.Add(artifact.Asset))
            {
                throw new InvalidDataException("Release artifact metadata is invalid.");
            }
        }
    }
}

public sealed class ReleaseSelection
{
    public ReleaseManifest Manifest { get; init; } = new();
    public FirmwarePackage DonglePackage { get; init; } = null!;
    public FirmwarePackage? ControllerPackage { get; init; }
}

public interface IReleaseSource
{
    Task<ReleaseSelection> LoadAsync(DeviceInfo device, CancellationToken cancellationToken);
}

public sealed class GitHubReleaseSource : IReleaseSource, IDisposable
{
    public const string DefaultBaseUrl =
        "https://github.com/alexandreprates/BandHub-Releases/releases/latest/download/";

    private readonly HttpClient client;
    private readonly byte[] publicKey;

    public GitHubReleaseSource(HttpClient? client = null)
    {
        this.client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        publicKey = FirmwarePackage.LoadEmbeddedPublicKey();
    }

    public async Task<ReleaseSelection> LoadAsync(
        DeviceInfo device,
        CancellationToken cancellationToken)
    {
        var json = await GetAsync("release.json", cancellationToken).ConfigureAwait(false);
        var signature = await GetAsync("release.json.sig", cancellationToken).ConfigureAwait(false);
        var manifest = ReleaseManifest.ParseAndVerify(json, signature, publicKey);
        var dongleArtifact = manifest.Find(device.DongleTarget);
        var controllerArtifact = device.ControllerTarget == 0
            ? null
            : manifest.Find(device.ControllerTarget);
        var donglePackage = await LoadPackageAsync(dongleArtifact, cancellationToken)
            .ConfigureAwait(false);
        var controllerPackage = controllerArtifact == null
            ? null
            : await LoadPackageAsync(controllerArtifact, cancellationToken).ConfigureAwait(false);
        return ValidateSelection(device, manifest, dongleArtifact, controllerArtifact,
                                 donglePackage, controllerPackage);
    }

    public void Dispose() => client.Dispose();

    private async Task<FirmwarePackage> LoadPackageAsync(
        ReleaseArtifact artifact,
        CancellationToken cancellationToken)
    {
        var bytes = await GetAsync(artifact.Asset, cancellationToken).ConfigureAwait(false);
        ValidateArtifactBytes(artifact, bytes);
        var package = FirmwarePackage.ParseAndVerify(bytes, publicKey);
        ValidatePackageMetadata(artifact, package);
        return package;
    }

    private async Task<byte[]> GetAsync(string asset, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(DefaultBaseUrl + asset, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    internal static ReleaseSelection ValidateSelection(
        DeviceInfo device,
        ReleaseManifest manifest,
        ReleaseArtifact dongleArtifact,
        ReleaseArtifact? controllerArtifact,
        FirmwarePackage donglePackage,
        FirmwarePackage? controllerPackage)
    {
        if (device.UsbProfile != 1 || !device.SupportsDongleSelfOta)
        {
            throw new NotSupportedException("This Dongle does not support software self-update.");
        }
        if (controllerArtifact != null &&
            (device.DongleFirmwareVersion < controllerArtifact.MinimumPeerVersion ||
             device.DongleFirmwareVersion > controllerArtifact.MaximumPeerVersion))
        {
            throw new InvalidDataException("Controller release is incompatible with the installed Dongle.");
        }
        if (device.ControllerFirmwareVersion != 0 &&
            (device.ControllerFirmwareVersion < dongleArtifact.MinimumPeerVersion ||
             device.ControllerFirmwareVersion > dongleArtifact.MaximumPeerVersion))
        {
            throw new InvalidDataException("Dongle release is incompatible with the paired Controller.");
        }
        return new ReleaseSelection
        {
            Manifest = manifest,
            DonglePackage = donglePackage,
            ControllerPackage = controllerPackage,
        };
    }

    internal static void ValidateArtifactBytes(ReleaseArtifact artifact, byte[] bytes)
    {
        if (bytes.LongLength != artifact.Size)
        {
            throw new InvalidDataException("Release artifact size does not match the manifest.");
        }
        using var sha = SHA256.Create();
        var digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
        if (!digest.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Release artifact SHA-256 does not match the manifest.");
        }
    }

    internal static void ValidatePackageMetadata(
        ReleaseArtifact artifact,
        FirmwarePackage package)
    {
        if (package.Target != artifact.ParsedTarget ||
            package.FirmwareVersion != artifact.FirmwareVersion)
        {
            throw new InvalidDataException("Release artifact does not match its signed package.");
        }
    }
}

public sealed class LocalBundleReleaseSource : IReleaseSource
{
    private readonly string path;
    private readonly byte[] publicKey = FirmwarePackage.LoadEmbeddedPublicKey();

    public LocalBundleReleaseSource(string path) => this.path = path;

    public Task<ReleaseSelection> LoadAsync(DeviceInfo device, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = ZipFile.OpenRead(path);
        var json = ReadEntry(archive, "release.json");
        var signature = ReadEntry(archive, "release.json.sig");
        var manifest = ReleaseManifest.ParseAndVerify(json, signature, publicKey);
        var dongleArtifact = manifest.Find(device.DongleTarget);
        var controllerArtifact = device.ControllerTarget == 0
            ? null
            : manifest.Find(device.ControllerTarget);
        var dongleBytes = ReadEntry(archive, dongleArtifact.Asset);
        GitHubReleaseSource.ValidateArtifactBytes(dongleArtifact, dongleBytes);
        var donglePackage = FirmwarePackage.ParseAndVerify(dongleBytes, publicKey);
        GitHubReleaseSource.ValidatePackageMetadata(dongleArtifact, donglePackage);
        FirmwarePackage? controllerPackage = null;
        if (controllerArtifact != null)
        {
            var controllerBytes = ReadEntry(archive, controllerArtifact.Asset);
            GitHubReleaseSource.ValidateArtifactBytes(controllerArtifact, controllerBytes);
            controllerPackage = FirmwarePackage.ParseAndVerify(controllerBytes, publicKey);
            GitHubReleaseSource.ValidatePackageMetadata(controllerArtifact, controllerPackage);
        }
        return Task.FromResult(GitHubReleaseSource.ValidateSelection(
            device, manifest, dongleArtifact, controllerArtifact,
            donglePackage, controllerPackage));
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name)
            ?? throw new InvalidDataException($"Local release bundle is missing '{name}'.");
        using var stream = entry.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
