using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BandHub.FirmwareUpdate.Core;
using NUnit.Framework;

namespace BandHub.FirmwareUpdate.Tests;

[TestFixture]
public sealed class DongleBindingServiceTests
{
    [TestCase(OtaCommand.UnpairController)]
    [TestCase(OtaCommand.UnpairDongle)]
    public async Task UnpairSendsCommandAndWaitsForDongleToBecomeUnbound(
        OtaCommand command)
    {
        var transport = new FakeTransport(Device());
        transport.DeviceInfos.Enqueue(Device());
        transport.DeviceInfos.Enqueue(Device(flags: 0x00));
        var service = new DongleBindingService(new FakeDiscovery(transport));

        var result = command == OtaCommand.UnpairController
            ? await service.UnpairControllerAsync(
                transport.Descriptor, TimeSpan.FromSeconds(1),
                CancellationToken.None)
            : await service.UnpairDongleAsync(
                transport.Descriptor, TimeSpan.FromSeconds(1),
                CancellationToken.None);

        Assert.That(result.ControllerBound, Is.False);
        Assert.That(transport.Commands, Has.Count.EqualTo(1));
        Assert.That(transport.Commands[0][3], Is.EqualTo((byte)command));
        Assert.That(BitConverter.ToUInt32(transport.Commands[0], 4), Is.Not.Zero);
    }

    [Test]
    public void RemoteUnpairRequiresConnectedControllerSupport()
    {
        var transport = new FakeTransport(Device(controllerFeatures: 0));
        transport.DeviceInfos.Enqueue(Device(controllerFeatures: 0));
        var service = new DongleBindingService(new FakeDiscovery(transport));

        var error = Assert.ThrowsAsync<NotSupportedException>(async () =>
            await service.UnpairControllerAsync(
                transport.Descriptor, TimeSpan.FromSeconds(1),
                CancellationToken.None));

        Assert.That(error?.Message, Does.Contain("Controller firmware"));
        Assert.That(transport.Commands, Is.Empty);
    }

    [Test]
    public async Task LocalUnpairSendsCommandWithoutSavedBinding()
    {
        var transport = new FakeTransport(Device(flags: 0x00));
        var service = new DongleBindingService(new FakeDiscovery(transport));

        var result = await service.UnpairDongleAsync(
            transport.Descriptor, TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.That(result.ControllerBound, Is.False);
        Assert.That(transport.Commands, Has.Count.EqualTo(1));
        Assert.That(transport.Commands[0][3],
            Is.EqualTo((byte)OtaCommand.UnpairDongle));
    }

    [Test]
    public void BindingToolsRequirePcHidProfile()
    {
        var transport = new FakeTransport(Device());
        transport.Descriptor = new DongleDescriptor
        {
            Path = "dongle",
            UsbProfile = UsbProfile.Ps3RockBandGuitar,
        };
        var service = new DongleBindingService(new FakeDiscovery(transport));

        Assert.ThrowsAsync<NotSupportedException>(async () =>
            await service.UnpairDongleAsync(
                transport.Descriptor, TimeSpan.FromSeconds(1),
                CancellationToken.None));
        Assert.That(transport.Commands, Is.Empty);
    }

    private static DeviceInfo Device(
        byte flags = 0x03,
        uint controllerFeatures = 1U << 12) => new()
    {
        Capabilities = 0x08,
        UsbProfile = (byte)UsbProfile.PcHid,
        Flags = flags,
        ControllerFeatureFlags = controllerFeatures,
    };

    private sealed class FakeDiscovery : IDongleDiscovery
    {
        private readonly FakeTransport transport;

        public FakeDiscovery(FakeTransport transport)
        {
            this.transport = transport;
        }

        public Task<IReadOnlyList<DongleDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DongleDescriptor>>(
                new[] { transport.Descriptor });

        public Task<IDongleTransport> OpenAsync(
            string path,
            CancellationToken cancellationToken) =>
            Task.FromResult<IDongleTransport>(transport);

        public Task<IDongleTransport> WaitForMacAsync(
            byte[] mac,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult<IDongleTransport>(transport);
    }

    private sealed class FakeTransport : IDongleTransport
    {
        private readonly DeviceInfo initial;

        public FakeTransport(DeviceInfo initial)
        {
            this.initial = initial;
            Descriptor = new DongleDescriptor
            {
                Path = "dongle",
                UsbProfile = UsbProfile.PcHid,
                DeviceInfo = initial,
            };
        }

        public DongleDescriptor Descriptor { get; set; }
        public Queue<DeviceInfo> DeviceInfos { get; } = new();
        public List<byte[]> Commands { get; } = new();

        public Task<DeviceInfo> ReadDeviceInfoAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(DeviceInfos.Count > 0
                ? DeviceInfos.Dequeue()
                : initial);

        public Task<OtaStatus> ReadStatusAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new OtaStatus { State = OtaState.Idle });

        public Task SendCommandAsync(
            byte[] report,
            CancellationToken cancellationToken)
        {
            Commands.Add(report);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
