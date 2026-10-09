using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace BandHub.FirmwareUpdate.Core;

public sealed class ControllerUsbUpdateService
{
    private readonly IControllerUsbDiscovery discovery;
    internal TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(20);
    internal TimeSpan ReconnectTimeout { get; init; } = TimeSpan.FromSeconds(45);
    internal TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(2);
    public ControllerUsbUpdateService(IControllerUsbDiscovery discovery) => this.discovery = discovery;

    public async Task UpdateAsync(ControllerUpdateDescriptor selected, IReleaseSource source,
        Func<CancellationToken, Task<bool>> confirmReady, IProgress<UpdateProgress>? progress,
        CancellationToken token)
    {
        if (!selected.Supported) throw new NotSupportedException(selected.UnsupportedReason);
        var reporter = new UpdateProgressReporter(progress);
        using var transport = await discovery.OpenAsync(selected, token).ConfigureAwait(false);
        var original = await transport.ReadInfoAsync(token).ConfigureAwait(false);
        ValidateSelected(selected, original);
        reporter.Report("release", 0, "Checking the Controller release for direct USB update...");
        var package = await source.LoadControllerAsync(original, token).ConfigureAwait(false);
        CloudflareR2ReleaseSource.ValidateControllerPackage(original, package);
        if (package.FirmwareVersion < original.FirmwareVersion)
            throw new InvalidOperationException("Downgrading Controller firmware is not supported.");
        if (package.FirmwareVersion == original.FirmwareVersion)
        {
            reporter.Report("complete", 100, "The USB Controller is up to date.");
            return;
        }
        if (!await confirmReady(token).ConfigureAwait(false)) throw new OperationCanceledException(token);
        var refreshed = await transport.ReadInfoAsync(token).ConfigureAwait(false);
        ValidateSelected(selected, refreshed);
        if (refreshed != original) throw new IOException("Controller information changed. Refresh and retry.");
        var initialStatus = await transport.ReadStatusAsync(token).ConfigureAwait(false);
        if (initialStatus.State is not (ControllerUpdateState.Idle or ControllerUpdateState.Error))
            throw new InvalidOperationException("Another Controller update is active. Wait for it to finish or time out.");
        var session = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var commitStarted = false;
        var begun = false;
        try
        {
            begun = true;
            await ExchangeAsync(transport, ControllerUpdateCommand.Begin, session, 0,
                (uint)package.Bytes.Length, null, ControllerUpdateState.Receiving, 0, token).ConfigureAwait(false);
            for (var offset = 0; offset < package.Bytes.Length; offset += ControllerUsbUpdateProtocol.DataBytes)
            {
                var count = Math.Min(ControllerUsbUpdateProtocol.DataBytes, package.Bytes.Length - offset);
                await ExchangeAsync(transport, ControllerUpdateCommand.Data, session, (uint)offset, 0,
                    package.Bytes.AsSpan(offset, count).ToArray(), ControllerUpdateState.Receiving,
                    (uint)(offset + count), token).ConfigureAwait(false);
                var percent = (offset + count) * 90 / package.Bytes.Length;
                reporter.Report("usb-transfer", percent, "Transferring signed Controller firmware over USB...");
            }
            var ready = await ExchangeAsync(transport, ControllerUpdateCommand.Finish, session, 0, 0, null,
                ControllerUpdateState.Ready, (uint)package.Bytes.Length, token).ConfigureAwait(false);
            if (ready.FirmwareVersion != package.FirmwareVersion)
                throw new IOException("Controller verified a different firmware version.");
            token.ThrowIfCancellationRequested();
            commitStarted = true;
            reporter.Report("usb-commit", 92, "Installing verified firmware. Keep the Controller connected.");
            // Commit may reboot before its acknowledgement arrives. Never abort or automatically retry
            // an installation after an uncertain commit; reconcile by exact identity and healthy version.
            try
            {
                await ExchangeAsync(transport, ControllerUpdateCommand.Commit, session, 0, 0, null,
                    ControllerUpdateState.RebootPending, (uint)package.Bytes.Length, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException) { }
            transport.Dispose();
            reporter.Report("usb-reconnect", 95, "Waiting for the same USB Controller and its image health check...");
            await WaitForHealthyAsync(original, package.FirmwareVersion).ConfigureAwait(false);
            reporter.Report("complete", 100, "Controller updated and confirmed healthy over USB.");
        }
        catch
        {
            if (begun && !commitStarted)
            {
                try { await transport.SendAsync(ControllerUsbUpdateProtocol.Encode(ControllerUpdateCommand.Abort, session),
                    CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or TimeoutException) { }
            }
            throw;
        }
    }

    internal static void ValidateSelected(ControllerUpdateDescriptor selected, ControllerUpdateInfo info)
    {
        ControllerUsbDiscovery.ValidateIdentity(selected, info);
        if (!info.SupportsUpdate || info.Health != ControllerImageHealth.Healthy)
            throw new InvalidOperationException("Controller is not ready for a USB firmware update.");
        if (selected.Info != null && info.FirmwareVersion != selected.Info.FirmwareVersion)
            throw new IOException("Controller firmware changed since discovery. Refresh and retry.");
    }

    private async Task<ControllerUpdateStatus> ExchangeAsync(IControllerUsbTransport transport,
        ControllerUpdateCommand command, uint session, uint offset, uint total, byte[]? data,
        ControllerUpdateState expectedState, uint expectedOffset, CancellationToken token)
    {
        var report = ControllerUsbUpdateProtocol.Encode(command, session, offset, total, data);
        var deadline = Environment.TickCount64 + (long)CommandTimeout.TotalMilliseconds;
        var retryAt = Environment.TickCount64;
        var sendAttempted = false;
        Exception? lastError = null;
        while (Environment.TickCount64 < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (Environment.TickCount64 >= retryAt &&
                (!sendAttempted || command != ControllerUpdateCommand.Commit))
            {
                // A stale but valid status also leaves Commit uncertain. Send it only once.
                sendAttempted = true;
                try
                {
                    // Only the same in-flight command is retried, with identical bytes and session.
                    await transport.SendAsync(report, token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or TimeoutException)
                {
                    // An uncertain Commit must be reconciled by identity and image health.
                    if (command == ControllerUpdateCommand.Commit) throw;
                    LogTransientError(error);
                }
                retryAt = Environment.TickCount64 + (long)RetryInterval.TotalMilliseconds;
            }

            ControllerUpdateStatus? status = null;
            try
            {
                status = await transport.ReadStatusAsync(token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException)
            {
                if (command == ControllerUpdateCommand.Commit) throw;
                LogTransientError(error);
            }
            if (status?.SessionId == session)
            {
                if (status.Error != 0 || status.State == ControllerUpdateState.Error)
                    throw new InvalidOperationException(
                        $"Controller USB update error {status.Error} during {command} at offset {offset}." +
                        (status.Error == 8
                            ? " The Controller aborted the transfer. Reconnect it and retry. " +
                              "If this repeats with another USB data cable or port, the Controller may need " +
                              "corrected firmware installed through a Dongle or USB recovery."
                            : string.Empty), lastError);
                if (status.Command == command && status.BytesReceived == expectedOffset && status.State == expectedState)
                    return status;
            }
            await Task.Delay(PollInterval, token).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Controller did not acknowledge USB {command} at offset {offset} " +
            $"(expected {expectedOffset} bytes, session {session:X8}).", lastError);

        void LogTransientError(Exception error)
        {
            if (lastError == null)
                Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] USB {command} at offset {offset}: " +
                    $"retrying after {error.GetType().Name}: {error.Message}");
            lastError = error;
        }
    }

    private async Task WaitForHealthyAsync(ControllerUpdateInfo original, uint version)
    {
        var deadline = Environment.TickCount64 + (long)ReconnectTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            var matches = (await discovery.DiscoverAsync(CancellationToken.None).ConfigureAwait(false))
                .Where(d => d.Serial == original.Serial).ToArray();
            if (matches.Length > 1) throw new IOException("Multiple USB devices have the selected Controller identity.");
            if (matches.Length == 1 && matches[0].Info is { } info)
            {
                if (info.Target != original.Target) throw new IOException("Controller target changed after restart.");
                if (info.FirmwareVersion == version && info.Health == ControllerImageHealth.Healthy) return;
                if (info.Health == ControllerImageHealth.Failed)
                    throw new IOException("Controller image health validation failed.");
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        throw new TimeoutException("The same Controller did not confirm the new healthy firmware. Refresh to check for rollback; do not disconnect another device.");
    }
}
