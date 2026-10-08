using System;

namespace BandHub.FirmwareUpdate.Core;

public enum FirmwareTarget : uint
{
    LegacyController = 1,
    ControllerGh3 = 0x0101,
    ControllerGh5 = 0x0102,
    ControllerGh5SuperMini = 0x0103,
    ControllerGh3SuperMini = 0x0104,
    DongleDevKitPc = 0x0201,
    DongleZeroPc = 0x0202,
}

public enum UsbProfile : byte
{
    PcHid = 1,
    Ps3RockBandGuitar = 2,
    Xbox360GuitarHero = 3,
}

public enum OtaCommand : byte
{
    Begin = 1,
    Data = 2,
    Finish = 3,
    Abort = 4,
    Clear = 5,
    BeginStagedController = 6,
    ArmController = 7,
    BeginDongle = 8,
    CommitDongle = 9,
    CancelController = 10,
    SetUsbProfile = 11,
    UnpairController = 12,
    UnpairDongle = 13,
    EnterBootloader = 14,
}

public enum OtaState : byte
{
    Idle = 0,
    Erasing = 1,
    Receiving = 2,
    Ready = 3,
    Error = 4,
    Negotiating = 5,
    Serving = 6,
    AwaitingConfirmation = 7,
    SelfReceiving = 8,
    SelfVerifying = 9,
    RebootPending = 10,
    ControllerStaged = 11,
}

public sealed class OtaStatus
{
    public OtaState State { get; init; }
    public byte Error { get; init; }
    public uint SessionId { get; init; }
    public uint BytesReceived { get; init; }
    public uint TotalSize { get; init; }
    public uint FirmwareVersion { get; init; }
    public uint ImageSize { get; init; }
    public uint ManifestCrc32 { get; init; }
    public uint OperationProgress { get; init; }
}

public sealed class DeviceInfo
{
    private const uint ControllerPackageV2Feature = 1U << 10;
    private const uint ControllerRemoteUnpairFeature = 1U << 12;

    public byte Capabilities { get; init; }
    public byte UsbProfile { get; init; }
    public FirmwareTarget DongleTarget { get; init; }
    public uint DongleFirmwareVersion { get; init; }
    public FirmwareTarget ControllerTarget { get; init; }
    public uint ControllerFirmwareVersion { get; init; }
    public uint ControllerFeatureFlags { get; init; }
    public byte ControllerBatteryPercent { get; init; }
    public byte Flags { get; init; }
    public byte[] DongleMac { get; init; } = Array.Empty<byte>();
    public byte[] ControllerMac { get; init; } = Array.Empty<byte>();

    public bool SupportsControllerOta => (Capabilities & 0x01) != 0;
    public bool SupportsDongleSelfOta => (Capabilities & 0x02) != 0;
    public bool SupportsUsbBootloader => (Capabilities & 0x40) != 0;
    public bool SupportsXbox360Profile => (Capabilities & 0x20) != 0;
    public bool SupportsUsbProfileSwitch => (Capabilities & 0x04) != 0;
    public bool SupportsLinkDiagnostics => (Capabilities & 0x10) != 0;
    public bool SupportsControllerManagement => (Capabilities & 0x80) != 0;
    public bool ControllerSupportsWirelessManagement => (ControllerFeatureFlags & (1U << 14)) != 0;
    public bool SupportsBindingManagement => (Capabilities & 0x08) != 0;
    public bool SupportsControllerPackageV2 =>
        (ControllerFeatureFlags & ControllerPackageV2Feature) != 0;
    public bool SupportsControllerRemoteUnpair =>
        (ControllerFeatureFlags & ControllerRemoteUnpairFeature) != 0;
    public bool ControllerBound => (Flags & 0x01) != 0;
    public bool ControllerConnected => (Flags & 0x02) != 0;
    public bool BatteryValid => (Flags & 0x04) != 0;
    public bool ControllerExternallyPowered => (Flags & 0x10) != 0;
    public string DongleId => BitConverter.ToString(DongleMac).Replace("-", string.Empty);
}

public static class OtaProtocol
{
    public static bool IsControllerTarget(FirmwareTarget target) => target is
        FirmwareTarget.ControllerGh3 or FirmwareTarget.ControllerGh5 or
        FirmwareTarget.ControllerGh3SuperMini or FirmwareTarget.ControllerGh5SuperMini;

    public static bool IsSuperMiniTarget(FirmwareTarget target) => target is
        FirmwareTarget.ControllerGh3SuperMini or FirmwareTarget.ControllerGh5SuperMini;

    private static readonly byte[] Ps3ReturnToPcPayload =
    {
        0x42, 0x48, 0x55, 0x53, 0x42, 0x50, 0x43, 0x01,
    };

    public const ushort VendorId = 0x1209;
    public const ushort ProductId = 0x2882;
    public const ushort Ps3VendorId = 0x12ba;
    public const ushort Ps3ProductId = 0x0200;
    public const byte CommandReportId = 0x20;
    public const byte StatusReportId = 0x21;
    public const byte DeviceInfoReportId = 0x22;
    public const int ReportBytes = 63;
    public const int CommandDataBytes = 44;

    public static byte[] EncodePs3ReturnToPcOutput(int outputReportLength)
    {
        if (outputReportLength < Ps3ReturnToPcPayload.Length + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(outputReportLength));
        }
        var output = new byte[outputReportLength];
        output[0] = 0;
        Buffer.BlockCopy(Ps3ReturnToPcPayload, 0, output, 1, Ps3ReturnToPcPayload.Length);
        return output;
    }

    public static byte[] EncodeUsbProfileCommand(uint sessionId, UsbProfile profile)
    {
        if (sessionId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }
        if (profile != UsbProfile.PcHid && profile != UsbProfile.Ps3RockBandGuitar && profile != UsbProfile.Xbox360GuitarHero)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }
        return EncodeCommand(
            OtaCommand.SetUsbProfile,
            sessionId,
            data: new[] { (byte)profile });
    }

    public static byte[] EncodeBindingCommand(uint sessionId, OtaCommand command)
    {
        if (command != OtaCommand.UnpairController &&
            command != OtaCommand.UnpairDongle)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }
        if (sessionId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }
        return EncodeCommand(command, sessionId);
    }

    public static byte[] EncodeCommand(
        OtaCommand command,
        uint sessionId,
        uint offset = 0,
        uint totalSize = 0,
        byte[]? data = null)
    {
        data ??= Array.Empty<byte>();
        if (data.Length > CommandDataBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(data));
        }

        var report = new byte[ReportBytes];
        WriteUInt16(report, 0, 0x544f);
        report[2] = 1;
        report[3] = (byte)command;
        WriteUInt32(report, 4, sessionId);
        WriteUInt32(report, 8, offset);
        WriteUInt32(report, 12, totalSize);
        report[16] = (byte)data.Length;
        Buffer.BlockCopy(data, 0, report, 17, data.Length);
        WriteUInt16(report, 61, Crc16(report, 61));
        return report;
    }

    public static OtaStatus ParseStatus(byte[] input)
    {
        var report = NormalizeFeatureReport(input, StatusReportId);
        ValidateReport(report, 0x544f);
        var bytesReceived = ReadUInt32(report, 10);
        var totalSize = ReadUInt32(report, 14);
        if (report[2] != 1 || report[5] != 0 ||
            report[3] > (byte)OtaState.ControllerStaged || report[4] > 19 ||
            bytesReceived > totalSize)
        {
            throw new InvalidOperationException("Unsupported OTA status report.");
        }

        return new OtaStatus
        {
            State = (OtaState)report[3],
            Error = report[4],
            SessionId = ReadUInt32(report, 6),
            BytesReceived = bytesReceived,
            TotalSize = totalSize,
            FirmwareVersion = ReadUInt32(report, 18),
            ImageSize = ReadUInt32(report, 22),
            ManifestCrc32 = ReadUInt32(report, 26),
            OperationProgress = ReadUInt32(report, 30),
        };
    }

    public static DeviceInfo ParseDeviceInfo(byte[] input)
    {
        var report = NormalizeFeatureReport(input, DeviceInfoReportId);
        ValidateReport(report, 0x4942);
        if (report[2] != 1 || report[5] != 0)
        {
            throw new InvalidOperationException("Unsupported device information report.");
        }

        var dongleMac = new byte[6];
        var controllerMac = new byte[6];
        Buffer.BlockCopy(report, 28, dongleMac, 0, dongleMac.Length);
        Buffer.BlockCopy(report, 34, controllerMac, 0, controllerMac.Length);
        return new DeviceInfo
        {
            Capabilities = report[3],
            UsbProfile = report[4],
            DongleTarget = (FirmwareTarget)ReadUInt32(report, 6),
            DongleFirmwareVersion = ReadUInt32(report, 10),
            ControllerTarget = (FirmwareTarget)ReadUInt32(report, 14),
            ControllerFirmwareVersion = ReadUInt32(report, 18),
            ControllerFeatureFlags = ReadUInt32(report, 22),
            ControllerBatteryPercent = report[26],
            Flags = report[27],
            DongleMac = dongleMac,
            ControllerMac = controllerMac,
        };
    }

    public static ushort Crc16(byte[] data, int length)
    {
        ushort crc = 0xffff;
        for (var index = 0; index < length; ++index)
        {
            crc ^= (ushort)(data[index] << 8);
            for (var bit = 0; bit < 8; ++bit)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
        }
        return crc;
    }

    internal static uint ReadUInt32(byte[] input, int offset) =>
        (uint)(input[offset] | input[offset + 1] << 8 |
               input[offset + 2] << 16 | input[offset + 3] << 24);

    internal static ushort ReadUInt16(byte[] input, int offset) =>
        (ushort)(input[offset] | input[offset + 1] << 8);

    internal static void WriteUInt32(byte[] output, int offset, uint value)
    {
        output[offset] = (byte)value;
        output[offset + 1] = (byte)(value >> 8);
        output[offset + 2] = (byte)(value >> 16);
        output[offset + 3] = (byte)(value >> 24);
    }

    internal static void WriteUInt16(byte[] output, int offset, ushort value)
    {
        output[offset] = (byte)value;
        output[offset + 1] = (byte)(value >> 8);
    }

    private static byte[] NormalizeFeatureReport(byte[] input, byte reportId)
    {
        if (input.Length == ReportBytes)
        {
            return input;
        }
        if (input.Length == ReportBytes + 1 && input[0] == reportId)
        {
            var report = new byte[ReportBytes];
            Buffer.BlockCopy(input, 1, report, 0, report.Length);
            return report;
        }
        throw new InvalidOperationException("Unexpected HID feature report length.");
    }

    private static void ValidateReport(byte[] report, ushort magic)
    {
        if (ReadUInt16(report, 0) != magic ||
            ReadUInt16(report, 61) != Crc16(report, 61))
        {
            throw new InvalidOperationException("Invalid HID report checksum or magic.");
        }
    }
}
