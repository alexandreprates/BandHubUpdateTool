using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;

namespace BandHub.FirmwareUpdate.Core;

public sealed class FirmwarePackage
{
    public const int HeaderBytes = 128;
    public static readonly byte[] VersionMarkerMagic =
        System.Text.Encoding.ASCII.GetBytes("BandHub-FW-Ver1!");

    private FirmwarePackage(
        byte[] bytes,
        FirmwareTarget target,
        uint firmwareVersion,
        uint imageSize,
        uint headerCrc32)
    {
        Bytes = bytes;
        Target = target;
        FirmwareVersion = firmwareVersion;
        ImageSize = imageSize;
        HeaderCrc32 = headerCrc32;
    }

    public byte[] Bytes { get; }
    public FirmwareTarget Target { get; }
    public uint FirmwareVersion { get; }
    public uint ImageSize { get; }
    public uint HeaderCrc32 { get; }

    public static FirmwarePackage ParseAndVerify(byte[] bytes, byte[] publicKey)
    {
        if (bytes.Length < HeaderBytes)
        {
            throw new InvalidDataException("Firmware package is shorter than its header.");
        }
        if (OtaProtocol.ReadUInt32(bytes, 0) != 0x57464842)
        {
            throw new InvalidDataException("Firmware package magic is invalid.");
        }

        var format = OtaProtocol.ReadUInt16(bytes, 4);
        var target = (FirmwareTarget)OtaProtocol.ReadUInt32(bytes, 8);
        var exactTarget = target != FirmwareTarget.LegacyController;
        if ((format != 1 && format != 2) || OtaProtocol.ReadUInt16(bytes, 6) != HeaderBytes ||
            (format == 1) != !exactTarget || !Enum.IsDefined(typeof(FirmwareTarget), target))
        {
            throw new InvalidDataException("Firmware package format or target is unsupported.");
        }

        var firmwareVersion = OtaProtocol.ReadUInt32(bytes, 12);
        var imageSize = OtaProtocol.ReadUInt32(bytes, 16);
        var packageSize = OtaProtocol.ReadUInt32(bytes, 20);
        if (firmwareVersion == 0 || imageSize == 0 || imageSize > 0x140000 ||
            packageSize != bytes.Length || packageSize != HeaderBytes + imageSize)
        {
            throw new InvalidDataException("Firmware package sizes or version are invalid.");
        }
        if (bytes[56] != 1 || bytes[57] != 64 || OtaProtocol.ReadUInt16(bytes, 58) != 0)
        {
            throw new InvalidDataException("Firmware package signature metadata is invalid.");
        }

        var expectedHeaderCrc = OtaProtocol.ReadUInt32(bytes, 124);
        if (Crc32(bytes, 124) != expectedHeaderCrc)
        {
            throw new InvalidDataException("Firmware package header CRC is invalid.");
        }

        var image = new byte[imageSize];
        Buffer.BlockCopy(bytes, HeaderBytes, image, 0, image.Length);
        using var sha = SHA256.Create();
        var imageDigest = sha.ComputeHash(image);
        if (!imageDigest.SequenceEqual(bytes.Skip(24).Take(32)))
        {
            throw new InvalidDataException("Firmware image SHA-256 is invalid.");
        }

        var manifest = new byte[60];
        Buffer.BlockCopy(bytes, 0, manifest, 0, manifest.Length);
        var signature = new byte[64];
        Buffer.BlockCopy(bytes, 60, signature, 0, signature.Length);
        if (!VerifyP256(manifest, signature, publicKey))
        {
            throw new InvalidDataException("Firmware package signature is invalid.");
        }
        if (ReadEmbeddedVersion(image) != firmwareVersion)
        {
            throw new InvalidDataException("Embedded firmware version does not match the package.");
        }

        return new FirmwarePackage(bytes, target, firmwareVersion, imageSize, expectedHeaderCrc);
    }

    public static byte[] LoadEmbeddedPublicKey()
    {
        var assembly = typeof(FirmwarePackage).GetTypeInfo().Assembly;
        var resource = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("release-public-key.hex", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Release public key resource is missing.");
        using var reader = new StreamReader(stream);
        var hex = reader.ReadToEnd().Trim();
        if (hex.Length != 130)
        {
            throw new InvalidDataException("Release public key has an invalid length.");
        }
        return Enumerable.Range(0, hex.Length / 2)
            .Select(index => Convert.ToByte(hex.Substring(index * 2, 2), 16))
            .ToArray();
    }

    public static bool VerifyP256(byte[] payload, byte[] signature, byte[] publicKey)
    {
        if (signature.Length != 64 || publicKey.Length != 65 || publicKey[0] != 0x04)
        {
            return false;
        }
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(payload);
        X9ECParameters curve = ECNamedCurveTable.GetByName("secp256r1");
        var domain = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H, curve.GetSeed());
        var key = new ECPublicKeyParameters(curve.Curve.DecodePoint(publicKey), domain);
        var verifier = new ECDsaSigner();
        verifier.Init(false, key);
        return verifier.VerifySignature(
            digest,
            new BigInteger(1, signature.Take(32).ToArray()),
            new BigInteger(1, signature.Skip(32).Take(32).ToArray()));
    }

    private static uint ReadEmbeddedVersion(byte[] image)
    {
        uint version = 0;
        var matches = 0;
        for (var offset = 0; offset <= image.Length - VersionMarkerMagic.Length - 8; ++offset)
        {
            var match = true;
            for (var index = 0; index < VersionMarkerMagic.Length; ++index)
            {
                if (image[offset + index] != VersionMarkerMagic[index])
                {
                    match = false;
                    break;
                }
            }
            if (!match)
            {
                continue;
            }
            var candidate = OtaProtocol.ReadUInt32(image, offset + VersionMarkerMagic.Length);
            var inverted = OtaProtocol.ReadUInt32(image, offset + VersionMarkerMagic.Length + 4);
            if (candidate != 0 && inverted == ~candidate)
            {
                version = candidate;
                ++matches;
            }
        }
        if (matches != 1)
        {
            throw new InvalidDataException("Firmware image must contain one version marker.");
        }
        return version;
    }

    private static uint Crc32(byte[] data, int length)
    {
        uint crc = 0xffffffff;
        for (var index = 0; index < length; ++index)
        {
            crc ^= data[index];
            for (var bit = 0; bit < 8; ++bit)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320 : 0);
            }
        }
        return ~crc;
    }
}
