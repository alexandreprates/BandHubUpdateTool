using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;

namespace BandHub.FirmwareUpdate.Core;

public sealed class DongleDescriptor
{
    public string Path { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool Supported { get; init; }
    public string UnsupportedReason { get; init; } = string.Empty;
    public DeviceInfo? DeviceInfo { get; init; }
    public UsbProfile UsbProfile { get; init; }
    public string SerialNumber { get; init; } = string.Empty;
    public bool CanSwitchUsbProfile { get; init; }
    public bool CanEnterBootloader { get; init; }
}

public static class DongleDescriptorSelection
{
    public static int FindByMac(
        IReadOnlyList<DongleDescriptor> descriptors,
        byte[]? mac)
    {
        if (mac == null)
        {
            return -1;
        }
        for (var index = 0; index < descriptors.Count; ++index)
        {
            if (descriptors[index].DeviceInfo?.DongleMac.SequenceEqual(mac) == true)
            {
                return index;
            }
        }
        return -1;
    }

    public static int FindByIdentity(
        IReadOnlyList<DongleDescriptor> descriptors,
        string? serialNumber,
        byte[]? mac)
    {
        if (!string.IsNullOrWhiteSpace(serialNumber))
        {
            for (var index = 0; index < descriptors.Count; ++index)
            {
                if (string.Equals(
                        descriptors[index].SerialNumber,
                        serialNumber,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
        }
        return FindByMac(descriptors, mac);
    }
}

public interface IDongleTransport : IDisposable
{
    DongleDescriptor Descriptor { get; }
    Task<DeviceInfo> ReadDeviceInfoAsync(CancellationToken cancellationToken);
    Task<OtaStatus> ReadStatusAsync(CancellationToken cancellationToken);
    Task SendCommandAsync(byte[] report, CancellationToken cancellationToken);
}

public interface IDongleDiscovery
{
    Task<IReadOnlyList<DongleDescriptor>> DiscoverAsync(CancellationToken cancellationToken);
    Task<IDongleTransport> OpenAsync(string path, CancellationToken cancellationToken);
    Task<IDongleTransport> WaitForMacAsync(
        byte[] mac,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class HidDongleDiscovery : IDongleDiscovery
{
    public Task<IReadOnlyList<DongleDescriptor>> DiscoverAsync(
        CancellationToken cancellationToken) => Task.Run<IReadOnlyList<DongleDescriptor>>(() =>
    {
        var results = new List<DongleDescriptor>();
        foreach (var device in DeviceList.Local.GetHidDevices(OtaProtocol.VendorId, OtaProtocol.ProductId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var serialNumber = ReadSerialNumber(device);
            try
            {
                if (device.GetProductName().StartsWith("BandHub Xbox 360", StringComparison.Ordinal)) continue;
                using var transport = OpenDevice(device);
                var info = transport.ReadDeviceInfoAsync(cancellationToken).GetAwaiter().GetResult();
                var profile = ParseUsbProfile(info.UsbProfile);
                results.Add(new DongleDescriptor
                {
                    Path = device.DevicePath,
                    DisplayName = $"BandHub Dongle {FormatMac(info.DongleMac)}",
                    Supported = UsbProfileSupport.For(profile).Management && info.SupportsDongleSelfOta,
                    UnsupportedReason = profile == 0
                        ? "The installed firmware reported an unrecognized USB profile."
                        : !info.SupportsDongleSelfOta
                            ? "The installed firmware does not contain the self-OTA agent."
                            : profile != UsbProfile.PcHid
                                ? "Switch this Dongle to the PC HID profile before installing firmware updates."
                                : string.Empty,
                    DeviceInfo = info,
                    UsbProfile = profile,
                    SerialNumber = serialNumber,
                    CanSwitchUsbProfile = info.SupportsUsbProfileSwitch,
                    CanEnterBootloader = info.SupportsUsbBootloader,
                });
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                results.Add(new DongleDescriptor
                {
                    Path = device.DevicePath,
                    DisplayName = "BandHub Dongle (legacy firmware)",
                    Supported = false,
                    UnsupportedReason = error.Message,
                    UsbProfile = UsbProfile.PcHid,
                    SerialNumber = serialNumber,
                });
            }
        }

        foreach (var device in DeviceList.Local.GetHidDevices(
                     OtaProtocol.Ps3VendorId, OtaProtocol.Ps3ProductId))
        {
            var serialNumber = ReadSerialNumber(device);
            try
            {
                if (!UsbProfileSupport.IsBandHubPs3Identity(device.GetManufacturer(), device.GetProductName(), serialNumber)) continue;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            results.Add(new DongleDescriptor
            {
                Path = device.DevicePath,
                DisplayName = string.IsNullOrWhiteSpace(serialNumber)
                    ? "BandHub PS3 Rock Band Guitar Dongle"
                    : $"BandHub PS3 Rock Band Guitar Dongle {serialNumber}",
                Supported = false,
                UnsupportedReason =
                    "Switch this Dongle to the PC HID profile before installing firmware updates.",
                UsbProfile = UsbProfile.Ps3RockBandGuitar,
                SerialNumber = serialNumber,
                CanSwitchUsbProfile = true,
            });
        }
        foreach (var device in UsbProfileManagement.DiscoverAsync(cancellationToken).GetAwaiter().GetResult().Where(x => x.Role == 2))
            results.Add(new DongleDescriptor { Path = device.Path, SerialNumber = device.Serial,
                DisplayName = $"BandHub Xbox 360 Dongle {device.Serial}", UsbProfile = device.Profile,
                Supported = false, CanSwitchUsbProfile = true, CanEnterBootloader = device.CanEnterBootloader,
                UnsupportedReason = "Return to PC HID before updating firmware." });
        return results;
    }, cancellationToken);

    public Task<IDongleTransport> OpenAsync(
        string path,
        CancellationToken cancellationToken) => Task.Run<IDongleTransport>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var device = DeviceList.Local.GetHidDevices().FirstOrDefault(item => item.DevicePath == path)
            ?? throw new InvalidOperationException("The selected Dongle is no longer connected.");
        return OpenDevice(device);
    }, cancellationToken);

    public async Task<IDongleTransport> WaitForMacAsync(
        byte[] mac,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptors = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
            var match = descriptors.FirstOrDefault(item =>
                item.Supported && item.DeviceInfo != null && item.DeviceInfo.DongleMac.SequenceEqual(mac));
            if (match != null)
            {
                return await OpenAsync(match.Path, cancellationToken).ConfigureAwait(false);
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("The updated Dongle did not reconnect over USB.");
    }

    public async Task<DongleDescriptor> SwitchUsbProfileAsync(
        DongleDescriptor descriptor,
        UsbProfile targetProfile,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (targetProfile != UsbProfile.PcHid &&
            targetProfile != UsbProfile.Ps3RockBandGuitar && targetProfile != UsbProfile.Xbox360GuitarHero)
        {
            throw new ArgumentOutOfRangeException(nameof(targetProfile));
        }
        if (descriptor.UsbProfile == targetProfile)
        {
            return descriptor;
        }
        if (!descriptor.CanSwitchUsbProfile)
        {
            throw new NotSupportedException(
                "The installed Dongle firmware does not support USB profile selection.");
        }
        if (string.IsNullOrWhiteSpace(descriptor.SerialNumber))
        {
            throw new InvalidOperationException(
                "The Dongle USB serial could not be read, so it cannot be followed safely " +
                "across a profile change. Reconnect it and refresh the device list.");
        }

        if (descriptor.UsbProfile == UsbProfile.PcHid)
        {
            if (targetProfile != UsbProfile.Ps3RockBandGuitar &&
                !(targetProfile == UsbProfile.Xbox360GuitarHero && descriptor.DeviceInfo?.SupportsXbox360Profile == true))
            {
                throw new NotSupportedException("The requested USB profile transition is invalid.");
            }
            using (var transport = await OpenAsync(descriptor.Path, cancellationToken)
                       .ConfigureAwait(false))
            {
                var info = await transport.ReadDeviceInfoAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(transport.Descriptor.SerialNumber, descriptor.SerialNumber, StringComparison.OrdinalIgnoreCase) || info.UsbProfile != 1 ||
                    !info.SupportsUsbProfileSwitch || (targetProfile == UsbProfile.Xbox360GuitarHero && !info.SupportsXbox360Profile))
                    throw new InvalidOperationException("Dongle identity or capabilities changed. Refresh the device list.");
                var sessionId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                await transport.SendCommandAsync(
                    OtaProtocol.EncodeUsbProfileCommand(sessionId, targetProfile),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else if (descriptor.UsbProfile == UsbProfile.Xbox360GuitarHero)
        {
            await UsbProfileManagement.SendProfileAsync(new(descriptor.Path, descriptor.SerialNumber, 2, descriptor.UsbProfile, 0x0e), targetProfile, cancellationToken).ConfigureAwait(false);
        }
        else if (descriptor.UsbProfile == UsbProfile.Ps3RockBandGuitar)
        {
            if (targetProfile != UsbProfile.PcHid)
            {
                throw new NotSupportedException("The requested USB profile transition is invalid.");
            }
            await SendPs3ReturnToPcAsync(descriptor.Path, descriptor.SerialNumber, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException("The current USB profile is not recognized.");
        }

        return await WaitForProfileAsync(
            descriptor.SerialNumber,
            descriptor.DeviceInfo?.DongleMac,
            targetProfile,
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static HidDongleTransport OpenDevice(HidDevice device)
    {
        if (!device.TryOpen(out HidStream stream))
        {
            throw new InvalidOperationException(
                "Unable to open the Dongle HID interface. Check permissions and other applications.");
        }
        stream.ReadTimeout = 2000;
        stream.WriteTimeout = 2000;
        return new HidDongleTransport(device, stream);
    }

    private async Task<DongleDescriptor> WaitForProfileAsync(
        string serialNumber,
        byte[]? mac,
        UsbProfile targetProfile,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptors = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
            var candidates = descriptors.Where(item => item.UsbProfile == targetProfile).ToList();
            var selected = candidates.FindIndex(item =>
                string.Equals(item.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));
            if (selected >= 0)
            {
                return candidates[selected];
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException(
            "The Dongle did not reconnect with the requested USB profile. " +
            "Unplug it, reconnect it, and try again.");
    }

    private static Task SendPs3ReturnToPcAsync(
        string path, string expectedSerial,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var device = DeviceList.Local.GetHidDevices().FirstOrDefault(
            item => item.DevicePath == path)
            ?? throw new InvalidOperationException(
                "The selected Dongle is no longer connected.");
        if (!UsbProfileSupport.MatchesPs3Recovery(device.VendorID, device.ProductID,
                device.GetManufacturer(), device.GetProductName(), ReadSerialNumber(device), expectedSerial))
            throw new InvalidOperationException("The selected PS3 Dongle identity changed. Refresh the device list.");
        if (!device.TryOpen(out HidStream stream))
        {
            throw new InvalidOperationException(
                "Unable to open the Dongle HID interface. Check permissions and other applications.");
        }
        using (stream)
        {
            stream.WriteTimeout = 2000;
            var outputLength = Math.Max(device.GetMaxOutputReportLength(), 9);
            stream.Write(OtaProtocol.EncodePs3ReturnToPcOutput(outputLength));
        }
    }, cancellationToken);

    private static UsbProfile ParseUsbProfile(byte value) => value switch
    {
        (byte)UsbProfile.PcHid => UsbProfile.PcHid,
        (byte)UsbProfile.Ps3RockBandGuitar => UsbProfile.Ps3RockBandGuitar,
        (byte)UsbProfile.Xbox360GuitarHero => UsbProfile.Xbox360GuitarHero,
        _ => (UsbProfile)0,
    };

    private static string ReadSerialNumber(HidDevice device)
    {
        try
        {
            return device.GetSerialNumber() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatMac(byte[] mac) =>
        string.Join(":", mac.Select(value => value.ToString("X2")));
}

internal sealed class HidDongleTransport : IDongleTransport
{
    private readonly HidDevice device;
    private readonly HidStream stream;
    private readonly object sync = new();

    public HidDongleTransport(HidDevice device, HidStream stream)
    {
        this.device = device;
        this.stream = stream;
        Descriptor = new DongleDescriptor
        {
            Path = device.DevicePath,
            DisplayName = "BandHub Dongle",
            SerialNumber = device.GetSerialNumber(),
            Supported = true,
        };
    }

    public DongleDescriptor Descriptor { get; }

    public Task<DeviceInfo> ReadDeviceInfoAsync(CancellationToken cancellationToken) =>
        Task.Run(() => OtaProtocol.ParseDeviceInfo(ReadFeature(OtaProtocol.DeviceInfoReportId)),
                 cancellationToken);

    public Task<OtaStatus> ReadStatusAsync(CancellationToken cancellationToken) =>
        Task.Run(() => OtaProtocol.ParseStatus(ReadFeature(OtaProtocol.StatusReportId)),
                 cancellationToken);

    public Task SendCommandAsync(byte[] report, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (report.Length != OtaProtocol.ReportBytes)
            {
                throw new ArgumentException("OTA command report has an invalid length.", nameof(report));
            }
            var output = new byte[Math.Max(device.GetMaxOutputReportLength(), report.Length + 1)];
            output[0] = OtaProtocol.CommandReportId;
            Buffer.BlockCopy(report, 0, output, 1, report.Length);
            lock (sync)
            {
                stream.Write(output);
            }
        }, cancellationToken);

    public void Dispose() => stream.Dispose();

    private byte[] ReadFeature(byte reportId)
    {
        if (OperatingSystem.IsLinux())
        {
            return LinuxHidFeatureReport.Read(
                device.DevicePath, reportId, OtaProtocol.ReportBytes + 1);
        }
        var buffer = new byte[Math.Max(device.GetMaxFeatureReportLength(), OtaProtocol.ReportBytes + 1)];
        buffer[0] = reportId;
        lock (sync)
        {
            stream.GetFeature(buffer);
        }
        if (buffer.Length == OtaProtocol.ReportBytes + 1)
        {
            return buffer;
        }
        var report = new byte[OtaProtocol.ReportBytes + 1];
        Buffer.BlockCopy(buffer, 0, report, 0, report.Length);
        return report;
    }
}
