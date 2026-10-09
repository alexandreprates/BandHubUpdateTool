using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace BandHub.FirmwareUpdate.Core;

public sealed class DongleBindingService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private readonly IDongleDiscovery discovery;

    public DongleBindingService(IDongleDiscovery discovery)
    {
        this.discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    }

    public Task<DeviceInfo> UnpairControllerAsync(
        DongleDescriptor descriptor,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        UnpairAsync(
            descriptor,
            OtaCommand.UnpairController,
            timeout,
            cancellationToken);

    public Task<DeviceInfo> UnpairDongleAsync(
        DongleDescriptor descriptor,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        UnpairAsync(
            descriptor,
            OtaCommand.UnpairDongle,
            timeout,
            cancellationToken);

    private async Task<DeviceInfo> UnpairAsync(
        DongleDescriptor descriptor,
        OtaCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.UsbProfile != UsbProfile.PcHid)
        {
            throw new NotSupportedException(
                "Binding tools are available only in the PC Dongle profile.");
        }
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var transport = await discovery.OpenAsync(
            descriptor.Path, cancellationToken).ConfigureAwait(false);
        var info = await transport.ReadDeviceInfoAsync(cancellationToken)
            .ConfigureAwait(false);
        ValidatePreconditions(info, command);

        var sessionId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        await transport.SendCommandAsync(
            OtaProtocol.EncodeBindingCommand(sessionId, command),
            cancellationToken).ConfigureAwait(false);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            info = await transport.ReadDeviceInfoAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!info.ControllerBound)
            {
                return info;
            }
        }

        throw new TimeoutException(command == OtaCommand.UnpairController
            ? "The Controller did not confirm the unpair request. Make sure it is awake and connected, then try again."
            : "The Dongle did not clear its saved Controller binding.");
    }

    private static void ValidatePreconditions(DeviceInfo info, OtaCommand command)
    {
        if (!info.SupportsBindingManagement)
        {
            throw new NotSupportedException(
                "The installed Dongle firmware does not support binding tools.");
        }
        if (command != OtaCommand.UnpairController)
        {
            return;
        }
        if (!info.ControllerBound)
        {
            throw new InvalidOperationException(
                "The selected Dongle does not have a saved Controller binding.");
        }
        if (!info.ControllerConnected)
        {
            throw new InvalidOperationException(
                "The Controller must be awake and connected before it can be unpaired remotely.");
        }
        if (!info.SupportsControllerRemoteUnpair)
        {
            throw new NotSupportedException(
                "The connected Controller firmware does not support remote unpairing.");
        }
    }
}
