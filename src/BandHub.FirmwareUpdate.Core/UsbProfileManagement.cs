using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;

namespace BandHub.FirmwareUpdate.Core;

public sealed record UsbProfileDevice(string Path, string Serial, byte Role, UsbProfile Profile, byte SupportedProfiles, bool CanEnterBootloader = false)
{
    public bool Supports(UsbProfile target) => (byte)target is >= 1 and <= 3 && (SupportedProfiles & (1 << (byte)target)) != 0;
}

public static class UsbProfileManagement
{
    public static bool MatchesIdentity(int vendor, int product, string manufacturer, string name, string serial, byte role, UsbProfile profile)
    {
        if (manufacturer != "BandHub" || serial.Length != 12 || !serial.All(char.IsAsciiHexDigit)) return false;
        if (profile == UsbProfile.Xbox360GuitarHero)
            return vendor == 0x1209 && product == 0x2882 &&
                name == (role == 1 ? "BandHub Xbox 360 Guitar" : role == 2 ? "BandHub Xbox 360 Dongle" : "");
        // Released Controller firmware can retain TinyUSB's default product name.
        // Callers must still validate the role/model in the management reports.
        return role == 1 && profile == UsbProfile.PcHid && vendor == 0x303a && product == 0x1001 &&
            name is "BandHub GH3 SuperMini" or "BandHub GH5 SuperMini" or "TinyUSB HID";
    }

    public static UsbProfileDevice ParseInfo(string path, string serial, byte[] response, byte expectedRole, UsbProfile expectedProfile)
    {
        if (response.Length != 63 || response[12] != 1 || response[13] != expectedRole ||
            response[14] != (byte)expectedProfile || (response[15] & (1 << (byte)expectedProfile)) == 0 ||
            (response[15] & ~0x0e) != 0 || (response[15] & 2) == 0 ||
            (expectedRole == 1 && (response[15] & 4) != 0))
            throw new InvalidDataException("Unsupported USB profile capabilities.");
        return new(path, serial, expectedRole, expectedProfile, response[15], (response[22] & 1) != 0);
    }

    public static async Task<IReadOnlyList<UsbProfileDevice>> DiscoverAsync(CancellationToken token)
    {
        var result = new List<UsbProfileDevice>();
        foreach (var device in DeviceList.Local.GetHidDevices())
        {
            token.ThrowIfCancellationRequested();
            if (device.VendorID != 0x303a && device.VendorID != 0x1209) continue;
            try
            {
                var name = device.GetProductName(); var serial = device.GetSerialNumber();
                var profile = name.StartsWith("BandHub Xbox 360", StringComparison.Ordinal) ? UsbProfile.Xbox360GuitarHero : UsbProfile.PcHid;
                byte role = name == "BandHub Xbox 360 Dongle" ? (byte)2 : (byte)1;
                if (!MatchesIdentity(device.VendorID, device.ProductID, device.GetManufacturer(), name, serial, role, profile) ||
                    device.GetMaxFeatureReportLength() < 64 || device.GetMaxOutputReportLength() < 64) continue;
                var reply = await ExchangeAsync(device, 6, null, token).ConfigureAwait(false);
                result.Add(ParseInfo(device.DevicePath, serial, reply, role, profile));
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException) { }
        }
        return result;
    }

    public static async Task EnterBootloaderAsync(UsbProfileDevice descriptor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!descriptor.CanEnterBootloader)
            throw new NotSupportedException("Update this device's firmware before using USB flash mode.");
        var device = DeviceList.Local.GetHidDevices().FirstOrDefault(x => x.DevicePath == descriptor.Path)
            ?? throw new IOException("The selected device disconnected.");
        if (!MatchesIdentity(device.VendorID, device.ProductID, device.GetManufacturer(), device.GetProductName(), device.GetSerialNumber(), descriptor.Role, descriptor.Profile) ||
            !string.Equals(device.GetSerialNumber(), descriptor.Serial, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Device identity changed. Refresh the list.");
        var reply = await ExchangeAsync(device, 6, null, token).ConfigureAwait(false);
        var current = ParseInfo(device.DevicePath, descriptor.Serial, reply, descriptor.Role, descriptor.Profile);
        if (!current.CanEnterBootloader)
            throw new NotSupportedException("The installed firmware does not support USB flash mode.");
        // Acknowledge before disconnecting. A successful write alone is insufficient.
        await ExchangeAsync(device, 8, null, token).ConfigureAwait(false);
    }

    public static async Task SendProfileAsync(UsbProfileDevice descriptor, UsbProfile target, CancellationToken token)
    {
        if (!descriptor.Supports(target) || (descriptor.Profile == UsbProfile.Xbox360GuitarHero && target != UsbProfile.PcHid))
            throw new NotSupportedException("Return to PC HID before selecting another console profile.");
        var device = DeviceList.Local.GetHidDevices().FirstOrDefault(x => x.DevicePath == descriptor.Path)
            ?? throw new IOException("The selected device disconnected.");
        if (!MatchesIdentity(device.VendorID, device.ProductID, device.GetManufacturer(), device.GetProductName(), device.GetSerialNumber(), descriptor.Role, descriptor.Profile) ||
            !string.Equals(device.GetSerialNumber(), descriptor.Serial, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Device identity changed. Refresh the list.");
        var reply = await ExchangeAsync(device, 6, null, token).ConfigureAwait(false);
        var current = ParseInfo(device.DevicePath, descriptor.Serial, reply, descriptor.Role, descriptor.Profile);
        if (!current.Supports(target)) throw new NotSupportedException("The firmware does not support that profile.");
        // Xbox recovery can reboot immediately after enqueueing the request. The
        // confirmed result is enumeration with the same serial and target profile.
        await ExchangeAsync(device, 7, new[] { (byte)target }, token, descriptor.Profile == UsbProfile.Xbox360GuitarHero).ConfigureAwait(false);
    }

    public static async Task<UsbProfileDevice> WaitAsync(string serial, byte role, UsbProfile target, TimeSpan timeout, CancellationToken token,
        Func<CancellationToken, Task<IReadOnlyList<UsbProfileDevice>>>? discover = null)
    {
        token.ThrowIfCancellationRequested();
        discover ??= DiscoverAsync;
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            token.ThrowIfCancellationRequested();
            var found = (await discover(token).ConfigureAwait(false)).FirstOrDefault(x => x.Role == role && x.Profile == target && string.Equals(x.Serial, serial, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found;
            await Task.Delay(250, token).ConfigureAwait(false);
        }
        throw new TimeoutException("The same device did not reconnect in the requested profile. After startup, hold BOOT for five seconds and release to recover PC HID.");
    }

    private static Task<byte[]> ExchangeAsync(HidDevice device, byte operation, byte[]? payload, CancellationToken token, bool writeOnly = false) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        using var stream = device.Open(); stream.ReadTimeout = 500; stream.WriteTimeout = 500;
        var id = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var request = ControllerManagementProtocol.Request(operation, 0, id, payload);
        var output = new byte[Math.Max(64, device.GetMaxOutputReportLength())]; output[0] = 0x30;
        request.CopyTo(output, 1); stream.Write(output);
        if (writeOnly) return Array.Empty<byte>();
        for (var i = 0; i < 10; ++i)
        {
            await Task.Delay(50, token).ConfigureAwait(false);
            var feature = new byte[64]; feature[0] = 0x31;
            if (OperatingSystem.IsLinux()) feature = LinuxHidFeatureReport.Read(device.DevicePath, 0x31, 64);
            else stream.GetFeature(feature);
            try { return ControllerManagementProtocol.Response(feature, operation, id, 0); }
            catch (ManagementPendingException) { }
        }
        throw new TimeoutException("USB profile management timed out.");
    }, token);
}
