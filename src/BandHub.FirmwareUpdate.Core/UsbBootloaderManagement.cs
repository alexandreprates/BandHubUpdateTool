using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;

namespace BandHub.FirmwareUpdate.Core;

public static class UsbBootloaderManagement
{
    public static bool MatchesPcDongle(int vendor, int product, string manufacturer, string name, string serial, string expectedSerial) =>
        vendor == OtaProtocol.VendorId && product == OtaProtocol.ProductId && manufacturer == "BandHub" &&
        name == "BandHub Controller" && serial.Length == 12 && serial.All(char.IsAsciiHexDigit) &&
        string.Equals(serial, expectedSerial, StringComparison.OrdinalIgnoreCase);

    public static async Task EnterAsync(UsbProfileDevice selected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!selected.CanEnterBootloader)
            throw new NotSupportedException("Update this device's firmware before using USB flash mode.");
        if (selected.Role == 2 && selected.Profile == UsbProfile.PcHid)
        {
            var device = DeviceList.Local.GetHidDevices().FirstOrDefault(x => x.DevicePath == selected.Path)
                ?? throw new IOException("The selected Dongle disconnected.");
            if (!MatchesPcDongle(device.VendorID, device.ProductID, device.GetManufacturer(), device.GetProductName(), device.GetSerialNumber(), selected.Serial))
                throw new IOException("Dongle identity changed. Refresh the device list.");
            using var transport = await new HidDongleDiscovery().OpenAsync(selected.Path, token).ConfigureAwait(false);
            await EnterDongleAsync(transport, selected.Serial, token).ConfigureAwait(false);
        }
        else
            await UsbProfileManagement.EnterBootloaderAsync(selected, token).ConfigureAwait(false);
        await WaitForDisconnectAsync(() => DeviceList.Local.GetHidDevices().Any(x => x.DevicePath == selected.Path),
            TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
    }

    public static async Task EnterDongleAsync(IDongleTransport transport, string serial, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = await transport.ReadDeviceInfoAsync(token).ConfigureAwait(false);
        if (!string.Equals(transport.Descriptor.SerialNumber, serial, StringComparison.OrdinalIgnoreCase) ||
            info.UsbProfile != (byte)UsbProfile.PcHid || !info.SupportsUsbBootloader)
            throw new NotSupportedException("Dongle identity or bootloader capability changed. Refresh the device list.");
        var status = await transport.ReadStatusAsync(token).ConfigureAwait(false);
        if (status.State is not (OtaState.Idle or OtaState.Ready or OtaState.Error or OtaState.ControllerStaged))
            throw new InvalidOperationException("Wait for the current firmware update to finish before entering USB flash mode.");
        var session = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        await transport.SendCommandAsync(OtaProtocol.EncodeCommand(OtaCommand.EnterBootloader, session), token).ConfigureAwait(false);
        for (var attempt = 0; attempt < 8; ++attempt)
        {
            await Task.Delay(40, token).ConfigureAwait(false);
            status = await transport.ReadStatusAsync(token).ConfigureAwait(false);
            if (status.SessionId == session && status.State == OtaState.RebootPending && status.Error == 0) return;
            if (status.Error != 0) throw new IOException("The Dongle rejected USB flash mode. Wait for active operations to finish.");
        }
        throw new TimeoutException("The Dongle did not acknowledge USB flash mode. Refresh and retry.");
    }

    // Disappearance confirms the application left USB, not ROM readiness. Never
    // select or flash an arbitrary serial port based only on this observation.
    public static async Task WaitForDisconnectAsync(Func<bool> isPresent, TimeSpan timeout, CancellationToken token)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!isPresent()) return;
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException("The device acknowledged flash mode but did not disconnect. Refresh and retry.");
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }
}
