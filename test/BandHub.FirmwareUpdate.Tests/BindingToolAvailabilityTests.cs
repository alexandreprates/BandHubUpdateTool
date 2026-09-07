using BandHub.FirmwareUpdate.Core;
using NUnit.Framework;

namespace BandHub.FirmwareUpdate.Tests;

[TestFixture]
public sealed class BindingToolAvailabilityTests
{
    [Test]
    public void DongleUnpairIsAvailableWithoutControllerOrSavedBinding()
    {
        var descriptor = Descriptor(Device(flags: 0x00));

        Assert.That(BindingToolAvailability.CanUnpairDongle(descriptor), Is.True);
        Assert.That(BindingToolAvailability.CanUnpairController(descriptor), Is.False);
    }

    [Test]
    public void DongleUnpairRequiresBindingManagementAndPcHid()
    {
        Assert.That(BindingToolAvailability.CanUnpairDongle(
            Descriptor(Device(capabilities: 0x00))), Is.False);
        Assert.That(BindingToolAvailability.CanUnpairDongle(
            Descriptor(Device(), UsbProfile.Ps3RockBandGuitar)), Is.False);
        Assert.That(BindingToolAvailability.CanUnpairDongle(null), Is.False);
    }

    [Test]
    public void ControllerUnpairRetainsRemoteSafetyRequirements()
    {
        Assert.That(BindingToolAvailability.CanUnpairController(
            Descriptor(Device())), Is.True);
        Assert.That(BindingToolAvailability.CanUnpairController(
            Descriptor(Device(flags: 0x01))), Is.False);
        Assert.That(BindingToolAvailability.CanUnpairController(
            Descriptor(Device(controllerFeatures: 0))), Is.False);
    }

    private static DongleDescriptor Descriptor(
        DeviceInfo info,
        UsbProfile profile = UsbProfile.PcHid) => new()
    {
        Path = "dongle",
        UsbProfile = profile,
        DeviceInfo = info,
    };

    private static DeviceInfo Device(
        byte flags = 0x03,
        byte capabilities = 0x08,
        uint controllerFeatures = 1U << 12) => new()
    {
        Capabilities = capabilities,
        UsbProfile = (byte)UsbProfile.PcHid,
        Flags = flags,
        ControllerFeatureFlags = controllerFeatures,
    };
}
