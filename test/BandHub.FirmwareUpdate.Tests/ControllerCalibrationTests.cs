using System;
using System.IO;
using BandHub.FirmwareUpdate.Core;
using NUnit.Framework;

namespace BandHub.FirmwareUpdate.Tests;

[TestFixture]
public sealed class ControllerCalibrationTests
{
    [TestCase("BandHub Dongle")]
    [TestCase("BandHub PS3 Rock Band Guitar")]
    public void ProfileCapabilitiesAndIdentityPreventWrongDeviceCommands(string productName)
    {
        Assert.That(UsbProfileSupport.MatchesPs3Recovery(0x12ba, 0x0200, "BandHub", productName, "1C8F7372DA90", "AABBCCDDEEFF"), Is.False);
        Assert.That(UsbProfileSupport.MatchesPs3Recovery(0x1209, 0x2882, "BandHub", productName, "1C8F7372DA90", "1C8F7372DA90"), Is.False);
        Assert.That(UsbProfileSupport.For(UsbProfile.PcHid).Management, Is.True);
        Assert.That(UsbProfileSupport.For(UsbProfile.Ps3RockBandGuitar).Management, Is.False);
        Assert.That(UsbProfileSupport.For((UsbProfile)99).Gameplay, Is.False);
        Assert.That(UsbProfileSupport.IsBandHubPs3Identity("BandHub", productName, "1C8F7372DA90"), Is.True);
        Assert.That(UsbProfileSupport.IsBandHubPs3Identity("Harmonix", "Rock Band Guitar", "1C8F7372DA90"), Is.False);
        Assert.That(UsbProfileSupport.IsBandHubPs3Identity("BandHub", productName, ""), Is.False);
    }
    [Test]
    public void DefaultWireRecordMatchesFirmware()
    {
        var record = new ControllerCalibration();
        Assert.That(record.Encode(), Is.EqualTo(new byte[] { 0, 0, 255, 15, 4, 5, 2, 0, 60, 0, 88, 2, 0, 0, 0, 0 }));
        Assert.That(ControllerCalibration.Decode(record.Encode()), Is.EqualTo(record));
        var request = ControllerManagementProtocol.Request(2, 5, 42, record.Encode());
        Assert.That(request, Has.Length.EqualTo(63));
        Assert.That(request[..12], Is.EqualTo(new byte[] { 0x42, 0x48, 0x43, 0x46, 1, 2, 0, 5, 42, 0, 0, 0 }));
        var feature = new byte[64]; feature[0] = 0x31; request.CopyTo(feature, 1);
        Assert.That(ControllerManagementProtocol.Response(feature, 2, 42, 5), Is.EqualTo(request));
        Assert.Throws<ManagementPendingException>(() => ControllerManagementProtocol.Response(feature, 2, 43));
        feature[20] ^= 1;
        Assert.Throws<InvalidDataException>(() => ControllerManagementProtocol.Response(feature, 2, 42));
    }
    [Test]
    public void ImportRejectsWrongAssemblySchemaAndUnsafeRanges()
    {
        var record = new ControllerCalibration { WhammyMin = 100, WhammyMax = 2900, Inverted = true };
        var json = new CalibrationFile(1, 5, record).Export();
        Assert.That(CalibrationFile.Import(json, 5).Calibration, Is.EqualTo(record));
        Assert.Throws<InvalidDataException>(() => CalibrationFile.Import(json, 3));
        Assert.Throws<InvalidDataException>(() => CalibrationFile.Import(new CalibrationFile(2, 5, record).Export(), 5));
        Assert.Throws<InvalidDataException>(() => (record with { WhammyMax = 101 }).Encode());
        Assert.Throws<InvalidDataException>(() => (record with { ReleaseMs = 0 }).Encode());
        Assert.Throws<InvalidDataException>(() => (record with { ConnectedSleepSeconds = 10 }).Encode());
        var sleepFile = new CalibrationFile(1, 5, record with { SleepEnabled = true }).Export();
        Assert.Throws<InvalidDataException>(() => CalibrationFile.Import(sleepFile, 5, false));
        Assert.That(CalibrationFile.Import(json, 5, false).Calibration, Is.EqualTo(record));
        var wire = record.Encode(); wire[15] = 1;
        Assert.Throws<InvalidDataException>(() => ControllerCalibration.Decode(wire));
    }
}
