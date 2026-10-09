using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;

namespace BandHub.FirmwareUpdate.Core;

public sealed record ControllerUpdateDescriptor(string Path, string Serial, string Name,
    ControllerUpdateInfo? Info, string UnsupportedReason)
{
    public bool Supported => Info?.SupportsUpdate == true && Info.Health == ControllerImageHealth.Healthy;
    public string DisplayName => Name == "TinyUSB HID" ? Info?.Target switch
    {
        FirmwareTarget.ControllerGh3SuperMini => "BandHub GH3 SuperMini",
        FirmwareTarget.ControllerGh5SuperMini => "BandHub GH5 SuperMini",
        _ => "BandHub Controller (TinyUSB HID)",
    } : Name;
}

public interface IControllerUsbDiscovery
{
    Task<IReadOnlyList<ControllerUpdateDescriptor>> DiscoverAsync(CancellationToken token);
    Task<IControllerUsbTransport> OpenAsync(ControllerUpdateDescriptor selected, CancellationToken token);
}

public interface IControllerUsbTransport : IDisposable
{
    Task<ControllerUpdateInfo> ReadInfoAsync(CancellationToken token);
    Task<ControllerUpdateStatus> ReadStatusAsync(CancellationToken token);
    Task SendAsync(byte[] report, CancellationToken token);
}

public sealed class ControllerUsbDiscovery : IControllerUsbDiscovery
{
    public Task<IReadOnlyList<ControllerUpdateDescriptor>> DiscoverAsync(CancellationToken token) =>
        Task.Run<IReadOnlyList<ControllerUpdateDescriptor>>(async () =>
        {
            var found = new List<ControllerUpdateDescriptor>();
            foreach (var device in DeviceList.Local.GetHidDevices())
            {
                token.ThrowIfCancellationRequested();
                if (device.VendorID is not (0x303a or 0x1209)) continue;
                try
                {
                    var serial = device.GetSerialNumber();
                    var name = device.GetProductName();
                    var profile = UsbProfileManagement.MatchesIdentity(device.VendorID, device.ProductID,
                        device.GetManufacturer(), name, serial, 1, UsbProfile.Xbox360GuitarHero)
                        ? UsbProfile.Xbox360GuitarHero : UsbProfile.PcHid;
                    if (!UsbProfileManagement.MatchesIdentity(device.VendorID, device.ProductID,
                            device.GetManufacturer(), name, serial, 1, profile) ||
                        !ControllerUsbUpdateProtocol.ValidSerial(serial)) continue;
                    var selected = new ControllerUpdateDescriptor(device.DevicePath, serial, name, null,
                        "Install a compatible Controller firmware once using an external USB flashing tool.");
                    if (profile != UsbProfile.PcHid)
                    {
                        found.Add(selected with { UnsupportedReason = "Return to PC Dongle using USB profiles before updating." });
                        continue;
                    }
                    try
                    {
                        using var transport = await OpenAsync(selected, token).ConfigureAwait(false);
                        var info = await transport.ReadInfoAsync(token).ConfigureAwait(false);
                        ValidateIdentity(selected, info);
                        found.Add(selected with { Info = info, UnsupportedReason = !info.SupportsUpdate
                            ? "The installed firmware cannot accept signed USB updates."
                            : info.Health != ControllerImageHealth.Healthy
                                ? "Controller image health is pending or failed. Wait and refresh." : string.Empty });
                    }
                    catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException)
                    {
                        found.Add(selected with { UnsupportedReason = selected.UnsupportedReason + " " + error.Message });
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
            return found;
        }, token);

    public Task<IControllerUsbTransport> OpenAsync(ControllerUpdateDescriptor selected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var device = DeviceList.Local.GetHidDevices().SingleOrDefault(d => d.DevicePath == selected.Path)
            ?? throw new IOException("The selected USB Controller disconnected.");
        if (device.GetSerialNumber() != selected.Serial || device.GetProductName() != selected.Name ||
            !UsbProfileManagement.MatchesIdentity(device.VendorID, device.ProductID, device.GetManufacturer(),
                device.GetProductName(), device.GetSerialNumber(), 1, UsbProfile.PcHid))
            throw new IOException("The selected USB Controller identity changed. Refresh the device list.");
        return Task.FromResult<IControllerUsbTransport>(new HidControllerUsbTransport(device));
    }

    internal static void ValidateIdentity(ControllerUpdateDescriptor selected, ControllerUpdateInfo info)
    {
        // Some Controller firmware retains TinyUSB's default product string.
        // Obtain its model from the validated info report instead of guessing GH5.
        var expected = selected.Name switch
        {
            "BandHub Guitar GH3" or "BandHub GH3 SuperMini" => FirmwareTarget.ControllerGh3SuperMini,
            "BandHub Guitar GH5" or "BandHub GH5 SuperMini" => FirmwareTarget.ControllerGh5SuperMini,
            "TinyUSB HID" when OtaProtocol.IsSuperMiniTarget(info.Target) => info.Target,
            _ => (FirmwareTarget)0,
        };
        if (expected == 0 || info.Serial != selected.Serial || info.Target != expected ||
            (selected.Info != null && selected.Info.Target != info.Target))
            throw new IOException("USB Controller identity does not match its management reports.");
    }
}

internal sealed class HidControllerUsbTransport : IControllerUsbTransport
{
    private readonly HidDevice device;
    private readonly HidStream stream;
    public HidControllerUsbTransport(HidDevice device)
    {
        this.device = device;
        stream = device.Open();
        stream.ReadTimeout = 1000;
        stream.WriteTimeout = 1000;
    }
    public async Task<ControllerUpdateInfo> ReadInfoAsync(CancellationToken token) =>
        ControllerUsbUpdateProtocol.DecodeInfo(await ReadAsync(ControllerUsbUpdateProtocol.InfoReportId, token).ConfigureAwait(false));
    public async Task<ControllerUpdateStatus> ReadStatusAsync(CancellationToken token) =>
        ControllerUsbUpdateProtocol.DecodeStatus(await ReadAsync(ControllerUsbUpdateProtocol.StatusReportId, token).ConfigureAwait(false));
    private Task<byte[]> ReadAsync(byte id, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var report = new byte[64]; report[0] = id;
        if (OperatingSystem.IsLinux()) return LinuxHidFeatureReport.Read(device.DevicePath, id, 64);
        stream.GetFeature(report);
        return report;
    }, token);
    public Task SendAsync(byte[] report, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var output = new byte[Math.Max(report.Length, device.GetMaxOutputReportLength())];
        report.CopyTo(output, 0);
        stream.Write(output);
    }, token);
    public void Dispose() => stream.Dispose();
}
