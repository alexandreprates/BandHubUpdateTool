using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;

namespace BandHub.FirmwareUpdate.Core;

public sealed record ControllerUsbDevice(string Path, string Name);

public sealed class ControllerManagement : IDisposable
{
    private readonly HidDevice device;
    private readonly HidStream stream;
    private readonly SemaphoreSlim gate = new(1, 1);
    private uint requestId;
    public byte Profile { get; private set; }

    public static IReadOnlyList<ControllerUsbDevice> Discover()
    {
        var result = new List<ControllerUsbDevice>();
        foreach (var device in DeviceList.Local.GetHidDevices(0x303a))
        {
            try
            {
                var name = device.GetProductName();
                if (device.GetManufacturer() == "BandHub" && name.StartsWith("BandHub GH", StringComparison.Ordinal) &&
                    device.GetMaxFeatureReportLength() >= 64 && device.GetMaxOutputReportLength() >= 64)
                    result.Add(new(device.DevicePath, $"{name} ({device.GetSerialNumber()})"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    public ControllerManagement(string path)
    {
        device = DeviceList.Local.GetHidDevices().FirstOrDefault(candidate => candidate.DevicePath == path) ?? throw new IOException("Controller disconnected.");
        stream = device.Open();
        stream.ReadTimeout = 500; stream.WriteTimeout = 500;
    }
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var response = await ExchangeAsync(5, null, cancellationToken);
        if (response[12] != 1 || response[13] != 1 || (response[14] & 7) != 7 || response[7] is not (3 or 5))
            throw new InvalidDataException("Unsupported controller management capabilities.");
        Profile = response[7];
    }
    public async Task<ControllerCalibration> ReadAsync(CancellationToken token) =>
        ControllerCalibration.Decode((await ExchangeAsync(1, null, token)).AsSpan(12, 16));
    public async Task<ControllerCalibration> WriteAsync(ControllerCalibration value, CancellationToken token) =>
        ControllerCalibration.Decode((await ExchangeAsync(2, value.Encode(), token)).AsSpan(12, 16));
    public async Task<ControllerCalibration> ResetAsync(CancellationToken token) =>
        ControllerCalibration.Decode((await ExchangeAsync(3, null, token)).AsSpan(12, 16));
    public Task<byte[]> DiagnosticsAsync(CancellationToken token) => ExchangeAsync(4, null, token);

    private async Task<byte[]> ExchangeAsync(byte operation, byte[]? payload, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(async () =>
            {
                var id = ++requestId;
                var data = ControllerManagementProtocol.Request(operation, Profile, id, payload);
                var output = new byte[Math.Max(device.GetMaxOutputReportLength(), 64)];
                output[0] = ControllerManagementProtocol.CommandId;
                data.CopyTo(output, 1);
                token.ThrowIfCancellationRequested();
                stream.Write(output);
                for (var attempt = 0; attempt < 20; ++attempt)
                {
                    await Task.Delay(50, token);
                    var feature = new byte[64]; feature[0] = ControllerManagementProtocol.ResponseId;
                    if (OperatingSystem.IsLinux()) feature = LinuxHidFeatureReport.Read(device.DevicePath, feature[0], feature.Length);
                    else stream.GetFeature(feature);
                    try { return ControllerManagementProtocol.Response(feature, operation, id, Profile == 0 ? null : Profile); }
                    catch (ManagementPendingException) { }
                }
                throw new TimeoutException("Controller management timed out. Check USB connection and firmware.");
            }, token);
        }
        finally { gate.Release(); }
    }
    public void Dispose() { stream.Dispose(); }
}
