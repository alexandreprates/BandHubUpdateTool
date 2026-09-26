using System;
using System.Linq;

namespace BandHub.FirmwareUpdate.Core;

public sealed record UsbProfileSupport(bool Gameplay, bool Battery, bool Management, bool Diagnostics, bool ProfileRecovery)
{
    public static UsbProfileSupport For(UsbProfile profile) => profile switch
    {
        UsbProfile.PcHid => new(true, true, true, true, true),
        UsbProfile.Xbox360GuitarHero => new(true, false, false, false, true),
        UsbProfile.Ps3RockBandGuitar => new(true, false, false, false, true),
        _ => new(false, false, false, false, false),
    };

    public static bool MatchesPs3Recovery(int vendor, int product, string manufacturer, string name, string serial, string expectedSerial) =>
        vendor == OtaProtocol.Ps3VendorId && product == OtaProtocol.Ps3ProductId &&
        IsBandHubPs3Identity(manufacturer, name, serial) &&
        string.Equals(serial, expectedSerial, StringComparison.OrdinalIgnoreCase);

    // VID/PID are shared with real Rock Band hardware; they are not an identity.
    public static bool IsBandHubPs3Identity(string manufacturer, string product, string serial) =>
        manufacturer == "BandHub" && product == "BandHub PS3 Rock Band Guitar" &&
        serial.Length == 12 && serial.All(char.IsAsciiHexDigit);
}
