using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BandHub.FirmwareUpdate.Core;
using NUnit.Framework;

namespace BandHub.FirmwareUpdate.Tests;

[TestFixture]
public sealed class ControllerUsbUpdateTests
{
    internal static ControllerUpdateInfo Info => new("D88B499205D4", FirmwareTarget.ControllerGh3SuperMini,
        0x000f0000, 0x140000, true, ControllerImageHealth.Healthy);
    private static ControllerUpdateDescriptor Descriptor => new("selected-path", Info.Serial, "BandHub GH3 SuperMini", Info, "");
    private static FirmwarePackage Package(FirmwareTarget target = FirmwareTarget.ControllerGh3SuperMini, uint version = 0x100000) =>
        FirmwarePackage.ParseAndVerify(ReleaseSourceTests.BuildPackage(target, version), ReleaseSourceTests.FixturePublicKey);

    [TestCase(FirmwareTarget.ControllerGh3SuperMini, "BandHub GH3 SuperMini")]
    [TestCase(FirmwareTarget.ControllerGh5SuperMini, "BandHub GH5 SuperMini")]
    public void TinyUsbProductUsesValidatedReportModelAndRechecksSerialAndTarget(FirmwareTarget target, string name)
    {
        var info = Info with { Target = target };
        var descriptor = Descriptor with { Name = "TinyUSB HID", Info = null };
        Assert.DoesNotThrow(() => ControllerUsbDiscovery.ValidateIdentity(descriptor, info));
        Assert.That((descriptor with { Info = info }).DisplayName, Is.EqualTo(name));
        Assert.Throws<IOException>(() => ControllerUsbDiscovery.ValidateIdentity(descriptor, info with { Serial = "000000000001" }));
        Assert.Throws<IOException>(() => ControllerUsbDiscovery.ValidateIdentity(descriptor, info with { Target = FirmwareTarget.ControllerGh3 }));
        Assert.Throws<IOException>(() => ControllerUsbDiscovery.ValidateIdentity(descriptor with { Info = info },
            info with { Target = target == FirmwareTarget.ControllerGh3SuperMini ? FirmwareTarget.ControllerGh5SuperMini : FirmwareTarget.ControllerGh3SuperMini }));
    }

    [TestCase("BandHub GH3 SuperMini", FirmwareTarget.ControllerGh5SuperMini)]
    [TestCase("BandHub GH5 SuperMini", FirmwareTarget.ControllerGh3SuperMini)]
    [TestCase("Other HID", FirmwareTarget.ControllerGh5SuperMini)]
    public void ReportModelMustMatchRecognizedProduct(string name, FirmwareTarget target)
    {
        Assert.Throws<IOException>(() => ControllerUsbDiscovery.ValidateIdentity(Descriptor with { Name = name, Info = null }, Info with { Target = target }));
    }

    [Test]
    public void SharedWireVectorsMatchCSharpAndRejectEveryCorruptedByte()
    {
        var vectors = File.ReadAllLines(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures/controller_usb_update_vectors.txt"));
        Assert.That(Convert.ToHexString(ControllerUsbUpdateProtocol.Encode(ControllerUpdateCommand.Begin,
            0x12345678, total: 4096).AsSpan(1)).ToLowerInvariant(), Is.EqualTo(vectors[0]));
        var status = new[] { ControllerUsbUpdateProtocol.StatusReportId }.Concat(Convert.FromHexString(vectors[1])).ToArray();
        var info = new[] { ControllerUsbUpdateProtocol.InfoReportId }.Concat(Convert.FromHexString(vectors[2])).ToArray();
        Assert.That(ControllerUsbUpdateProtocol.DecodeStatus(status), Is.EqualTo(new ControllerUpdateStatus(
            ControllerUpdateState.Ready, 0, ControllerUpdateCommand.Finish, 0x12345678, 4096, 4096, 0x100000)));
        Assert.That(ControllerUsbUpdateProtocol.DecodeInfo(info), Is.EqualTo(Info with { FirmwareVersion = 0x100000 }));
        foreach (var report in new[] { status, info })
            for (var i = 0; i < report.Length; ++i)
            {
                var bad = report.ToArray(); bad[i] ^= 1;
                Assert.Throws<InvalidDataException>(() => { if (report == status) ControllerUsbUpdateProtocol.DecodeStatus(bad); else ControllerUsbUpdateProtocol.DecodeInfo(bad); });
            }
    }

    [Test]
    public void InfoRejectsUnknownRoleTargetHealthSerialAndReservedFieldsEvenWithValidCrc()
    {
        var vector = File.ReadAllLines(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures/controller_usb_update_vectors.txt"))[2];
        foreach (var offset in new[] { 3, 4, 5, 6, 18, 30 })
        {
            var payload = Convert.FromHexString(vector); payload[offset] = 0xff;
            OtaProtocol.WriteUInt16(payload, 61, OtaProtocol.Crc16(payload, 61));
            Assert.Throws<InvalidDataException>(() => ControllerUsbUpdateProtocol.DecodeInfo(new byte[] { 0x42 }.Concat(payload).ToArray()));
        }
    }

    [Test]
    public void EncodeRejectsInvalidSessionsSizesAndCommandParameters()
    {
        Assert.Throws<ArgumentException>(() => ControllerUsbUpdateProtocol.Encode(ControllerUpdateCommand.Begin, 0, total: 200));
        Assert.Throws<ArgumentException>(() => ControllerUsbUpdateProtocol.Encode(ControllerUpdateCommand.Begin, 1, total: 128));
        Assert.Throws<ArgumentException>(() => ControllerUsbUpdateProtocol.Encode(ControllerUpdateCommand.Data, 1, data: new byte[45]));
        Assert.Throws<ArgumentException>(() => ControllerUsbUpdateProtocol.Encode(ControllerUpdateCommand.Commit, 1, offset: 1));
    }

    [Test]
    public async Task UsbUpdateWritesExactSignedPackageAndConfirmsHealthyIdentity()
    {
        var device = new FakeDevice(); var package = Package();
        await Run(device, package);
        Assert.That(device.Received.ToArray(), Is.EqualTo(package.Bytes));
        Assert.That(device.Commands.First(), Is.EqualTo(ControllerUpdateCommand.Begin));
        Assert.That(device.Commands.TakeLast(2), Is.EqualTo(new[] { ControllerUpdateCommand.Finish, ControllerUpdateCommand.Commit }));
        Assert.That(device.Commands, Does.Not.Contain(ControllerUpdateCommand.Abort));
        Assert.That(device.Discovered, Is.GreaterThan(0));
    }

    [TestCase(FirmwareTarget.ControllerGh5SuperMini)]
    [TestCase(FirmwareTarget.ControllerGh3)]
    public void WrongModelIsRejectedBeforeAnyUsbWrite(FirmwareTarget target)
    {
        var device = new FakeDevice();
        Assert.ThrowsAsync<InvalidDataException>(() => Run(device, Package(target)));
        Assert.That(device.Commands, Is.Empty);
    }

    [Test]
    public void DowngradeIsRejectedBeforeAnyUsbWrite()
    {
        var device = new FakeDevice();
        Assert.ThrowsAsync<InvalidOperationException>(() => Run(device, Package(version: 0xe0000)));
        Assert.That(device.Commands, Is.Empty);
    }

    [Test]
    public async Task CurrentVersionAndDeclinedConfirmationNeverWrite()
    {
        var device = new FakeDevice();
        await Run(device, Package(version: Info.FirmwareVersion));
        Assert.That(device.Commands, Is.Empty);
        Assert.ThrowsAsync<OperationCanceledException>(() => Run(device, Package(), confirm: _ => Task.FromResult(false)));
        Assert.That(device.Commands, Is.Empty);
    }

    [Test]
    public void IdentityChangedDuringDownloadOrConfirmationIsRejected()
    {
        var device = new FakeDevice();
        Assert.ThrowsAsync<IOException>(() => Run(device, Package(), confirm: _ =>
        {
            device.CurrentInfo = Info with { Serial = "000000000001" };
            return Task.FromResult(true);
        }));
        Assert.That(device.Commands, Is.Empty);
    }

    [Test]
    public void BusyOrUnhealthyDevicesCannotStart()
    {
        var device = new FakeDevice { Status = new(ControllerUpdateState.Receiving, 0, ControllerUpdateCommand.Begin, 99, 0, 384, 0) };
        Assert.ThrowsAsync<InvalidOperationException>(() => Run(device, Package()));
        Assert.That(device.Commands, Is.Empty);
        device.CurrentInfo = Info with { Health = ControllerImageHealth.Pending };
        Assert.ThrowsAsync<InvalidOperationException>(() => Run(device, Package()));
    }

    [Test]
    public void TransferCancellationAbortsBeforeCommit()
    {
        using var cancellation = new CancellationTokenSource();
        var device = new FakeDevice { OnData = () => cancellation.Cancel() };
        Assert.ThrowsAsync<OperationCanceledException>(() => Run(device, Package(), token: cancellation.Token));
        Assert.That(device.Commands.Last(), Is.EqualTo(ControllerUpdateCommand.Abort));
        Assert.That(device.Commands, Does.Not.Contain(ControllerUpdateCommand.Commit));
    }

    [Test]
    public async Task LostDataAcknowledgementRetriesIdenticalCommandWithoutDuplicatingBytes()
    {
        var device = new FakeDevice { DropFirstDataAck = true };
        var package = Package();
        await Run(device, package);
        Assert.That(device.DuplicateDataReports, Is.EqualTo(1));
        Assert.That(device.Received.ToArray(), Is.EqualTo(package.Bytes));
    }

    [Test]
    public void DeviceErrorAndTimeoutAbortWithoutCommit()
    {
        foreach (var error in new[] { true, false })
        {
            var device = new FakeDevice { ReportError = error, WrongOffset = !error };
            Assert.That(async () => await Run(device, Package()), Throws.Exception);
            Assert.That(device.Commands.Last(), Is.EqualTo(ControllerUpdateCommand.Abort));
            Assert.That(device.Commands, Does.Not.Contain(ControllerUpdateCommand.Commit));
        }
    }

    [Test]
    public async Task UncertainCommitReconcilesHealthyVersionWithoutAbort()
    {
        using var cancellation = new CancellationTokenSource();
        var device = new FakeDevice { LoseCommitAck = true, OnCommit = () => cancellation.Cancel() };
        await Run(device, Package(), token: cancellation.Token);
        Assert.That(device.Commands, Does.Not.Contain(ControllerUpdateCommand.Abort));
        Assert.That(device.Discovered, Is.GreaterThan(0));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void DifferentDeviceOrRollbackNeverReportsSuccess(bool differentDevice)
    {
        var device = new FakeDevice { ReconnectedInfo = differentDevice
            ? Info with { Serial = "000000000001", FirmwareVersion = 0x100000 } : Info };
        Assert.ThrowsAsync<TimeoutException>(() => Run(device, Package()));
        Assert.That(device.Commands, Does.Not.Contain(ControllerUpdateCommand.Abort));
    }

    private static Task Run(FakeDevice device, FirmwarePackage package, CancellationToken token = default,
        Func<CancellationToken, Task<bool>>? confirm = null) => new ControllerUsbUpdateService(device)
        {
            // Allow retries to cross multiple Windows timer ticks, including CI scheduling delays.
            CommandTimeout = TimeSpan.FromSeconds(2), ReconnectTimeout = TimeSpan.FromMilliseconds(30),
            PollInterval = TimeSpan.FromMilliseconds(1), RetryInterval = TimeSpan.FromMilliseconds(5),
        }.UpdateAsync(Descriptor, new Source(package), confirm ?? (_ => Task.FromResult(true)), null, token);

    private sealed class Source(FirmwarePackage package) : IReleaseSource
    {
        public Task<ReleaseSelection> LoadAsync(DeviceInfo info, CancellationToken token) =>
            throw new AssertionException("USB must never request Dongle releases.");
        public Task<FirmwarePackage> LoadControllerAsync(ControllerUpdateInfo info, CancellationToken token) => Task.FromResult(package);
    }

    private sealed class FakeDevice : IControllerUsbDiscovery, IControllerUsbTransport
    {
        public ControllerUpdateInfo CurrentInfo = Info;
        public ControllerUpdateInfo? ReconnectedInfo;
        public ControllerUpdateStatus Status = new(ControllerUpdateState.Idle, 0, 0, 0, 0, 0, 0);
        public List<ControllerUpdateCommand> Commands = new();
        public List<byte> Received = new();
        public Action? OnData, OnCommit;
        public bool LoseCommitAck, ReportError, WrongOffset, DropFirstDataAck;
        public int DuplicateDataReports;
        private byte[]? lastData;
        public int Discovered;
        public Task<IControllerUsbTransport> OpenAsync(ControllerUpdateDescriptor selected, CancellationToken token)
        { Assert.That(selected.Path, Is.EqualTo(Descriptor.Path)); return Task.FromResult<IControllerUsbTransport>(this); }
        public Task<IReadOnlyList<ControllerUpdateDescriptor>> DiscoverAsync(CancellationToken token)
        {
            Discovered++;
            var info = ReconnectedInfo ?? Info with { FirmwareVersion = 0x100000 };
            return Task.FromResult<IReadOnlyList<ControllerUpdateDescriptor>>(new[] { Descriptor with { Path = "new-path", Serial = info.Serial, Info = info } });
        }
        public Task<ControllerUpdateInfo> ReadInfoAsync(CancellationToken token) => Task.FromResult(CurrentInfo);
        public Task<ControllerUpdateStatus> ReadStatusAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (DropFirstDataAck && DuplicateDataReports == 0 && Status.Command == ControllerUpdateCommand.Data)
                return Task.FromResult(Status with { BytesReceived = 0 });
            return Task.FromResult(Status);
        }
        public Task SendAsync(byte[] report, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var p = report.AsSpan(1).ToArray();
            var command = (ControllerUpdateCommand)p[3]; Commands.Add(command);
            Status = Status with { Command = command, SessionId = OtaProtocol.ReadUInt32(p, 4) };
            switch (command)
            {
                case ControllerUpdateCommand.Begin:
                    Status = Status with { State = ControllerUpdateState.Receiving, TotalSize = OtaProtocol.ReadUInt32(p, 12) }; break;
                case ControllerUpdateCommand.Data:
                    if (lastData?.SequenceEqual(report) == true) { DuplicateDataReports++; break; }
                    lastData = report.ToArray();
                    Received.AddRange(p.AsSpan(17, p[16]).ToArray());
                    Status = Status with { BytesReceived = WrongOffset ? 0 : (uint)Received.Count, Error = ReportError ? (byte)5 : (byte)0 };
                    OnData?.Invoke(); break;
                case ControllerUpdateCommand.Finish:
                    Status = Status with { State = ControllerUpdateState.Ready, FirmwareVersion = 0x100000 }; break;
                case ControllerUpdateCommand.Commit:
                    OnCommit?.Invoke();
                    Status = Status with { State = ControllerUpdateState.RebootPending };
                    if (LoseCommitAck) throw new IOException("Reboot disconnected HID before ACK.");
                    break;
            }
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }
}
