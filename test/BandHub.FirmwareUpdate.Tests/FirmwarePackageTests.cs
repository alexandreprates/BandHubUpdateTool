using System.IO;
using BandHub.FirmwareUpdate.Core;
using NUnit.Framework;

namespace BandHub.FirmwareUpdate.Tests;

[TestFixture]
public sealed class FirmwarePackageTests
{
    [Test]
    public void EmbeddedReleaseKeyIsValidUncompressedP256Key()
    {
        var key = FirmwarePackage.LoadEmbeddedPublicKey();

        Assert.That(key, Has.Length.EqualTo(65));
        Assert.That(key[0], Is.EqualTo(0x04));
    }

    [Test]
    public void PackageParserRejectsShortInputBeforeCryptography()
    {
        var key = FirmwarePackage.LoadEmbeddedPublicKey();

        Assert.Throws<InvalidDataException>(() => FirmwarePackage.ParseAndVerify(new byte[127], key));
    }
}
