using System;
using System.Security.Cryptography;
using System.IO;
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
    private static readonly TimeSpan DongleReconnectGuidanceDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DongleReconnectTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ControllerWakeTimeout = TimeSpan.FromMinutes(2);
    private readonly IDongleDiscovery discovery;

    public FirmwareUpdateService(IDongleDiscovery discovery) => this.discovery = discovery;

    public async Task UpdateAllAsync(
        DongleDescriptor descriptor,
        IReleaseSource releaseSource,
        Func<CancellationToken, Task<bool>> confirmDevicesReadyAsync,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken,
        bool dongleOnly = false)
    {
        var reporter = new UpdateProgressReporter(progress);
        ArgumentNullException.ThrowIfNull(confirmDevicesReadyAsync);
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
            var selectedInfo = descriptor.DeviceInfo
                ?? throw new IOException("Dongle selection has no verified identity. Refresh and retry.");
            if (info.DongleTarget != selectedInfo.DongleTarget ||
                info.DongleFirmwareVersion != selectedInfo.DongleFirmwareVersion ||
                info.DongleMac.Length != 6 ||
                !CryptographicOperations.FixedTimeEquals(info.DongleMac, selectedInfo.DongleMac))
                throw new IOException("The selected Dongle changed before opening. Refresh and retry.");
            var initialStatus = await dongle.ReadStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            var recoveredPreviousSession = RecoveryCommandFor(initialStatus.State) != null;
            await RecoverActiveSessionAsync(
                dongle, initialStatus, reporter, cancellationToken).ConfigureAwait(false);
            if (recoveredPreviousSession && !dongleOnly)
            {
                info = await WaitForControllerIdentityAsync(
                    dongle, TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);
            }
            if (!dongleOnly) ValidateControllerIdentity(info);
            reporter.Report("release", 0, "Checking the latest stable release...");
            var release = dongleOnly
                ? await releaseSource.LoadDongleAsync(info, cancellationToken).ConfigureAwait(false)
                : await releaseSource.LoadAsync(info, cancellationToken).ConfigureAwait(false);
            if (dongleOnly && release.ControllerPackage != null)
                throw new InvalidOperationException("Dongle-only selection unexpectedly contains Controller firmware.");

            var updateController = release.ControllerPackage != null &&
                                   release.ControllerPackage.FirmwareVersion >
                                   info.ControllerFirmwareVersion;
            var updateDongle = release.DonglePackage.FirmwareVersion > info.DongleFirmwareVersion;
            if (!updateController && !updateDongle)
            {
                reporter.Report("complete", 100, dongleOnly ? "The Dongle is up to date." : "The Dongle and Controller are up to date.");
                return;
            }

            var originalDongleInfo = info;
            info = dongleOnly
                ? await ConfirmDongleReadyAsync(dongle, originalDongleInfo, confirmDevicesReadyAsync, cancellationToken).ConfigureAwait(false)
                : await ConfirmDevicesReadyAndRefreshAsync(
                dongle,
                release.DonglePackage.Target,
                release.ControllerPackage?.Target ?? info.ControllerTarget,
                confirmDevicesReadyAsync,
                reporter,
                cancellationToken).ConfigureAwait(false);
            updateController = release.ControllerPackage != null &&
                               release.ControllerPackage.FirmwareVersion >
                               info.ControllerFirmwareVersion;
            updateDongle = release.DonglePackage.FirmwareVersion > info.DongleFirmwareVersion;
            if (!updateController && !updateDongle)
            {
                reporter.Report("complete", 100, dongleOnly ? "The Dongle is up to date." : "The Dongle and Controller are up to date.");
                return;
            }
            var expectedControllerMac = Array.Empty<byte>();
            var expectedControllerFirmwareVersion = 0U;
            if (updateController)
            {
                ValidateControllerReady(info);
                expectedControllerMac = info.ControllerMac.AsSpan().ToArray();
                expectedControllerFirmwareVersion = info.ControllerFirmwareVersion;
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
                dongle = await WaitForUpdatedDongleAsync(
                    originalMac,
                    reporter,
                    DongleReconnectGuidanceDelay,
                    DongleReconnectTimeout,
                    cancellationToken).ConfigureAwait(false);
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
                info = updateDongle
                    ? await WaitForControllerAfterDongleUpdateAsync(
                        dongle,
                        reporter,
                        ControllerWakeTimeout,
                        cancellationToken).ConfigureAwait(false)
                    : await WaitForControllerReadyAsync(
                        dongle, TimeSpan.FromSeconds(30), cancellationToken)
                        .ConfigureAwait(false);
                ValidateControllerSelection(
                    info,
                    release.ControllerPackage!.Target,
                    expectedControllerFirmwareVersion,
                    expectedControllerMac);
                var armSession = NewSessionId();
                await dongle.SendCommandAsync(
                    OtaProtocol.EncodeCommand(OtaCommand.ArmController, armSession),
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    reporter.Report(
                        "controller", 0,
                        "Controller update armed; waiting for automatic acceptance.");
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

    internal static async Task<DeviceInfo> ConfirmDongleReadyAsync(IDongleTransport transport,
        DeviceInfo original, Func<CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        if (!await confirm(token).ConfigureAwait(false)) throw new OperationCanceledException(token);
        token.ThrowIfCancellationRequested();
        var refreshed = await transport.ReadDeviceInfoAsync(token).ConfigureAwait(false);
        ValidateDevice(refreshed);
        if (refreshed.DongleTarget != original.DongleTarget ||
            refreshed.DongleFirmwareVersion != original.DongleFirmwareVersion ||
            !CryptographicOperations.FixedTimeEquals(refreshed.DongleMac, original.DongleMac))
            throw new IOException("Dongle identity changed. Refresh and retry.");
        return refreshed;
    }

    internal static async Task<DeviceInfo> ConfirmDevicesReadyAndRefreshAsync(
        IDongleTransport dongle,
        FirmwareTarget expectedDongleTarget,
        FirmwareTarget expectedControllerTarget,
        Func<CancellationToken, Task<bool>> confirmDevicesReadyAsync,
        UpdateProgressReporter progress,
        CancellationToken cancellationToken)
    {
        progress.Report(
            "confirmation",
            100,
            "Firmware download completed and packages verified. " +
            "Waiting for device readiness confirmation...");
        if (!await confirmDevicesReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new OperationCanceledException(
                "Firmware installation was cancelled at device readiness confirmation.",
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var refreshed = await dongle.ReadDeviceInfoAsync(cancellationToken)
            .ConfigureAwait(false);
        ValidateDevice(refreshed);
        ValidateControllerIdentity(refreshed);
        if (refreshed.DongleTarget != expectedDongleTarget ||
            refreshed.ControllerTarget != expectedControllerTarget)
        {
            throw new InvalidOperationException(
                "The paired device selection changed while firmware was downloading. " +
                "Restart the update to download the correct packages.");
        }
        progress.Report(
            "confirmation", 100, "Dongle and Controller readiness confirmed.");
        return refreshed;
    }

    internal async Task<IDongleTransport> WaitForUpdatedDongleAsync(
        byte[] mac,
        UpdateProgressReporter progress,
        TimeSpan guidanceDelay,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var reconnectTask = discovery.WaitForMacAsync(mac, timeout, cancellationToken);
        using var guidanceCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var guidanceTask = Task.Delay(guidanceDelay, guidanceCancellation.Token);
        if (await Task.WhenAny(reconnectTask, guidanceTask).ConfigureAwait(false) == guidanceTask &&
            !guidanceTask.IsCanceled)
        {
            progress.Report(
                "dongle",
                96,
                "The Dongle is taking longer than expected to reconnect. " +
                "If it remains in programming mode, unplug it and reconnect it.");
        }

        try
        {
            return await reconnectTask.ConfigureAwait(false);
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(
                "The updated Dongle did not reconnect over USB. " +
                "Unplug it, reconnect it, and retry the update.",
                error);
        }
        finally
        {
            guidanceCancellation.Cancel();
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
                    ? "Waiting for the Controller to accept the update..."
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
        if (!HasSufficientControllerPower(info))
        {
            throw new InvalidOperationException(
                "Controller requires at least 30% battery or external power with the battery absent.");
        }
    }

    internal static bool HasSufficientControllerPower(DeviceInfo info) =>
        info.ControllerExternallyPowered ||
        (info.BatteryValid && info.ControllerBatteryPercent >= 30);

    internal static void ValidateControllerSelection(
        DeviceInfo info,
        FirmwareTarget expectedTarget,
        uint expectedFirmwareVersion,
        byte[] expectedMac)
    {
        ValidateControllerReady(info);
        if (info.ControllerTarget != expectedTarget ||
            info.ControllerFirmwareVersion != expectedFirmwareVersion ||
            !CryptographicOperations.FixedTimeEquals(info.ControllerMac, expectedMac))
        {
            throw new InvalidOperationException(
                "The paired Controller changed after the Dongle restarted. " +
                "Restart the update to select and verify the correct firmware.");
        }
    }

    private static void ValidateControllerIdentity(DeviceInfo info)
    {
        if ((info.ControllerFeatureFlags & (1U << 13)) != 0 && !OtaProtocol.IsSuperMiniTarget(info.ControllerTarget))
            throw new NotSupportedException("Update the Dongle firmware to a SuperMini-aware release before wireless Controller updates.");
        if (!info.SupportsControllerOta || !info.ControllerBound ||
            !info.ControllerConnected || info.ControllerFirmwareVersion == 0 ||
            !OtaProtocol.IsControllerTarget(info.ControllerTarget))
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
                last.ControllerConnected && HasSufficientControllerPower(last))
            {
                return last;
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        if (last == null || !last.ControllerConnected)
        {
            throw new TimeoutException(
                "Controller did not become ready after the Dongle restarted.");
        }
        ValidateControllerReady(last);
        return last;
    }

    internal static async Task<DeviceInfo> WaitForControllerAfterDongleUpdateAsync(
        IDongleTransport dongle,
        UpdateProgressReporter progress,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        progress.Report(
            "controller-wake",
            0,
            "Dongle update completed. Press the PS button to wake the Controller; " +
            "the update will continue automatically when it reconnects.");
        try
        {
            var info = await WaitForControllerReadyAsync(
                dongle, timeout, cancellationToken).ConfigureAwait(false);
            progress.Report(
                "controller-wake",
                100,
                "Controller reconnected; continuing with its firmware update.");
            return info;
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(
                "The Controller did not reconnect after the Dongle update. " +
                "Press the PS button to wake it, then retry the update.",
                error);
        }
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
        OtaProtocol.IsControllerTarget(info.ControllerTarget);

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
