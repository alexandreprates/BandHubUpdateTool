namespace BandHub.FirmwareUpdate.Core;

public static class BindingToolAvailability
{
    public static bool CanUnpairController(DongleDescriptor? descriptor)
    {
        var info = descriptor?.DeviceInfo;
        return descriptor?.UsbProfile == UsbProfile.PcHid &&
            info?.SupportsBindingManagement == true &&
            info.SupportsControllerRemoteUnpair &&
            info.ControllerBound &&
            info.ControllerConnected;
    }

    public static bool CanUnpairDongle(DongleDescriptor? descriptor) =>
        descriptor?.UsbProfile == UsbProfile.PcHid &&
        descriptor.DeviceInfo?.SupportsBindingManagement == true;
}
