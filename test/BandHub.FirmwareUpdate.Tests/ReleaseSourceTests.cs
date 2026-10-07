using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BandHub.FirmwareUpdate.Core;
using NUnit.Framework;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;

namespace BandHub.FirmwareUpdate.Tests;

[TestFixture]
public sealed class ReleaseSourceTests
{
    private static readonly X9ECParameters FixtureCurve =
        ECNamedCurveTable.GetByName("secp256r1");
    private static readonly ECDomainParameters FixtureDomain = new(
        FixtureCurve.Curve,
        FixtureCurve.G,
        FixtureCurve.N,
        FixtureCurve.H,
        FixtureCurve.GetSeed());
    private static readonly ECPrivateKeyParameters FixtureSigningKey =
        new(BigInteger.One, FixtureDomain);
    internal static readonly byte[] FixturePublicKey =
        FixtureCurve.G.Multiply(BigInteger.One).Normalize().GetEncoded(false);

    [Test]
    public void CloudflareR2SourceEmbedsProductionPublicDomain()
    {
        var baseUrl = typeof(CloudflareR2ReleaseSource).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "BandHubReleaseBaseUrl")
            .Value;

        Assert.That(baseUrl, Is.EqualTo("https://bandhub.alexandreprates.dev/"));
    }

    [Test]
    public async Task LocalBundleLoadsSignedExactTargetPackages()
    {
        var path = CreateBundle(FirmwareTarget.ControllerGh3);
        try
        {
            var selection = await new LocalBundleReleaseSource(path, FixturePublicKey).LoadAsync(
                SupportedDevice(), CancellationToken.None);

            Assert.That(selection.DonglePackage.Target, Is.EqualTo(FirmwareTarget.DongleZeroPc));
            Assert.That(selection.ControllerPackage?.Target, Is.EqualTo(FirmwareTarget.ControllerGh3));
            Assert.That(selection.DongleManifest.ReleaseVersion, Is.EqualTo("test-1"));
            Assert.That(selection.ControllerManifest, Is.SameAs(selection.DongleManifest));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task LocalBundleLoadsLegacyFlatSchemaOnePackages()
    {
        var path = CreateBundle(FirmwareTarget.ControllerGh3, versionedPaths: false);
        try
        {
            var selection = await new LocalBundleReleaseSource(path, FixturePublicKey).LoadAsync(
                SupportedDevice(), CancellationToken.None);

            Assert.That(selection.DonglePackage.Target, Is.EqualTo(FirmwareTarget.DongleZeroPc));
            Assert.That(selection.ControllerPackage?.Target, Is.EqualTo(FirmwareTarget.ControllerGh3));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void LocalBundleRejectsPackageTargetThatDiffersFromManifest()
    {
        var path = CreateBundle(FirmwareTarget.ControllerGh5);
        try
        {
            Assert.ThrowsAsync<InvalidDataException>(async () =>
                await new LocalBundleReleaseSource(path, FixturePublicKey).LoadAsync(
                    SupportedDevice(), CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task CloudflareR2SourceLoadsVersionedComponentObjects()
    {
        const uint firmwareVersion = 0x00030000;
        var donglePackage = BuildPackage(FirmwareTarget.DongleZeroPc, firmwareVersion);
        var controllerPackage = BuildPackage(FirmwareTarget.ControllerGh3, firmwareVersion);
        var dongleManifest = BuildComponentManifest(
            "dongle", "1.2.0", "dongle-zero-pc", donglePackage, firmwareVersion);
        var controllerManifest = BuildComponentManifest(
            "controller", "1.3.0", "controller-gh3", controllerPackage, firmwareVersion);
        const string baseUrl = "https://firmware.example.test/bandhub-releases/";
        var responses = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{baseUrl}dongle/release.json"] = dongleManifest,
            [$"{baseUrl}dongle/release.json.sig"] = Sign(dongleManifest),
            [$"{baseUrl}controller/release.json"] = controllerManifest,
            [$"{baseUrl}controller/release.json.sig"] = Sign(controllerManifest),
            [$"{baseUrl}dongle/1.2.0/dongle-zero-pc.bhfw"] = donglePackage,
            [$"{baseUrl}controller/1.3.0/controller-gh3.bhfw"] = controllerPackage,
        };
        var handler = new StaticResponseHandler(responses);
        using var source = new CloudflareR2ReleaseSource(
            new HttpClient(handler), baseUrl.TrimEnd('/'), FixturePublicKey);

        var selection = await source.LoadAsync(SupportedDevice(), CancellationToken.None);

        Assert.That(selection.DonglePackage.Target, Is.EqualTo(FirmwareTarget.DongleZeroPc));
        Assert.That(selection.ControllerPackage?.Target, Is.EqualTo(FirmwareTarget.ControllerGh3));
        Assert.That(selection.DongleManifest.ReleaseVersion, Is.EqualTo("1.2.0"));
        Assert.That(selection.ControllerManifest?.ReleaseVersion, Is.EqualTo("1.3.0"));
        Assert.That(handler.RequestedUris, Is.EquivalentTo(responses.Keys));
    }

    [Test]
    public void CloudflareR2SourceRejectsAuthenticatedS3EndpointAsPublicUrl()
    {
        const string endpoint =
            "https://example.r2.cloudflarestorage.com/bandhub-releases";
        using var client = new HttpClient();

        var error = Assert.Throws<InvalidOperationException>(() =>
            new CloudflareR2ReleaseSource(client, endpoint));

        Assert.That(error?.Message, Does.Contain("S3 endpoint"));
    }

    [TestCase(FirmwareTarget.ControllerGh3SuperMini, "controller-gh3-supermini")]
    [TestCase(FirmwareTarget.ControllerGh5SuperMini, "controller-gh5-supermini")]
    public async Task DirectUsbCatalogNeverRequestsDongle(FirmwareTarget target, string targetName)
    {
        var package = BuildPackage(target, 0x00100000);
        var manifest = BuildComponentManifest("controller", "1.4.0", targetName, package, 0x00100000);
        const string baseUrl = "https://firmware.example.test/";
        var responses = new Dictionary<string, byte[]>
        {
            [baseUrl + "controller/release.json"] = manifest,
            [baseUrl + "controller/release.json.sig"] = Sign(manifest),
            [baseUrl + $"controller/1.4.0/{targetName}.bhfw"] = package,
        };
        var handler = new StaticResponseHandler(responses);
        using var source = new CloudflareR2ReleaseSource(new HttpClient(handler), baseUrl, FixturePublicKey);
        var info = new ControllerUpdateInfo("D88B499205D4", target, 0xf0000, 0x140000, true, ControllerImageHealth.Healthy);
        var actual = await source.LoadControllerAsync(info, CancellationToken.None);
        Assert.That(actual.Target, Is.EqualTo(target));
        Assert.That(handler.RequestedUris, Is.EquivalentTo(responses.Keys));
    }

    [Test]
    public async Task DirectUsbLocalBundleNeedsOnlyControllerAndEnforcesModel()
    {
        var package = BuildPackage(FirmwareTarget.ControllerGh3SuperMini, 0x00100000);
        var manifest = BuildComponentManifest("controller", "1.4.0", "controller-gh3-supermini", package, 0x00100000);
        var path = Path.GetTempFileName();
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                WriteEntry(archive, "release.json", manifest);
                WriteEntry(archive, "release.json.sig", Sign(manifest));
                WriteEntry(archive, "controller/1.4.0/controller-gh3-supermini.bhfw", package);
            }
            var info = new ControllerUpdateInfo("D88B499205D4", FirmwareTarget.ControllerGh3SuperMini, 0xf0000, 0x140000, true, ControllerImageHealth.Healthy);
            var source = new LocalBundleReleaseSource(path, FixturePublicKey);
            Assert.That((await source.LoadControllerAsync(info, CancellationToken.None)).Target, Is.EqualTo(info.Target));
            Assert.ThrowsAsync<InvalidDataException>(async () => await source.LoadControllerAsync(
                info with { Target = FirmwareTarget.ControllerGh5SuperMini }, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task DongleOnlyCatalogNeverRequestsController()
    {
        var package = BuildPackage(FirmwareTarget.DongleZeroPc, 0x100000);
        var manifest = BuildComponentManifest("dongle", "1.4.0", "dongle-zero-pc", package, 0x100000);
        const string baseUrl = "https://firmware.example.test/";
        var responses = new Dictionary<string, byte[]>
        {
            [baseUrl + "dongle/release.json"] = manifest,
            [baseUrl + "dongle/release.json.sig"] = Sign(manifest),
            [baseUrl + "dongle/1.4.0/dongle-zero-pc.bhfw"] = package,
        };
        var handler = new StaticResponseHandler(responses);
        using var source = new CloudflareR2ReleaseSource(new HttpClient(handler), baseUrl, FixturePublicKey);
        var actual = await source.LoadDongleAsync(SupportedDevice(), CancellationToken.None);
        Assert.That(actual.ControllerPackage, Is.Null);
        Assert.That(handler.RequestedUris, Is.EquivalentTo(responses.Keys));
    }

    [Test]
    public async Task DirectUsbRawPackageRejectsCorruptedImageAndUntrustedSignature()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bhfw");
        var package = BuildPackage(FirmwareTarget.ControllerGh3SuperMini, 0x100000);
        var info = new ControllerUpdateInfo("D88B499205D4", FirmwareTarget.ControllerGh3SuperMini,
            0xf0000, 0x140000, true, ControllerImageHealth.Healthy);
        try
        {
            File.WriteAllBytes(path, package);
            var source = new LocalBundleReleaseSource(path, FixturePublicKey);
            Assert.That((await source.LoadControllerAsync(info, CancellationToken.None)).Bytes, Is.EqualTo(package));
            var corrupt = package.ToArray(); corrupt[^1] ^= 1; File.WriteAllBytes(path, corrupt);
            Assert.ThrowsAsync<InvalidDataException>(() => source.LoadControllerAsync(info, CancellationToken.None));
            corrupt = package.ToArray(); corrupt[60] ^= 1;
            WriteUInt32(corrupt, 124, Crc32(corrupt, 124)); File.WriteAllBytes(path, corrupt);
            Assert.ThrowsAsync<InvalidDataException>(() => source.LoadControllerAsync(info, CancellationToken.None));
            File.WriteAllBytes(path, package);
            Assert.ThrowsAsync<InvalidDataException>(() => source.LoadControllerAsync(
                info with { MaximumImageSize = 128 }, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    private static DeviceInfo SupportedDevice() => new()
    {
        Capabilities = 0x03,
        UsbProfile = 1,
        DongleTarget = FirmwareTarget.DongleZeroPc,
        DongleFirmwareVersion = 0x00020000,
        ControllerTarget = FirmwareTarget.ControllerGh3,
        ControllerFirmwareVersion = 0x00020000,
        ControllerBatteryPercent = 80,
        Flags = 0x07,
        DongleMac = new byte[] { 0x02, 1, 2, 3, 4, 5 },
        ControllerMac = new byte[] { 0x02, 6, 7, 8, 9, 10 },
    };

    private static string CreateBundle(
        FirmwareTarget controllerPackageTarget,
        bool versionedPaths = true)
    {
        const uint firmwareVersion = 0x00030000;
        var donglePackage = BuildPackage(FirmwareTarget.DongleZeroPc, firmwareVersion);
        var controllerPackage = BuildPackage(controllerPackageTarget, firmwareVersion);
        var manifest = BuildManifest(
            donglePackage, controllerPackage, firmwareVersion, versionedPaths);
        var path = Path.Combine(
            Path.GetTempPath(), $"bandhub-release-{Guid.NewGuid():N}.bhrelease");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "release.json", manifest);
        WriteEntry(archive, "release.json.sig", Sign(manifest));
        WriteEntry(
            archive,
            versionedPaths
                ? "dongle/test-1/dongle-zero-pc.bhfw"
                : "dongle-zero-pc.bhfw",
            donglePackage);
        WriteEntry(
            archive,
            versionedPaths
                ? "controller/test-1/controller-gh3.bhfw"
                : "controller-gh3.bhfw",
            controllerPackage);
        return path;
    }

    private static byte[] BuildManifest(
        byte[] donglePackage,
        byte[] controllerPackage,
        uint firmwareVersion,
        bool versionedPaths) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            artifacts = new object[]
            {
                Artifact("dongle", "dongle-zero-pc",
                         versionedPaths
                             ? "dongle/test-1/dongle-zero-pc.bhfw"
                             : "dongle-zero-pc.bhfw",
                         firmwareVersion, donglePackage),
                Artifact("controller", "controller-gh3",
                         versionedPaths
                             ? "controller/test-1/controller-gh3.bhfw"
                             : "controller-gh3.bhfw",
                         firmwareVersion, controllerPackage),
            },
            channel = "stable",
            maximumOtaProtocol = 1,
            minimumOtaProtocol = 1,
            releaseVersion = "test-1",
            schemaVersion = versionedPaths ? 2 : 1,
        });

    private static byte[] BuildComponentManifest(
        string component,
        string releaseVersion,
        string target,
        byte[] package,
        uint firmwareVersion) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            artifacts = new object[]
            {
                Artifact(
                    component,
                    target,
                    $"{component}/{releaseVersion}/{target}.bhfw",
                    firmwareVersion,
                    package),
            },
            channel = "stable",
            component,
            maximumOtaProtocol = 1,
            minimumOtaProtocol = 1,
            releaseVersion,
            schemaVersion = 3,
        });

    private static object Artifact(
        string component,
        string target,
        string asset,
        uint firmwareVersion,
        byte[] package) => new
    {
        asset,
        component,
        firmwareVersion,
        maximumPeerVersion = uint.MaxValue,
        minimumPeerVersion = 0U,
        sha256 = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
        size = package.LongLength,
        target,
    };

    internal static byte[] BuildPackage(FirmwareTarget target, uint firmwareVersion)
    {
        var image = new byte[256];
        var markerOffset = 32;
        Buffer.BlockCopy(
            FirmwarePackage.VersionMarkerMagic,
            0,
            image,
            markerOffset,
            FirmwarePackage.VersionMarkerMagic.Length);
        WriteUInt32(image, markerOffset + FirmwarePackage.VersionMarkerMagic.Length,
                    firmwareVersion);
        WriteUInt32(image, markerOffset + FirmwarePackage.VersionMarkerMagic.Length + 4,
                    ~firmwareVersion);

        var header = new byte[FirmwarePackage.HeaderBytes];
        WriteUInt32(header, 0, 0x57464842);
        WriteUInt16(header, 4, 2);
        WriteUInt16(header, 6, FirmwarePackage.HeaderBytes);
        WriteUInt32(header, 8, (uint)target);
        WriteUInt32(header, 12, firmwareVersion);
        WriteUInt32(header, 16, (uint)image.Length);
        WriteUInt32(header, 20, (uint)(header.Length + image.Length));
        Buffer.BlockCopy(SHA256.HashData(image), 0, header, 24, 32);
        header[56] = 1;
        header[57] = 64;
        var signedPrefix = header.Take(60).ToArray();
        Buffer.BlockCopy(Sign(signedPrefix), 0, header, 60, 64);
        WriteUInt32(header, 124, Crc32(header, 124));

        var package = new byte[header.Length + image.Length];
        Buffer.BlockCopy(header, 0, package, 0, header.Length);
        Buffer.BlockCopy(image, 0, package, header.Length, image.Length);
        return package;
    }

    private static byte[] Sign(byte[] payload)
    {
        var signer = new ECDsaSigner();
        signer.Init(true, FixtureSigningKey);
        var components = signer.GenerateSignature(SHA256.HashData(payload));
        var signature = new byte[64];
        CopyComponent(components[0].ToByteArrayUnsigned(), signature, 0);
        CopyComponent(components[1].ToByteArrayUnsigned(), signature, 32);
        return signature;
    }

    private static void CopyComponent(byte[] component, byte[] output, int offset)
    {
        if (component.Length > 32)
        {
            throw new InvalidDataException("ECDSA component exceeds P-256 size.");
        }
        Buffer.BlockCopy(component, 0, output, offset + 32 - component.Length, component.Length);
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(content, 0, content.Length);
    }

    private static uint Crc32(byte[] data, int length)
    {
        uint crc = uint.MaxValue;
        for (var index = 0; index < length; ++index)
        {
            crc ^= data[index];
            for (var bit = 0; bit < 8; ++bit)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320U : 0U);
            }
        }
        return ~crc;
    }

    private static void WriteUInt16(byte[] output, int offset, int value)
    {
        output[offset] = (byte)value;
        output[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] output, int offset, uint value)
    {
        output[offset] = (byte)value;
        output[offset + 1] = (byte)(value >> 8);
        output[offset + 2] = (byte)(value >> 16);
        output[offset + 3] = (byte)(value >> 24);
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, byte[]> responses;

        public StaticResponseHandler(IReadOnlyDictionary<string, byte[]> responses) =>
            this.responses = responses;

        public List<string> RequestedUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            RequestedUris.Add(uri);
            if (!responses.TryGetValue(uri, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        }
    }
}
