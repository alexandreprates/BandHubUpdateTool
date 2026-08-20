using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace BandHub.FirmwareUpdate.Core;

public sealed class UpdateProgress
{
    public string Stage { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int Percent { get; init; }
}

internal sealed class UpdateProgressReporter
{
    private readonly IProgress<UpdateProgress>? progress;
    private string lastStage = string.Empty;
    private string lastMessage = string.Empty;
    private int lastPercent = -1;

    public UpdateProgressReporter(IProgress<UpdateProgress>? progress) =>
        this.progress = progress;

    public void Report(string stage, int percent, string message)
    {
        var normalizedPercent = Math.Max(0, Math.Min(100, percent));
        if (normalizedPercent == lastPercent && stage == lastStage && message == lastMessage)
        {
            return;
        }
        lastStage = stage;
        lastMessage = message;
        lastPercent = normalizedPercent;
        progress?.Report(new UpdateProgress
        {
            Stage = stage,
            Percent = normalizedPercent,
            Message = message,
        });
    }
}

public sealed class FirmwareUpdateService
{
    private readonly IDongleDiscovery discovery;

    public FirmwareUpdateService(IDongleDiscovery discovery) => this.discovery = discovery;

    public async Task UpdateAllAsync(
        DongleDescriptor descriptor,
        IReleaseSource releaseSource,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var reporter = new UpdateProgressReporter(progress);
        if (!descriptor.Supported)
        {
            throw new NotSupportedException(descriptor.UnsupportedReason);
        }

        IDongleTransport? dongle = await discovery.OpenAsync(descriptor.Path, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var info = await dongle.ReadDeviceInfoAsync(cancellationToken).ConfigureAwait(false);
            ValidateDevice(info);
            var initialStatus = await dongle.ReadStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            var recoveredPreviousSession = RecoveryCommandFor(initialStatus.State) != null;
            await RecoverActiveSessionAsync(
                dongle, initialStatus, reporter, cancellationToken).ConfigureAwait(false);
            if (recoveredPreviousSession)
            {
                info = await WaitForControllerIdentityAsync(
                    dongle, TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);
            }
            ValidateControllerIdentity(info);
            reporter.Report("release", 0, "Checking the latest stable release...");
            var release = await releaseSource.LoadAsync(info, cancellationToken).ConfigureAwait(false);

            var updateController = release.ControllerPackage != null &&
                                   release.ControllerPackage.FirmwareVersion >
                                   info.ControllerFirmwareVersion;
            var updateDongle = release.DonglePackage.FirmwareVersion > info.DongleFirmwareVersion;
            if (!updateController && !updateDongle)
            {
                reporter.Report("complete", 100, "The Dongle and Controller are up to date.");
                return;
            }
            if (updateController)
            {
                ValidateControllerReady(info);
                await UploadAsync(
                    dongle,
                    release.ControllerPackage!,
                    OtaCommand.BeginStagedController,
                    OtaState.ControllerStaged,
                    "controller-stage",
                    reporter,
                    cancellationToken).ConfigureAwait(false);
            }

            if (updateDongle)
            {
                var originalMac = info.DongleMac;
                var session = await UploadAsync(
                    dongle,
                    release.DonglePackage,
                    OtaCommand.BeginDongle,
                    OtaState.RebootPending,
                    "dongle",
                    reporter,
                    cancellationToken).ConfigureAwait(false);
                reporter.Report("dongle", 95, "Restarting the Dongle...");
                await dongle.SendCommandAsync(
                    OtaProtocol.EncodeCommand(OtaCommand.CommitDongle, session),
                    CancellationToken.None).ConfigureAwait(false);
                dongle.Dispose();
                dongle = null;
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                dongle = await discovery.WaitForMacAsync(
                    originalMac, TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
                info = await dongle.ReadDeviceInfoAsync(cancellationToken).ConfigureAwait(false);
                if (info.DongleFirmwareVersion != release.DonglePackage.FirmwareVersion)
                {
                    throw new InvalidOperationException(
                        "The Dongle reconnected without the expected firmware version; rollback may have occurred.");
                }
                reporter.Report("dongle", 100, "Dongle update validated.");
            }

            if (updateController)
            {
                info = await WaitForControllerReadyAsync(
                    dongle, TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);
                var armSession = NewSessionId();
                await dongle.SendCommandAsync(
                    OtaProtocol.EncodeCommand(OtaCommand.ArmController, armSession),
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    reporter.Report(
                        "controller", 0,
                        "Press and hold Guide/PS on the Controller to authorize the update.");
                    await WaitForControllerAsync(
                        dongle,
                        release.ControllerPackage!.FirmwareVersion,
                        reporter,
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await CancelControllerUpdateAsync(dongle).ConfigureAwait(false);
                    throw;
                }
            }
            reporter.Report("complete", 100, "Firmware update completed successfully.");
        }
        finally
        {
            dongle?.Dispose();
        }
    }

    internal static OtaCommand? RecoveryCommandFor(OtaState state) => state switch
    {
        OtaState.Erasing or
        OtaState.Receiving or
        OtaState.SelfReceiving or
        OtaState.SelfVerifying or
        OtaState.RebootPending => OtaCommand.Abort,
        OtaState.Ready or
        OtaState.Error or
        OtaState.Negotiating or
        OtaState.Serving or
        OtaState.AwaitingConfirmation or
        OtaState.ControllerStaged => OtaCommand.CancelController,
        _ => null,
    };

    internal static async Task RecoverActiveSessionAsync(
        IDongleTransport dongle,
        OtaStatus initialStatus,
        UpdateProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var command = RecoveryCommandFor(initialStatus.State);
        if (command == null)
        {
            return;
        }

        var session = initialStatus.SessionId == 0
            ? NewSessionId()
            : initialStatus.SessionId;
        progress.Report("recovery", 0, "Recovering the previous interrupted update...");
        await dongle.SendCommandAsync(
            OtaProtocol.EncodeCommand(command.Value, session), cancellationToken)
            .ConfigureAwait(false);
        await WaitForRecoveryIdleAsync(
            dongle, TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);
        progress.Report("recovery", 100, "Previous update session cleared.");
    }

    private static async Task WaitForRecoveryIdleAsync(
        IDongleTransport dongle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        OtaStatus? last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await dongle.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (last.State == OtaState.Idle && last.Error == 0)
            {
                return;
            }
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Timed out clearing the previous update; last state was {last?.State} " +
            $"with error {last?.Error}.");
    }

    private static async Task<uint> UploadAsync(
        IDongleTransport dongle,
        FirmwarePackage package,
        OtaCommand beginCommand,
        OtaState completedState,
        string stage,
        UpdateProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var session = NewSessionId();
        var started = false;
        try
        {
            await dongle.SendCommandAsync(
                OtaProtocol.EncodeCommand(beginCommand, session, totalSize: (uint)package.Bytes.Length),
                cancellationToken).ConfigureAwait(false);
            started = true;
            await WaitForStatusAsync(
                dongle,
                value => value.SessionId == session &&
                         (value.State == OtaState.Receiving ||
                          value.State == OtaState.SelfReceiving),
                TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);

            for (var offset = 0; offset < package.Bytes.Length; offset += OtaProtocol.CommandDataBytes)
            {
                var length = Math.Min(OtaProtocol.CommandDataBytes, package.Bytes.Length - offset);
                var chunk = new byte[length];
                Buffer.BlockCopy(package.Bytes, offset, chunk, 0, length);
                await dongle.SendCommandAsync(
                    OtaProtocol.EncodeCommand(OtaCommand.Data, session, (uint)offset, data: chunk),
                    cancellationToken).ConfigureAwait(false);
                var expected = (uint)(offset + length);
                var status = await WaitForStatusAsync(
                    dongle,
                    value => value.SessionId == session && value.BytesReceived >= expected,
                    TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                EnsureNoDeviceError(status);
                progress.Report(
                    stage, (int)(expected * 90L / package.Bytes.Length),
                    $"Transferring {stage} firmware...");
            }

            await dongle.SendCommandAsync(
                OtaProtocol.EncodeCommand(OtaCommand.Finish, session),
                cancellationToken).ConfigureAwait(false);
            var completed = await WaitForStatusAsync(
                dongle,
                value => value.SessionId == session &&
                         (value.State == completedState || value.State == OtaState.Error),
                TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            EnsureNoDeviceError(completed);
            if (completed.FirmwareVersion != package.FirmwareVersion ||
                completed.ImageSize != package.ImageSize ||
                completed.ManifestCrc32 != package.HeaderCrc32)
            {
                throw new InvalidOperationException("Device metadata does not match the uploaded package.");
            }
            return session;
        }
        catch
        {
            if (started)
            {
                try
                {
                    await dongle.SendCommandAsync(
                        OtaProtocol.EncodeCommand(OtaCommand.Abort, session),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original transfer failure.
                }
            }
            throw;
        }
    }

    private static async Task WaitForControllerAsync(
        IDongleTransport dongle,
        uint expectedVersion,
        UpdateProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await dongle.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            EnsureNoDeviceError(status);
            var info = await dongle.ReadDeviceInfoAsync(cancellationToken).ConfigureAwait(false);
            if (info.ControllerFirmwareVersion == expectedVersion)
            {
                progress.Report("controller", 100, "Controller update validated.");
                return;
            }
            var percent = status.TotalSize == 0
                ? 0
                : (int)Math.Min(99, status.BytesReceived * 100L / status.TotalSize);
            progress.Report(
                "controller", percent,
                status.State == OtaState.Negotiating
                    ? "Waiting for Guide/PS authorization..."
                    : "Updating the Controller...");
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("Controller update did not finish within five minutes.");
    }

    private static async Task<OtaStatus> WaitForStatusAsync(
        IDongleTransport dongle,
        Func<OtaStatus, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        OtaStatus? last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await dongle.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            EnsureNoDeviceError(last);
            if (predicate(last))
            {
                return last;
            }
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"Timed out waiting for the Dongle; last state was {last?.State}.");
    }

    private static async Task CancelControllerUpdateAsync(IDongleTransport dongle)
    {
        try
        {
            await dongle.SendCommandAsync(
                OtaProtocol.EncodeCommand(OtaCommand.CancelController, NewSessionId()),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original update or cancellation failure.
        }
    }

    private static void ValidateDevice(DeviceInfo info)
    {
        if (info.UsbProfile != 1 || !info.SupportsDongleSelfOta)
        {
            throw new NotSupportedException(
                "Updater version 1 requires a PC-profile Dongle with the self-OTA agent.");
        }
    }

    private static void ValidateControllerReady(DeviceInfo info)
    {
        ValidateControllerIdentity(info);
        if (!info.BatteryValid || info.ControllerBatteryPercent < 30)
        {
            throw new InvalidOperationException("Controller battery must be available and at least 30%.");
        }
    }

    private static void ValidateControllerIdentity(DeviceInfo info)
    {
        if (!info.SupportsControllerOta || !info.ControllerBound ||
            !info.ControllerConnected || info.ControllerFirmwareVersion == 0 ||
            (info.ControllerTarget != FirmwareTarget.ControllerGh3 &&
             info.ControllerTarget != FirmwareTarget.ControllerGh5))
        {
            throw new InvalidOperationException(
                "A supported paired Controller must be connected before checking updates.");
        }
        if (!info.SupportsControllerPackageV2)
        {
            throw new NotSupportedException(
                "The paired Controller requires one wired bridge update before it can accept exact-target packages.");
        }
    }

    private static async Task<DeviceInfo> WaitForControllerReadyAsync(
        IDongleTransport dongle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        DeviceInfo? last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await dongle.ReadDeviceInfoAsync(cancellationToken).ConfigureAwait(false);
            if (last.SupportsControllerOta && last.SupportsControllerPackageV2 &&
                last.ControllerBound &&
                last.ControllerConnected && last.BatteryValid &&
                last.ControllerBatteryPercent >= 30)
            {
                return last;
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        ValidateControllerReady(last ?? throw new TimeoutException(
            "Controller information was not available after the Dongle restarted."));
        throw new TimeoutException("Controller did not become ready after the Dongle restarted.");
    }

    private static async Task<DeviceInfo> WaitForControllerIdentityAsync(
        IDongleTransport dongle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        DeviceInfo? last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await dongle.ReadDeviceInfoAsync(cancellationToken).ConfigureAwait(false);
            if (HasControllerIdentity(last))
            {
                return last;
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        ValidateControllerIdentity(last ?? throw new TimeoutException(
            "Controller information was not available after update recovery."));
        throw new TimeoutException("Controller did not reconnect after update recovery.");
    }

    private static bool HasControllerIdentity(DeviceInfo info) =>
        info.SupportsControllerOta && info.ControllerBound &&
        info.ControllerConnected &&
        info.ControllerFirmwareVersion != 0 &&
        (info.ControllerTarget == FirmwareTarget.ControllerGh3 ||
         info.ControllerTarget == FirmwareTarget.ControllerGh5);

    private static void EnsureNoDeviceError(OtaStatus status)
    {
        if (status.Error != 0 || status.State == OtaState.Error)
        {
            throw new InvalidOperationException($"Dongle OTA error {status.Error}.");
        }
    }

    private static uint NewSessionId()
    {
        var bytes = new byte[4];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(bytes);
        var value = BitConverter.ToUInt32(bytes, 0);
        return value == 0 ? 1U : value;
    }

}
