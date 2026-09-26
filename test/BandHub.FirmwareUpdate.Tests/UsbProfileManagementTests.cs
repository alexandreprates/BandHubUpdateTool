using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BandHub.FirmwareUpdate.Core;
using NUnit.Framework;

namespace BandHub.FirmwareUpdate.Tests;

public sealed class UsbProfileManagementTests
{
    [TestCase(1, "BandHub Xbox 360 Guitar")]
    [TestCase(2, "BandHub Xbox 360 Dongle")]
    public void XboxIdentityRequiresRoleProductManufacturerAndSerial(byte role, string product)
    {
        Assert.That(UsbProfileManagement.MatchesIdentity(0x1209, 0x2882, "BandHub", product, "AABBCCDDEEFF", role, UsbProfile.Xbox360GuitarHero), Is.True);
        Assert.That(UsbProfileManagement.MatchesIdentity(0x1209, 0x2882, "Other", product, "AABBCCDDEEFF", role, UsbProfile.Xbox360GuitarHero), Is.False);
        Assert.That(UsbProfileManagement.MatchesIdentity(0x1209, 0x2882, "BandHub", product, "", role, UsbProfile.Xbox360GuitarHero), Is.False);
        Assert.That(UsbProfileManagement.MatchesIdentity(0x303a, 0x1001, "BandHub", product, "AABBCCDDEEFF", role, UsbProfile.Xbox360GuitarHero), Is.False);
        Assert.That(UsbProfileManagement.MatchesIdentity(0x1209, 0x2882, "BandHub", product, "AABBCCDDEEFF", (byte)(3-role), UsbProfile.Xbox360GuitarHero), Is.False);
    }
    [Test]
    public void ProfileCapabilitiesDoNotConfuseHardwarePresetWithUsbProfile()
    {
        var reply = new byte[63]; reply[12]=1; reply[13]=1; reply[14]=3; reply[15]=10;
        var device = UsbProfileManagement.ParseInfo("path", "AABBCCDDEEFF", reply, 1, UsbProfile.Xbox360GuitarHero);
        Assert.That(device.Supports(UsbProfile.PcHid), Is.True);
        Assert.That(device.Supports(UsbProfile.Xbox360GuitarHero), Is.True);
        Assert.That(device.Supports(UsbProfile.Ps3RockBandGuitar), Is.False);
        Assert.Throws<InvalidDataException>(() => UsbProfileManagement.ParseInfo("path", "AABBCCDDEEFF", reply, 2, UsbProfile.Xbox360GuitarHero));
        reply[14]=5;
        Assert.Throws<InvalidDataException>(() => UsbProfileManagement.ParseInfo("path", "AABBCCDDEEFF", reply, 1, UsbProfile.Xbox360GuitarHero));
    }
    [Test]
    public void XboxOtaProfileEncodingPreservesExistingCommandContract()
    {
        var report = OtaProtocol.EncodeUsbProfileCommand(42, UsbProfile.Xbox360GuitarHero);
        Assert.That(report[3], Is.EqualTo((byte)OtaCommand.SetUsbProfile));
        Assert.That(report[17], Is.EqualTo(3));
        Assert.That(new DeviceInfo { Capabilities = 4 }.SupportsXbox360Profile, Is.False);
        Assert.That(new DeviceInfo { Capabilities = 0x24 }.SupportsXbox360Profile, Is.True);
        Assert.That(UsbProfileSupport.For(UsbProfile.Xbox360GuitarHero).Management, Is.False);
        Assert.That(UsbProfileSupport.For(UsbProfile.Xbox360GuitarHero).ProfileRecovery, Is.True);
    }
    [Test]
    public void OlderOrMalformedCapabilitiesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => UsbProfileManagement.ParseInfo("path", "AABBCCDDEEFF", new byte[63], 1, UsbProfile.PcHid));
        Assert.Throws<InvalidDataException>(() => UsbProfileManagement.ParseInfo("path", "AABBCCDDEEFF", new byte[1], 1, UsbProfile.PcHid));
    }
    [Test]
    public async Task ReconnectRequiresMatchingSerialRoleAndProfile()
    {
        IReadOnlyList<UsbProfileDevice> devices = new[] {
            new UsbProfileDevice("other", "112233445566", 1, UsbProfile.Xbox360GuitarHero, 10),
            new UsbProfileDevice("wrong-role", "AABBCCDDEEFF", 2, UsbProfile.Xbox360GuitarHero, 14),
            new UsbProfileDevice("old-mode", "AABBCCDDEEFF", 1, UsbProfile.PcHid, 10),
            new UsbProfileDevice("correct", "aabbccddeeff", 1, UsbProfile.Xbox360GuitarHero, 10),
        };
        var match = await UsbProfileManagement.WaitAsync("AABBCCDDEEFF", 1, UsbProfile.Xbox360GuitarHero,
            TimeSpan.FromSeconds(1), CancellationToken.None, _ => Task.FromResult(devices));
        Assert.That(match.Path, Is.EqualTo("correct"));
        IReadOnlyList<UsbProfileDevice> wrongSerial = new[] { devices[0] };
        Assert.ThrowsAsync<TimeoutException>(async () => await UsbProfileManagement.WaitAsync("AABBCCDDEEFF", 1,
            UsbProfile.Xbox360GuitarHero, TimeSpan.FromMilliseconds(1), CancellationToken.None, _ => Task.FromResult(wrongSerial)));
    }
    [Test]
    public void CancelledReconnectDoesNotProbeHardware()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await UsbProfileManagement.WaitAsync("AABBCCDDEEFF", 1,
            UsbProfile.PcHid, TimeSpan.FromSeconds(30), cancellation.Token, _ => throw new AssertionException("Unexpected discovery")));
    }
    [Test]
    public void XboxCannotSwitchDirectlyToPs3()
    {
        var device = new UsbProfileDevice("absent", "AABBCCDDEEFF", 2, UsbProfile.Xbox360GuitarHero, 14);
        Assert.ThrowsAsync<NotSupportedException>(async () => await UsbProfileManagement.SendProfileAsync(device, UsbProfile.Ps3RockBandGuitar, CancellationToken.None));
    }
}
