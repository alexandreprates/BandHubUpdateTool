using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;

namespace BandHub.FirmwareUpdate.Core;

public sealed record ControllerUsbDevice(string Path, string Name, DongleDescriptor? Dongle = null,
    string UnsupportedReason = "")
{
    public bool Supported => string.IsNullOrEmpty(UnsupportedReason);
}

public sealed class ControllerManagement : IDisposable
{
    private readonly IControllerManagementTransport transport;
    private readonly SemaphoreSlim gate = new(1, 1);
    private uint requestId = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
    public byte Profile { get; private set; }
    public bool CanSleep { get; private set; }

    public static IReadOnlyList<ControllerUsbDevice> Discover()
    {
        var result = new List<ControllerUsbDevice>();
        foreach (var device in DeviceList.Local.GetHidDevices(0x303a))
        {
            try
            {
                var name = device.GetProductName();
                if (UsbProfileManagement.MatchesIdentity(device.VendorID, device.ProductID,
                        device.GetManufacturer(), name, device.GetSerialNumber(), 1, UsbProfile.PcHid) &&
                    device.GetMaxFeatureReportLength() >= 64 && device.GetMaxOutputReportLength() >= 64)
                    result.Add(new(device.DevicePath, $"{(name == "TinyUSB HID" ? "BandHub Controller" : name)} ({device.GetSerialNumber()})"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    public static async Task<IReadOnlyList<ControllerUsbDevice>> DiscoverWirelessAsync(CancellationToken token,
        IDongleDiscovery? discovery = null)
    {
        var found = await (discovery ?? new HidDongleDiscovery()).DiscoverAsync(token).ConfigureAwait(false);
        return found.Select(d => new ControllerUsbDevice(d.Path,
            $"Controller via {d.DisplayName}", d, WirelessUnsupportedReason(d))).ToArray();
    }

    internal static string WirelessUnsupportedReason(DongleDescriptor dongle)
    {
        var info = dongle.DeviceInfo;
        if (dongle.UsbProfile != UsbProfile.PcHid || info?.UsbProfile != 1)
            return "Return the Dongle to PC Dongle using USB profiles before configuring the Controller.";
        if (!info.SupportsControllerManagement)
            return "Update the Dongle firmware to enable Controller calibration and settings over wireless. Direct Controller USB remains available.";
        if (!info.ControllerBound || !info.ControllerConnected)
            return "Turn on the paired Controller and press PS, then refresh.";
        if (!info.ControllerSupportsWirelessManagement || !OtaProtocol.IsSuperMiniTarget(info.ControllerTarget))
            return "Update the Controller firmware to enable calibration and settings through the Dongle. Direct Controller USB remains available.";
        return string.Empty;
    }

    internal static void ValidateWirelessIdentity(DeviceInfo expected, DeviceInfo current)
    {
        if (current.UsbProfile != 1 || !current.SupportsControllerManagement ||
            !current.ControllerSupportsWirelessManagement || !current.ControllerBound || !current.ControllerConnected ||
            !OtaProtocol.IsSuperMiniTarget(current.ControllerTarget) ||
            expected.DongleMac.Length != 6 || expected.ControllerMac.Length != 6 ||
            !expected.DongleMac.Any(value => value != 0) || !expected.ControllerMac.Any(value => value != 0) ||
            !expected.DongleMac.SequenceEqual(current.DongleMac) || !expected.ControllerMac.SequenceEqual(current.ControllerMac) ||
            expected.DongleTarget != current.DongleTarget || expected.DongleFirmwareVersion != current.DongleFirmwareVersion ||
            expected.ControllerTarget != current.ControllerTarget || current.ControllerFirmwareVersion == 0 ||
            expected.ControllerFirmwareVersion != current.ControllerFirmwareVersion)
            throw new IOException("The paired Controller identity, connection or capabilities changed. Refresh the list.");
    }

    internal static void ValidateWirelessState(OtaState state)
    {
        // A retained Controller package is passive; it is also the normal state
        // after a successful update. Ready means an update has been armed.
        if (state is not (OtaState.Idle or OtaState.Error or OtaState.ControllerStaged))
            throw new IOException("Finish or cancel the firmware update before configuring the Controller.");
    }

    public ControllerManagement(string path) : this(new ControllerUsbDevice(path, "Controller")) { }
    public ControllerManagement(ControllerUsbDevice selected) : this(new HidControllerManagementTransport(selected)) { }
    internal ControllerManagement(IControllerManagementTransport transport) => this.transport = transport;
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var response = await ExchangeAsync(5, null, cancellationToken);
        if (response[12] != 1 || response[13] != 1 || (response[14] & 7) != 7 || response[7] is not (3 or 5) ||
            (transport.ExpectedProfile.HasValue && response[7] != transport.ExpectedProfile.Value))
            throw new InvalidDataException("Unsupported controller management capabilities.");
        Profile = response[7];
        CanSleep = (response[14] & 8) != 0;
    }
    public async Task<ControllerCalibration> ReadAsync(CancellationToken token) =>
        ControllerCalibration.Decode((await ExchangeAsync(1, null, token)).AsSpan(12, 16));
    public async Task<ControllerCalibration> WriteAsync(ControllerCalibration value, CancellationToken token)
    {
        if (value.SleepEnabled && !CanSleep) throw new InvalidOperationException("This firmware does not support wireless inactivity sleep.");
        return ControllerCalibration.Decode((await ExchangeAsync(2, value.Encode(), token)).AsSpan(12, 16));
    }
    public async Task<ControllerCalibration> ResetAsync(CancellationToken token) =>
        ControllerCalibration.Decode((await ExchangeAsync(3, null, token)).AsSpan(12, 16));
    public Task<byte[]> DiagnosticsAsync(CancellationToken token) => ExchangeAsync(4, null, token);

    private async Task<byte[]> ExchangeAsync(byte operation, byte[]? payload, CancellationToken token)
    {
        if (operation != 5 && Profile is not (3 or 5))
            throw new InvalidOperationException("Connect to the Controller before reading or saving settings.");
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(async () =>
            {
                var id = ++requestId;
                var data = ControllerManagementProtocol.Request(operation, Profile, id, payload);
                token.ThrowIfCancellationRequested();
                await transport.ValidateAsync(token).ConfigureAwait(false);
                await transport.SendAsync(data, token).ConfigureAwait(false);
                for (var attempt = 0; attempt < (transport.Wireless ? 60 : 20); ++attempt)
                {
                    await Task.Delay(50, token);
                    var feature = await transport.ReadAsync(token).ConfigureAwait(false);
                    try
                    {
                        var response = ControllerManagementProtocol.Response(feature, operation, id, Profile == 0 ? null : Profile);
                        await transport.ValidateAsync(token).ConfigureAwait(false);
                        return response;
                    }
                    catch (ManagementPendingException) { }
                }
                throw new TimeoutException(transport.Wireless
                    ? "Controller management timed out. Check the paired Controller, Dongle and their firmware."
                    : "Controller management timed out. Check USB connection and firmware.");
            }, token);
        }
        finally { gate.Release(); }
    }
    public void Dispose() { transport.Dispose(); }
}
