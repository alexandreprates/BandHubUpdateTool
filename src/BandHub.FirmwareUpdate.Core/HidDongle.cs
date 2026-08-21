using System;
using System.Collections.Generic;
using System.Linq;
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
            try
            {
                using var transport = OpenDevice(device);
                var info = transport.ReadDeviceInfoAsync(cancellationToken).GetAwaiter().GetResult();
                results.Add(new DongleDescriptor
                {
                    Path = device.DevicePath,
                    DisplayName = $"BandHub Dongle {FormatMac(info.DongleMac)}",
                    Supported = info.UsbProfile == 1 && info.SupportsDongleSelfOta,
                    UnsupportedReason = info.SupportsDongleSelfOta
                        ? string.Empty
                        : "The installed firmware does not contain the self-OTA agent.",
                    DeviceInfo = info,
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
                });
            }
        }

        foreach (var device in DeviceList.Local.GetHidDevices(
                     OtaProtocol.Ps3VendorId, OtaProtocol.Ps3ProductId))
        {
            results.Add(new DongleDescriptor
            {
                Path = device.DevicePath,
                DisplayName = "BandHub PS3 Rock Band Guitar Dongle",
                Supported = false,
                UnsupportedReason = "PS3 USB profiles are not supported by updater version 1.",
            });
        }
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
