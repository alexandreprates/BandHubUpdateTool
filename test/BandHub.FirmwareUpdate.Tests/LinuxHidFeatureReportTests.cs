using System;
using BandHub.FirmwareUpdate.Core;
using NUnit.Framework;

namespace BandHub.FirmwareUpdate.Tests;

[TestFixture]
public sealed class LinuxHidFeatureReportTests
{
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
