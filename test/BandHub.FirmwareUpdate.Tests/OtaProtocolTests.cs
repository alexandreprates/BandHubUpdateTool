using System;
using BandHub.FirmwareUpdate.Core;
using NUnit.Framework;

namespace BandHub.FirmwareUpdate.Tests;

[TestFixture]
public sealed class OtaProtocolTests
{
    [Test]
    public void EncodeCommandMatchesFirmwareWireLayout()
    {
        var report = OtaProtocol.EncodeCommand(
            OtaCommand.BeginDongle, 0x12345678, totalSize: 0x00045678);

        Assert.That(report, Has.Length.EqualTo(63));
        Assert.That(report[0], Is.EqualTo(0x4f));
        Assert.That(report[1], Is.EqualTo(0x54));
        Assert.That(report[3], Is.EqualTo((byte)OtaCommand.BeginDongle));
        Assert.That(BitConverter.ToUInt32(report, 4), Is.EqualTo(0x12345678));
        Assert.That(BitConverter.ToUInt32(report, 12), Is.EqualTo(0x00045678));
        Assert.That(BitConverter.ToUInt16(report, 61), Is.EqualTo(OtaProtocol.Crc16(report, 61)));
    }

    [Test]
    public void EncodeCancelControllerUsesDedicatedCommand()
    {
        var report = OtaProtocol.EncodeCommand(OtaCommand.CancelController, 7);

        Assert.That(report[3], Is.EqualTo(10));
        Assert.That(BitConverter.ToUInt32(report, 4), Is.EqualTo(7));
        Assert.That(BitConverter.ToUInt16(report, 61), Is.EqualTo(OtaProtocol.Crc16(report, 61)));
    }

    [Test]
    public void ParseDeviceInfoReturnsExactHardwareIdentity()
    {
        var report = new byte[64];
        report[0] = OtaProtocol.DeviceInfoReportId;
        Write16(report, 1, 0x4942);
        report[3] = 1;
        report[4] = 0x03;
        report[5] = 1;
        Write32(report, 7, (uint)FirmwareTarget.DongleZeroPc);
        Write32(report, 11, 0x00030000);
        Write32(report, 15, (uint)FirmwareTarget.ControllerGh5);
        Write32(report, 19, 0x00020000);
        Write32(report, 23, 1U << 10);
        report[27] = 72;
        report[28] = 0x07;
        for (var index = 0; index < 6; ++index)
        {
            report[29 + index] = (byte)(0xa0 + index);
        }
        var payload = new byte[63];
        Buffer.BlockCopy(report, 1, payload, 0, payload.Length);
        Write16(report, 62, OtaProtocol.Crc16(payload, 61));

        var info = OtaProtocol.ParseDeviceInfo(report);

        Assert.That(info.DongleTarget, Is.EqualTo(FirmwareTarget.DongleZeroPc));
        Assert.That(info.ControllerTarget, Is.EqualTo(FirmwareTarget.ControllerGh5));
        Assert.That(info.SupportsDongleSelfOta, Is.True);
        Assert.That(info.SupportsControllerPackageV2, Is.True);
        Assert.That(info.ControllerConnected, Is.True);
        Assert.That(info.ControllerBatteryPercent, Is.EqualTo(72));
    }

    [Test]
    public void ParseStatusRejectsCorruptedCrc()
    {
        var report = new byte[63];
        Write16(report, 0, 0x544f);
        report[2] = 1;
        report[3] = (byte)OtaState.Idle;
        Write16(report, 61, OtaProtocol.Crc16(report, 61));
        report[10] ^= 0x01;

        Assert.Throws<InvalidOperationException>(() => OtaProtocol.ParseStatus(report));
    }

    [Test]
    public void ParseStatusRejectsImpossibleProgress()
    {
        var report = new byte[63];
        Write16(report, 0, 0x544f);
        report[2] = 1;
        report[3] = (byte)OtaState.Receiving;
        Write32(report, 10, 101);
        Write32(report, 14, 100);
        Write16(report, 61, OtaProtocol.Crc16(report, 61));

        Assert.Throws<InvalidOperationException>(() => OtaProtocol.ParseStatus(report));
    }

    private static void Write16(byte[] output, int offset, ushort value)
    {
        output[offset] = (byte)value;
        output[offset + 1] = (byte)(value >> 8);
    }

    private static void Write32(byte[] output, int offset, uint value)
    {
        output[offset] = (byte)value;
        output[offset + 1] = (byte)(value >> 8);
        output[offset + 2] = (byte)(value >> 16);
        output[offset + 3] = (byte)(value >> 24);
    }
}
