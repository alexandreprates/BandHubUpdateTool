using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BandHub.FirmwareUpdate.Core;
using NUnit.Framework;

namespace BandHub.FirmwareUpdate.Tests;

public sealed class UsbBootloaderManagementTests
{
    private const string Serial = "AABBCCDDEEFF";
    private sealed class Transport : IDongleTransport
    {
        public DongleDescriptor Descriptor { get; set; } = new() { SerialNumber = Serial };
        public DeviceInfo Info { get; set; } = new() { Capabilities = 0x40, UsbProfile = 1 };
        public OtaState State { get; set; } = OtaState.Idle;
        public bool Reject { get; set; }
        public bool Acknowledge { get; set; } = true;
        public byte[]? Sent { get; private set; }
        public void Dispose() { }
        public Task<DeviceInfo> ReadDeviceInfoAsync(CancellationToken token) => Task.FromResult(Info);
        public Task<OtaStatus> ReadStatusAsync(CancellationToken token) => Task.FromResult(new OtaStatus {
            State = Sent != null && Acknowledge ? OtaState.RebootPending : State,
            SessionId = Sent != null && Acknowledge ? BitConverter.ToUInt32(Sent, 4) : 0,
            Error = Sent != null && Reject ? (byte)2 : (byte)0,
        });
        public Task SendCommandAsync(byte[] report, CancellationToken token) { Sent = report; return Task.CompletedTask; }
    }

    [Test]
    public void CapabilityDefaultsOffAndUsesReservedProfileByte()
    {
        var response = new byte[63]; response[12] = 1; response[13] = 1; response[14] = 3; response[15] = 10;
        Assert.That(UsbProfileManagement.ParseInfo("path", Serial, response, 1, UsbProfile.Xbox360GuitarHero).CanEnterBootloader, Is.False);
        response[22] = 1;
        Assert.That(UsbProfileManagement.ParseInfo("path", Serial, response, 1, UsbProfile.Xbox360GuitarHero).CanEnterBootloader, Is.True);
        Assert.That(new DeviceInfo { Capabilities = 0x3f }.SupportsUsbBootloader, Is.False);
        Assert.That(new DeviceInfo { Capabilities = 0x40 }.SupportsUsbBootloader, Is.True);
    }

    [Test]
    public void UnsupportedFirmwareIsRejectedBeforeHardwareAccess()
    {
        var selected = new UsbProfileDevice("absent", Serial, 1, UsbProfile.PcHid, 10);
        Assert.ThrowsAsync<NotSupportedException>(() => UsbBootloaderManagement.EnterAsync(selected, CancellationToken.None));
        Assert.ThrowsAsync<NotSupportedException>(() => UsbProfileManagement.EnterBootloaderAsync(selected, CancellationToken.None));
    }

    [Test]
    public void PcDongleIdentityRequiresProductManufacturerAndExactSerial()
    {
        Assert.That(UsbBootloaderManagement.MatchesPcDongle(0x1209, 0x2882, "BandHub", "BandHub Controller", Serial, Serial.ToLowerInvariant()), Is.True);
        Assert.That(UsbBootloaderManagement.MatchesPcDongle(0x1209, 0x2882, "Other", "BandHub Controller", Serial, Serial), Is.False);
        Assert.That(UsbBootloaderManagement.MatchesPcDongle(0x1209, 0x2882, "BandHub", "BandHub Xbox 360 Dongle", Serial, Serial), Is.False);
        Assert.That(UsbBootloaderManagement.MatchesPcDongle(0x1209, 0x2882, "BandHub", "BandHub Controller", "112233445566", Serial), Is.False);
    }

    [Test]
    public async Task CommandRequiresMatchingAcknowledgement()
    {
        var transport = new Transport();
        await UsbBootloaderManagement.EnterDongleAsync(transport, Serial, CancellationToken.None);
        Assert.That(transport.Sent, Is.Not.Null);
        Assert.That(transport.Sent![3], Is.EqualTo(14));
        Assert.That(BitConverter.ToUInt32(transport.Sent, 4), Is.Not.Zero);
        Assert.That(transport.Sent[16], Is.Zero);
        Assert.That(transport.Sent.AsSpan(17, 44).ToArray(), Is.All.Zero);
        var unanswered = new Transport { Acknowledge = false };
        Assert.ThrowsAsync<TimeoutException>(() => UsbBootloaderManagement.EnterDongleAsync(unanswered, Serial, CancellationToken.None));
        var rejected = new Transport { Reject = true };
        Assert.ThrowsAsync<IOException>(() => UsbBootloaderManagement.EnterDongleAsync(rejected, Serial, CancellationToken.None));
    }

    [TestCase(OtaState.Erasing)]
    [TestCase(OtaState.Receiving)]
    [TestCase(OtaState.Negotiating)]
    [TestCase(OtaState.Serving)]
    [TestCase(OtaState.AwaitingConfirmation)]
    [TestCase(OtaState.SelfReceiving)]
    [TestCase(OtaState.SelfVerifying)]
    [TestCase(OtaState.RebootPending)]
    public void ActiveUpdatesCannotBeInterrupted(OtaState state)
    {
        var transport = new Transport { State = state };
        Assert.ThrowsAsync<InvalidOperationException>(() => UsbBootloaderManagement.EnterDongleAsync(transport, Serial, CancellationToken.None));
        Assert.That(transport.Sent, Is.Null);
    }

    [Test]
    public void IdentityAndCapabilityAreRecheckedBeforeWriting()
    {
        var transport = new Transport { Descriptor = new() { SerialNumber = "112233445566" } };
        Assert.ThrowsAsync<NotSupportedException>(() => UsbBootloaderManagement.EnterDongleAsync(transport, Serial, CancellationToken.None));
        transport.Descriptor = new() { SerialNumber = Serial };
        transport.Info = new() { Capabilities = 0x3f, UsbProfile = 1 };
        Assert.ThrowsAsync<NotSupportedException>(() => UsbBootloaderManagement.EnterDongleAsync(transport, Serial, CancellationToken.None));
        transport.Info = new() { Capabilities = 0x40, UsbProfile = 3 };
        Assert.ThrowsAsync<NotSupportedException>(() => UsbBootloaderManagement.EnterDongleAsync(transport, Serial, CancellationToken.None));
        Assert.That(transport.Sent, Is.Null);
    }

    [Test]
    public async Task DisconnectWaitChecksTheSelectedInterfaceAndTimesOut()
    {
        var polls = 0;
        await UsbBootloaderManagement.WaitForDisconnectAsync(() => ++polls < 2, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.That(polls, Is.EqualTo(2));
        Assert.ThrowsAsync<TimeoutException>(() => UsbBootloaderManagement.WaitForDisconnectAsync(() => true, TimeSpan.Zero, CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(() => UsbBootloaderManagement.WaitForDisconnectAsync(() => throw new AssertionException("Unexpected hardware access"), TimeSpan.FromSeconds(1), cancellation.Token));
        var transport = new Transport();
        Assert.ThrowsAsync<OperationCanceledException>(() => UsbBootloaderManagement.EnterDongleAsync(transport, Serial, cancellation.Token));
        Assert.That(transport.Sent, Is.Null);
    }
}
