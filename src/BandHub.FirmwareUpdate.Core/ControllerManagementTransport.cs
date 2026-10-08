using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;

namespace BandHub.FirmwareUpdate.Core;

internal interface IControllerManagementTransport : IDisposable
{
    bool Wireless { get; }
    byte? ExpectedProfile { get; }
    Task ValidateAsync(CancellationToken token);
    Task SendAsync(byte[] report, CancellationToken token);
    Task<byte[]> ReadAsync(CancellationToken token);
}

internal sealed class HidControllerManagementTransport : IControllerManagementTransport
{
    private readonly HidDevice device;
    private readonly HidStream stream;
    private readonly ControllerUsbDevice selected;
    public bool Wireless => selected.Dongle != null;
    public byte? ExpectedProfile => selected.Dongle?.DeviceInfo?.ControllerTarget switch
    {
        FirmwareTarget.ControllerGh3SuperMini => 3,
        FirmwareTarget.ControllerGh5SuperMini => 5,
        _ => null,
    };

    public HidControllerManagementTransport(ControllerUsbDevice selected)
    {
        this.selected = selected;
        if (!selected.Supported) throw new NotSupportedException(selected.UnsupportedReason);
        device = DeviceList.Local.GetHidDevices().FirstOrDefault(candidate => candidate.DevicePath == selected.Path)
            ?? throw new IOException("The selected device disconnected. Refresh the list.");
        stream = device.Open(); stream.ReadTimeout = 500; stream.WriteTimeout = 500;
    }

    public Task ValidateAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (!Wireless) return;
        var expected = selected.Dongle!;
        if (device.VendorID != OtaProtocol.VendorId || device.ProductID != OtaProtocol.ProductId ||
            device.GetManufacturer() != "BandHub" ||
            !string.Equals(device.GetSerialNumber(), expected.SerialNumber, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Dongle USB identity changed. Refresh the device list.");
        ControllerManagement.ValidateWirelessIdentity(expected.DeviceInfo!, OtaProtocol.ParseDeviceInfo(ReadFeature(OtaProtocol.DeviceInfoReportId)));
        ControllerManagement.ValidateWirelessState(OtaProtocol.ParseStatus(ReadFeature(OtaProtocol.StatusReportId)).State);
    }, token);

    public Task SendAsync(byte[] report, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var output = new byte[Math.Max(device.GetMaxOutputReportLength(), report.Length + 1)];
        output[0] = ControllerManagementProtocol.CommandId; report.CopyTo(output, 1); stream.Write(output);
    }, token);

    public Task<byte[]> ReadAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        return ReadFeature(ControllerManagementProtocol.ResponseId);
    }, token);

    private byte[] ReadFeature(byte id)
    {
        if (OperatingSystem.IsLinux()) return LinuxHidFeatureReport.Read(device.DevicePath, id, 64);
        var report = new byte[Math.Max(64, device.GetMaxFeatureReportLength())]; report[0] = id;
        stream.GetFeature(report);
        return report.Length == 64 ? report : report[..64];
    }
    public void Dispose() => stream.Dispose();
}
