using System;
using System.IO;
using BandHub.FirmwareUpdate.Core;
using NUnit.Framework;

namespace BandHub.FirmwareUpdate.Tests;

[TestFixture]
public sealed class LinuxHidFeatureReportTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(63)]
    [TestCase(65)]
    public void ShortOrOversizedResponseCannotBeDecodedAsAFullReport(int bytesRead)
    {
        var buffer = new byte[64];
        buffer[0] = 0x41;
        Assert.Throws<InvalidDataException>(() =>
            LinuxHidFeatureReport.ValidateResponse(buffer, bytesRead, 0x41));
    }

    [Test]
    public void UnexpectedReportIdIsRejectedWithDiagnosticContext()
    {
        var buffer = new byte[64];
        buffer[0] = 0x01;
        var error = Assert.Throws<InvalidDataException>(() =>
            LinuxHidFeatureReport.ValidateResponse(buffer, 64, 0x41));
        Assert.That(error!.Message, Does.Contain("0x41").And.Contain("64/64").And.Contain("01000000"));
    }

    [Test]
    public void CompleteExpectedReportIsAccepted()
    {
        var buffer = new byte[64];
        buffer[0] = 0x41;
        Assert.DoesNotThrow(() => LinuxHidFeatureReport.ValidateResponse(buffer, 64, 0x41));
    }

    [Test]
    public void BuildGetFeatureRequestMatchesLinuxHidrawAbi()
    {
        Assert.That(
            LinuxHidFeatureReport.BuildGetFeatureRequest(64),
            Is.EqualTo((nuint)0xC0404807));
    }

    [TestCase("/dev/hidraw7")]
    [TestCase("/sys/devices/usb/hidraw/hidraw7")]
    public void ResolveDevicePathUsesValidatedHidrawName(string devicePath)
    {
        Assert.That(
            LinuxHidFeatureReport.ResolveDevicePath(devicePath),
            Is.EqualTo("/dev/hidraw7"));
    }

    [Test]
    public void ResolveDevicePathRejectsUnexpectedDeviceName()
    {
        Assert.Throws<InvalidOperationException>(() =>
            LinuxHidFeatureReport.ResolveDevicePath("/dev/ttyACM0"));
    }
}
