using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BandHub.FirmwareUpdate.Core;
using NUnit.Framework;

namespace BandHub.FirmwareUpdate.Tests;

[TestFixture]
public sealed class FirmwareUpdateServiceTests
{
    [TestCase(OtaState.Receiving, OtaCommand.Abort)]
    [TestCase(OtaState.SelfReceiving, OtaCommand.Abort)]
    [TestCase(OtaState.RebootPending, OtaCommand.Abort)]
    [TestCase(OtaState.ControllerStaged, OtaCommand.CancelController)]
    [TestCase(OtaState.Error, OtaCommand.CancelController)]
    [TestCase(OtaState.Negotiating, OtaCommand.CancelController)]
    [TestCase(OtaState.AwaitingConfirmation, OtaCommand.CancelController)]
    public async Task RecoveryUsesTheActiveSession(
        OtaState state,
        OtaCommand expectedCommand)
    {
        var transport = new FakeTransport(SupportedDevice());
        transport.Statuses.Enqueue(new OtaStatus { State = OtaState.Idle });
        var progress = new UpdateProgressReporter(null);

        await FirmwareUpdateService.RecoverActiveSessionAsync(
            transport,
            new OtaStatus { State = state, SessionId = 0x12345678 },
            progress,
            CancellationToken.None);

        Assert.That(transport.Commands, Has.Count.EqualTo(1));
        Assert.That(transport.Commands[0][3], Is.EqualTo((byte)expectedCommand));
        Assert.That(BitConverter.ToUInt32(transport.Commands[0], 4),
                    Is.EqualTo(0x12345678));
    }

    [Test]
    public void ProgressSuppressesDuplicatePercentageAndMessage()
    {
        var sink = new RecordingProgress();
        var progress = new UpdateProgressReporter(sink);

        for (var index = 0; index < 1000; ++index)
        {
            progress.Report("controller-stage", 12, "Transferring controller firmware...");
        }
        progress.Report("controller-stage", 13, "Transferring controller firmware...");

        Assert.That(sink.Values, Has.Count.EqualTo(2));
        Assert.That(sink.Values[1].Percent, Is.EqualTo(13));
    }

    [Test]
    public void ExactTargetUpdateRejectsControllerWithoutPackageV2Capability()
    {
        var info = SupportedDevice(controllerFeatures: 1U << 7);
        var transport = new FakeTransport(info);
        transport.Statuses.Enqueue(new OtaStatus { State = OtaState.Idle });
        var discovery = new FakeDiscovery(transport);
        var release = new RejectIfLoadedReleaseSource();
        var service = new FirmwareUpdateService(discovery);

        var error = Assert.ThrowsAsync<NotSupportedException>(async () =>
            await service.UpdateAllAsync(
                transport.Descriptor,
                release,
                _ => Task.FromResult(true),
                null,
                CancellationToken.None));

        Assert.That(error?.Message, Does.Contain("wired bridge update"));
        Assert.That(release.Loaded, Is.False);
    }

    [TestCase(0x17, 0, true)]
    [TestCase(0x07, 0, false)]
    [TestCase(0x07, 30, true)]
    [TestCase(0x03, 80, false)]
    public void ControllerPowerPreflightRequiresBatteryOrExternalPower(
        byte flags,
        byte controllerBatteryPercent,
        bool expected)
    {
        var info = SupportedDevice(
            flags: flags, controllerBatteryPercent: controllerBatteryPercent);

        Assert.That(
            FirmwareUpdateService.HasSufficientControllerPower(info),
            Is.EqualTo(expected));
    }

    [TestCase(OtaState.Serving, 0)]
    [TestCase(OtaState.Error, 15)]
    public void RecoveryRefreshesControllerIdentityBeforeLoadingRelease(
        OtaState initialState,
        byte initialError)
    {
        var disconnected = SupportedDevice(flags: 0x05);
        var reconnected = SupportedDevice();
        var transport = new FakeTransport(disconnected);
        transport.NextDeviceInfos.Enqueue(reconnected);
        transport.Statuses.Enqueue(new OtaStatus { State = initialState,
                                                   Error = initialError,
                                                   SessionId = 0x12345678 });
        if (initialState == OtaState.Error)
        {
            transport.Statuses.Enqueue(new OtaStatus { State = OtaState.Error,
                                                       Error = initialError,
                                                       SessionId = 0x12345678 });
        }
        transport.Statuses.Enqueue(new OtaStatus { State = OtaState.Idle });
        var release = new CaptureThenStopReleaseSource();
        var service = new FirmwareUpdateService(new FakeDiscovery(transport));

        Assert.ThrowsAsync<ExpectedStopException>(async () =>
            await service.UpdateAllAsync(
                transport.Descriptor,
                release,
                _ => Task.FromResult(true),
                null,
                CancellationToken.None));

        Assert.That(release.Device, Is.SameAs(reconnected));
        Assert.That(transport.Commands, Has.Count.EqualTo(1));
        Assert.That(transport.Commands[0][3],
                    Is.EqualTo((byte)OtaCommand.CancelController));
    }

    [Test]
    public void DongleSelectionFollowsMacAcrossEnumerationOrderChanges()
    {
        var firstMac = new byte[] { 0x02, 1, 2, 3, 4, 5 };
        var secondMac = new byte[] { 0x02, 6, 7, 8, 9, 10 };
        var reordered = new[]
        {
            Descriptor("second", secondMac),
            Descriptor("first", firstMac),
        };

        Assert.That(DongleDescriptorSelection.FindByMac(reordered, firstMac),
                    Is.EqualTo(1));
        Assert.That(DongleDescriptorSelection.FindByMac(reordered, secondMac),
                    Is.EqualTo(0));
    }

    [Test]
    public async Task ConfirmationRefreshesDeviceStateAfterDownload()
    {
        var initial = SupportedDevice(controllerBatteryPercent: 80);
        var refreshed = SupportedDevice(controllerBatteryPercent: 75);
        var transport = new FakeTransport(initial);
        transport.NextDeviceInfos.Enqueue(refreshed);
        await transport.ReadDeviceInfoAsync(CancellationToken.None);
        var recorded = new RecordingProgress();
        var confirmationCalled = false;

        var result = await FirmwareUpdateService.ConfirmDevicesReadyAndRefreshAsync(
            transport,
            FirmwareTarget.DongleZeroPc,
            FirmwareTarget.ControllerGh3,
            _ =>
            {
                confirmationCalled = true;
                Assert.That(transport.DeviceInfoReadCount, Is.EqualTo(1));
                return Task.FromResult(true);
            },
            new UpdateProgressReporter(recorded),
            CancellationToken.None);

        Assert.That(confirmationCalled, Is.True);
        Assert.That(result, Is.SameAs(refreshed));
        Assert.That(transport.DeviceInfoReadCount, Is.EqualTo(2));
        Assert.That(recorded.Values[0].Message, Does.Contain("download completed"));
        Assert.That(recorded.Values[^1].Message, Does.Contain("readiness confirmed"));
    }

    [Test]
    public void DeclinedConfirmationCancelsBeforeRefreshingDeviceState()
    {
        var transport = new FakeTransport(SupportedDevice());
        transport.ReadDeviceInfoAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await FirmwareUpdateService.ConfirmDevicesReadyAndRefreshAsync(
                transport,
                FirmwareTarget.DongleZeroPc,
                FirmwareTarget.ControllerGh3,
                _ => Task.FromResult(false),
                new UpdateProgressReporter(null),
                CancellationToken.None));

        Assert.That(transport.DeviceInfoReadCount, Is.EqualTo(1));
    }

    [Test]
    public void ConfirmationRejectsControllerDisconnectedDuringDownload()
    {
        var transport = new FakeTransport(SupportedDevice());
        transport.NextDeviceInfos.Enqueue(SupportedDevice(flags: 0x05));
        transport.ReadDeviceInfoAsync(CancellationToken.None).GetAwaiter().GetResult();

        var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await FirmwareUpdateService.ConfirmDevicesReadyAndRefreshAsync(
                transport,
                FirmwareTarget.DongleZeroPc,
                FirmwareTarget.ControllerGh3,
                _ => Task.FromResult(true),
                new UpdateProgressReporter(null),
                CancellationToken.None));

        Assert.That(error?.Message, Does.Contain("paired Controller must be connected"));
    }

    [Test]
    public void ConfirmationRejectsControllerTargetChangedDuringDownload()
    {
        var transport = new FakeTransport(SupportedDevice());
        transport.NextDeviceInfos.Enqueue(SupportedDevice(
            controllerTarget: FirmwareTarget.ControllerGh5));
        transport.ReadDeviceInfoAsync(CancellationToken.None).GetAwaiter().GetResult();

        var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await FirmwareUpdateService.ConfirmDevicesReadyAndRefreshAsync(
                transport,
                FirmwareTarget.DongleZeroPc,
                FirmwareTarget.ControllerGh3,
                _ => Task.FromResult(true),
                new UpdateProgressReporter(null),
                CancellationToken.None));

        Assert.That(error?.Message, Does.Contain("device selection changed"));
    }

    [Test]
    public async Task SlowDongleReconnectReportsPhysicalRecoveryGuidance()
    {
        var transport = new FakeTransport(SupportedDevice());
        var discovery = new FakeDiscovery(transport)
        {
            ReconnectCompletion = new TaskCompletionSource<IDongleTransport>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var recorded = new RecordingProgress();
        var service = new FirmwareUpdateService(discovery);

        var reconnect = service.WaitForUpdatedDongleAsync(
            transport.Descriptor.DeviceInfo!.DongleMac,
            new UpdateProgressReporter(recorded),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(45),
            CancellationToken.None);
        discovery.ReconnectCompletion.SetResult(transport);

        Assert.That(await reconnect, Is.SameAs(transport));
        Assert.That(recorded.Values, Has.Count.EqualTo(1));
        Assert.That(recorded.Values[0].Message, Does.Contain("unplug it and reconnect it"));
    }

    [Test]
    public void DongleReconnectTimeoutIncludesPhysicalRecoveryGuidance()
    {
        var transport = new FakeTransport(SupportedDevice());
        var discovery = new FakeDiscovery(transport)
        {
            ReconnectError = new TimeoutException("Simulated discovery timeout."),
        };
        var service = new FirmwareUpdateService(discovery);

        var error = Assert.ThrowsAsync<TimeoutException>(async () =>
            await service.WaitForUpdatedDongleAsync(
                transport.Descriptor.DeviceInfo!.DongleMac,
                new UpdateProgressReporter(null),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(45),
                CancellationToken.None));

        Assert.That(error?.Message, Does.Contain("Unplug it, reconnect it"));
        Assert.That(error?.InnerException?.Message, Does.Contain("Simulated discovery timeout"));
    }

    [Test]
    public async Task ControllerWakeWaitReportsGuidanceUntilControllerReconnects()
    {
        var disconnected = SupportedDevice(flags: 0x05);
        var reconnected = SupportedDevice();
        var transport = new FakeTransport(disconnected);
        transport.NextDeviceInfos.Enqueue(reconnected);
        var recorded = new RecordingProgress();

        var result = await FirmwareUpdateService.WaitForControllerAfterDongleUpdateAsync(
            transport,
            new UpdateProgressReporter(recorded),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.That(result, Is.SameAs(reconnected));
        Assert.That(transport.DeviceInfoReadCount, Is.EqualTo(2));
        Assert.That(recorded.Values, Has.Count.EqualTo(2));
        Assert.That(recorded.Values[0].Stage, Is.EqualTo("controller-wake"));
        Assert.That(recorded.Values[0].Percent, Is.Zero);
        Assert.That(recorded.Values[0].Message, Does.Contain("PS button"));
        Assert.That(recorded.Values[1].Percent, Is.EqualTo(100));
        Assert.That(recorded.Values[1].Message, Does.Contain("reconnected"));
    }

    [Test]
    public void ControllerWakeTimeoutIncludesRetryGuidance()
    {
        var transport = new FakeTransport(SupportedDevice(flags: 0x05));

        var error = Assert.ThrowsAsync<TimeoutException>(async () =>
            await FirmwareUpdateService.WaitForControllerAfterDongleUpdateAsync(
                transport,
                new UpdateProgressReporter(null),
                TimeSpan.Zero,
                CancellationToken.None));

        Assert.That(error?.Message, Does.Contain("Press the PS button"));
        Assert.That(error?.InnerException, Is.TypeOf<TimeoutException>());
    }

    [Test]
    public void ControllerSelectionIsRevalidatedBeforeArmingUpdate()
    {
        var expectedMac = new byte[] { 0x02, 6, 7, 8, 9, 10 };

        Assert.DoesNotThrow(() => FirmwareUpdateService.ValidateControllerSelection(
            SupportedDevice(),
            FirmwareTarget.ControllerGh3,
            0x00020000,
            expectedMac));

        var error = Assert.Throws<InvalidOperationException>(() =>
            FirmwareUpdateService.ValidateControllerSelection(
                SupportedDevice(controllerFirmwareVersion: 0x00020001),
                FirmwareTarget.ControllerGh3,
                0x00020000,
                expectedMac));

        Assert.That(error?.Message, Does.Contain("Controller changed"));
    }

    private static DongleDescriptor Descriptor(string path, byte[] mac) => new()
    {
        Path = path,
        DisplayName = path,
        Supported = true,
        DeviceInfo = SupportedDevice(dongleMac: mac),
    };

    private static DeviceInfo SupportedDevice(
        uint controllerFeatures = (1U << 7) | (1U << 10),
        byte[]? dongleMac = null,
        byte flags = 0x07,
        byte controllerBatteryPercent = 80,
        FirmwareTarget dongleTarget = FirmwareTarget.DongleZeroPc,
        FirmwareTarget controllerTarget = FirmwareTarget.ControllerGh3,
        uint controllerFirmwareVersion = 0x00020000) => new()
    {
        Capabilities = 0x03,
        UsbProfile = 1,
        DongleTarget = dongleTarget,
        DongleFirmwareVersion = 0x00020000,
        ControllerTarget = controllerTarget,
        ControllerFirmwareVersion = controllerFirmwareVersion,
        ControllerFeatureFlags = controllerFeatures,
        ControllerBatteryPercent = controllerBatteryPercent,
        Flags = flags,
        DongleMac = dongleMac ?? new byte[] { 0x02, 1, 2, 3, 4, 5 },
        ControllerMac = new byte[] { 0x02, 6, 7, 8, 9, 10 },
    };

    private sealed class RecordingProgress : IProgress<UpdateProgress>
    {
        public List<UpdateProgress> Values { get; } = new();
        public void Report(UpdateProgress value) => Values.Add(value);
    }

    private sealed class FakeTransport : IDongleTransport
    {
        private DeviceInfo info;

        public FakeTransport(DeviceInfo info)
        {
            this.info = info;
            Descriptor = new DongleDescriptor
            {
                Path = "fake",
                DisplayName = "Fake Dongle",
                Supported = true,
                DeviceInfo = info,
            };
        }

        public DongleDescriptor Descriptor { get; }
        public Queue<OtaStatus> Statuses { get; } = new();
        public Queue<DeviceInfo> NextDeviceInfos { get; } = new();
        public List<byte[]> Commands { get; } = new();
        public int DeviceInfoReadCount { get; private set; }

        public Task<DeviceInfo> ReadDeviceInfoAsync(CancellationToken cancellationToken)
        {
            if (DeviceInfoReadCount != 0 && NextDeviceInfos.Count != 0)
            {
                info = NextDeviceInfos.Dequeue();
            }
            ++DeviceInfoReadCount;
            return Task.FromResult(info);
        }

        public Task<OtaStatus> ReadStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Statuses.Dequeue());

        public Task SendCommandAsync(byte[] report, CancellationToken cancellationToken)
        {
            Commands.Add(report);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeDiscovery : IDongleDiscovery
    {
        private readonly IDongleTransport transport;

        public FakeDiscovery(IDongleTransport transport) => this.transport = transport;

        public TaskCompletionSource<IDongleTransport>? ReconnectCompletion { get; init; }
        public Exception? ReconnectError { get; init; }

        public Task<IReadOnlyList<DongleDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DongleDescriptor>>(
                new[] { transport.Descriptor });

        public Task<IDongleTransport> OpenAsync(
            string path,
            CancellationToken cancellationToken) => Task.FromResult(transport);

        public Task<IDongleTransport> WaitForMacAsync(
            byte[] mac,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (ReconnectError != null)
            {
                return Task.FromException<IDongleTransport>(ReconnectError);
            }
            return ReconnectCompletion?.Task ?? Task.FromResult(transport);
        }
    }

    private sealed class RejectIfLoadedReleaseSource : IReleaseSource
    {
        public bool Loaded { get; private set; }

        public Task<ReleaseSelection> LoadAsync(
            DeviceInfo device,
            CancellationToken cancellationToken)
        {
            Loaded = true;
            throw new AssertionException("Legacy Controller reached release loading.");
        }
    }

    private sealed class CaptureThenStopReleaseSource : IReleaseSource
    {
        public DeviceInfo? Device { get; private set; }

        public Task<ReleaseSelection> LoadAsync(
            DeviceInfo device,
            CancellationToken cancellationToken)
        {
            Device = device;
            throw new ExpectedStopException();
        }
    }

    private sealed class ExpectedStopException : Exception
    {
    }
}
