using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BandHub.FirmwareUpdate.Core;
using NUnit.Framework;

namespace BandHub.FirmwareUpdate.Tests;

public sealed class ControllerManagementTests
{
    private static DeviceInfo Info(byte capabilities = 0x80, uint features = 1U << 14, byte flags = 3,
        byte usbProfile = 1, FirmwareTarget target = FirmwareTarget.ControllerGh5SuperMini,
        byte[]? controllerMac = null) => new()
    {
        Capabilities = capabilities, ControllerFeatureFlags = features, Flags = flags, UsbProfile = usbProfile,
        ControllerTarget = target, ControllerFirmwareVersion = 0x130000,
        DongleMac = new byte[] { 2, 3, 4, 5, 6, 7 }, ControllerMac = controllerMac ?? new byte[] { 2, 8, 9, 10, 11, 12 },
    };
    private static DongleDescriptor Dongle(DeviceInfo info, UsbProfile profile = UsbProfile.PcHid) =>
        new() { DeviceInfo = info, UsbProfile = profile, Path = "dongle", DisplayName = "BandHub Dongle" };

    [Test]
    public void WirelessAvailabilityExplainsMissingFirmwareDisconnectedControllerAndConsoleProfiles()
    {
        Assert.That(ControllerManagement.WirelessUnsupportedReason(Dongle(Info())), Is.Empty);
        Assert.That(ControllerManagement.WirelessUnsupportedReason(Dongle(Info(capabilities: 0))), Does.Contain("Dongle firmware"));
        Assert.That(ControllerManagement.WirelessUnsupportedReason(Dongle(Info(features: 0))), Does.Contain("Controller firmware"));
        Assert.That(ControllerManagement.WirelessUnsupportedReason(Dongle(Info(flags: 1))), Does.Contain("Turn on"));
        Assert.That(ControllerManagement.WirelessUnsupportedReason(Dongle(Info(), UsbProfile.Xbox360GuitarHero)), Does.Contain("PC HID"));
        Assert.That(ControllerManagement.WirelessUnsupportedReason(Dongle(Info(target: FirmwareTarget.ControllerGh5))), Does.Contain("Controller firmware"));
    }

    [Test]
    public void RetainedPackageAfterUpdateDoesNotBlockCalibrationButActiveUpdatesDo()
    {
        foreach (var state in Enum.GetValues<OtaState>())
        {
            if (state is OtaState.Idle or OtaState.Error or OtaState.ControllerStaged)
                Assert.DoesNotThrow(() => ControllerManagement.ValidateWirelessState(state));
            else
                Assert.Throws<IOException>(() => ControllerManagement.ValidateWirelessState(state));
        }
        Assert.Throws<IOException>(() => ControllerManagement.ValidateWirelessState((OtaState)255));
    }

    [Test]
    public void WirelessIdentityRejectsChangedPeerModelVersionCapabilitiesAndConnection()
    {
        ControllerManagement.ValidateWirelessIdentity(Info(), Info());
        foreach (var changed in new[] { Info(controllerMac: new byte[6]), Info(flags: 1), Info(features: 0),
            Info(capabilities: 0), Info(usbProfile: 3), Info(target: FirmwareTarget.ControllerGh3SuperMini),
            new DeviceInfo { Capabilities = 0x80, ControllerFeatureFlags = 1U << 14, Flags = 3, UsbProfile = 1,
                ControllerTarget = Info().ControllerTarget, ControllerFirmwareVersion = 1,
                DongleMac = Info().DongleMac, ControllerMac = Info().ControllerMac } })
            Assert.Throws<IOException>(() => ControllerManagement.ValidateWirelessIdentity(Info(), changed));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AllManagementOperationsUseSameValidatedProtocolOnBothConnections(bool wireless)
    {
        var transport = new FakeTransport { Wireless = wireless, PendingReads = 1 };
        using var management = new ControllerManagement(transport);
        await management.ConnectAsync(CancellationToken.None);
        Assert.That(management.Profile, Is.EqualTo(5));
        Assert.That(management.CanSleep, Is.True);
        var calibration = await management.ReadAsync(CancellationToken.None);
        var updated = calibration with { WhammyMin = 250, WhammyMax = 3000, SleepEnabled = true };
        Assert.That(await management.WriteAsync(updated, CancellationToken.None), Is.EqualTo(updated));
        Assert.That(await management.ResetAsync(CancellationToken.None), Is.EqualTo(new ControllerCalibration()));
        Assert.That(await management.DiagnosticsAsync(CancellationToken.None), Has.Length.EqualTo(63));
        Assert.That(transport.Operations, Is.EqualTo(new byte[] { 5, 1, 2, 3, 4 }));
        Assert.That(transport.Validations, Is.EqualTo(10));
    }

    [Test]
    public void IdentityChangeBeforeSendPreventsCommandsAndAfterReplyPreventsSuccess()
    {
        var before = new FakeTransport { FailValidationAt = 1 };
        using var first = new ControllerManagement(before);
        Assert.ThrowsAsync<IOException>(() => first.ConnectAsync(CancellationToken.None));
        Assert.That(before.Operations, Is.Empty);
        var after = new FakeTransport { FailValidationAt = 2 };
        using var second = new ControllerManagement(after);
        Assert.ThrowsAsync<IOException>(() => second.ConnectAsync(CancellationToken.None));
        Assert.That(second.Profile, Is.Zero);
    }

    [Test]
    public async Task WrongModelAndFailedSaveCannotReportSuccess()
    {
        var wrongModel = new FakeTransport { ReplyProfile = 3 };
        using var wrong = new ControllerManagement(wrongModel);
        Assert.ThrowsAsync<InvalidDataException>(() => wrong.ConnectAsync(CancellationToken.None));
        var failedSave = new FakeTransport();
        using var management = new ControllerManagement(failedSave);
        await management.ConnectAsync(CancellationToken.None);
        failedSave.Error = 3;
        Assert.ThrowsAsync<InvalidOperationException>(() => management.WriteAsync(new ControllerCalibration(), CancellationToken.None));
    }

    [Test]
    public void CancelledManagementNeverSends()
    {
        var transport = new FakeTransport();
        using var management = new ControllerManagement(transport);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.That(async () => await management.ConnectAsync(cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(transport.Operations, Is.Empty);
    }

    private sealed class FakeTransport : IControllerManagementTransport
    {
        public bool Wireless { get; init; } = true;
        public byte? ExpectedProfile => Wireless ? (byte)5 : null;
        public byte ReplyProfile = 5, Error;
        public int Validations, FailValidationAt, PendingReads;
        public List<byte> Operations { get; } = new();
        private byte[] response = Array.Empty<byte>();
        public Task ValidateAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (++Validations == FailValidationAt) throw new IOException("Paired controller changed");
            return Task.CompletedTask;
        }
        public Task SendAsync(byte[] request, CancellationToken token)
        {
            Operations.Add(request[5]);
            var payload = request[5] == 5 ? new byte[] { 1, 1, 15 }
                : request[5] == 2 ? request[12..28] : new ControllerCalibration().Encode();
            response = ControllerManagementProtocol.Request(request[5], ReplyProfile, BitConverter.ToUInt32(request, 8), payload);
            response[6] = Error;
            OtaProtocol.WriteUInt16(response, 61, OtaProtocol.Crc16(response, 61));
            return Task.CompletedTask;
        }
        public Task<byte[]> ReadAsync(CancellationToken token)
        {
            var payload = PendingReads-- > 0 ? ControllerManagementProtocol.Request(5, 5, 0) : response;
            var feature = new byte[64]; feature[0] = ControllerManagementProtocol.ResponseId; payload.CopyTo(feature, 1);
            return Task.FromResult(feature);
        }
        public void Dispose() { }
    }
}
