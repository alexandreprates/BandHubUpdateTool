using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BandHub.FirmwareUpdate.Core;
using NUnit.Framework;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.OpenSsl;

namespace BandHub.FirmwareUpdate.Tests;

[TestFixture]
public sealed class ReleaseSourceTests
{
    [Test]
    public async Task LocalBundleLoadsSignedExactTargetPackages()
    {
        var path = CreateBundle(FirmwareTarget.ControllerGh3);
        try
        {
            var selection = await new LocalBundleReleaseSource(path).LoadAsync(
                SupportedDevice(), CancellationToken.None);

            Assert.That(selection.DonglePackage.Target, Is.EqualTo(FirmwareTarget.DongleZeroPc));
            Assert.That(selection.ControllerPackage?.Target, Is.EqualTo(FirmwareTarget.ControllerGh3));
            Assert.That(selection.Manifest.ReleaseVersion, Is.EqualTo("test-1"));
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
                await new LocalBundleReleaseSource(path).LoadAsync(
                    SupportedDevice(), CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
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

    private static string CreateBundle(FirmwareTarget controllerPackageTarget)
    {
        const uint firmwareVersion = 0x00030000;
        var donglePackage = BuildPackage(FirmwareTarget.DongleZeroPc, firmwareVersion);
        var controllerPackage = BuildPackage(controllerPackageTarget, firmwareVersion);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            artifacts = new object[]
            {
                Artifact("dongle", "dongle-zero-pc", "dongle-zero-pc.bhfw",
                         firmwareVersion, donglePackage),
                Artifact("controller", "controller-gh3", "controller-gh3.bhfw",
                         firmwareVersion, controllerPackage),
            },
            channel = "stable",
            maximumOtaProtocol = 1,
            minimumOtaProtocol = 1,
            releaseVersion = "test-1",
            schemaVersion = 1,
        });
        var path = Path.Combine(
            Path.GetTempPath(), $"bandhub-release-{Guid.NewGuid():N}.bhrelease");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "release.json", manifest);
        WriteEntry(archive, "release.json.sig", Sign(manifest));
        WriteEntry(archive, "dongle-zero-pc.bhfw", donglePackage);
        WriteEntry(archive, "controller-gh3.bhfw", controllerPackage);
        return path;
    }

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

    private static byte[] BuildPackage(FirmwareTarget target, uint firmwareVersion)
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
        using var reader = new StreamReader(FindPrivateKey());
        var keyObject = new PemReader(reader).ReadObject();
        var privateKey = keyObject switch
        {
            AsymmetricCipherKeyPair pair => (ECPrivateKeyParameters)pair.Private,
            ECPrivateKeyParameters key => key,
            _ => throw new InvalidDataException("Repository release private key is invalid."),
        };
        var signer = new ECDsaSigner();
        signer.Init(true, privateKey);
        var components = signer.GenerateSignature(SHA256.HashData(payload));
        var signature = new byte[64];
        CopyComponent(components[0].ToByteArrayUnsigned(), signature, 0);
        CopyComponent(components[1].ToByteArrayUnsigned(), signature, 32);
        return signature;
    }

    private static string FindPrivateKey()
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
             directory != null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName, "pki", "controller-ota-private.pem");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("Repository release private key was not found.");
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
}
