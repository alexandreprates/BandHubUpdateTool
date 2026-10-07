using System;
using System.IO;
using System.Linq;
using System.Text;

namespace BandHub.FirmwareUpdate.Core;

public enum ControllerUpdateCommand : byte { Begin = 1, Data, Finish, Commit, Abort }
public enum ControllerUpdateState : byte { Idle, Receiving, Ready, RebootPending, Error }
public enum ControllerImageHealth : byte { Pending, Healthy, Failed }

public sealed record ControllerUpdateInfo(
    string Serial, FirmwareTarget Target, uint FirmwareVersion, uint MaximumImageSize,
    bool SupportsUpdate, ControllerImageHealth Health);

public sealed record ControllerUpdateStatus(
    ControllerUpdateState State, byte Error, ControllerUpdateCommand Command,
    uint SessionId, uint BytesReceived, uint TotalSize, uint FirmwareVersion);

public static class ControllerUsbUpdateProtocol
{
    public const byte CommandReportId = 0x40;
    public const byte StatusReportId = 0x41;
    public const byte InfoReportId = 0x42;
    public const int DataBytes = 44;
    public const uint MaximumImageSize = 0x140000;

    public static byte[] Encode(ControllerUpdateCommand command, uint session,
        uint offset = 0, uint total = 0, byte[]? data = null)
    {
        data ??= Array.Empty<byte>();
        if (session == 0 || !Enum.IsDefined(command) || data.Length > DataBytes ||
            (command == ControllerUpdateCommand.Begin && (offset != 0 || data.Length != 0 ||
                total <= FirmwarePackage.HeaderBytes || total > FirmwarePackage.HeaderBytes + MaximumImageSize)) ||
            (command == ControllerUpdateCommand.Data && (data.Length == 0 || total != 0)) ||
            (command is not (ControllerUpdateCommand.Begin or ControllerUpdateCommand.Data) &&
                (offset != 0 || total != 0 || data.Length != 0)))
            throw new ArgumentException("Invalid Controller USB update command.");
        var payload = new byte[63];
        OtaProtocol.WriteUInt16(payload, 0, 0x5543);
        payload[2] = 1; payload[3] = (byte)command;
        OtaProtocol.WriteUInt32(payload, 4, session);
        OtaProtocol.WriteUInt32(payload, 8, offset);
        OtaProtocol.WriteUInt32(payload, 12, total);
        payload[16] = (byte)data.Length;
        data.CopyTo(payload, 17);
        OtaProtocol.WriteUInt16(payload, 61, OtaProtocol.Crc16(payload, 61));
        return new[] { CommandReportId }.Concat(payload).ToArray();
    }

    public static ControllerUpdateStatus DecodeStatus(byte[] report)
    {
        var p = Payload(report, StatusReportId);
        if (p[3] > 4 || p[4] > 10 || p[5] > 5 || p.AsSpan(22, 39).ContainsAnyExcept((byte)0) ||
            OtaProtocol.ReadUInt32(p, 10) > OtaProtocol.ReadUInt32(p, 14) ||
            OtaProtocol.ReadUInt32(p, 14) > MaximumImageSize + FirmwarePackage.HeaderBytes)
            throw new InvalidDataException("Invalid Controller USB update status.");
        return new((ControllerUpdateState)p[3], p[4], (ControllerUpdateCommand)p[5],
            OtaProtocol.ReadUInt32(p, 6), OtaProtocol.ReadUInt32(p, 10),
            OtaProtocol.ReadUInt32(p, 14), OtaProtocol.ReadUInt32(p, 18));
    }

    public static ControllerUpdateInfo DecodeInfo(byte[] report)
    {
        var p = Payload(report, InfoReportId);
        var target = (FirmwareTarget)OtaProtocol.ReadUInt32(p, 6);
        var serial = Encoding.ASCII.GetString(p, 18, 12);
        var maximum = OtaProtocol.ReadUInt32(p, 14);
        if (p[3] != 1 || (p[4] & ~1) != 0 || p[5] > 2 ||
            !OtaProtocol.IsSuperMiniTarget(target) || OtaProtocol.ReadUInt32(p, 10) == 0 ||
            maximum == 0 || maximum > MaximumImageSize || !ValidSerial(serial) ||
            p.AsSpan(30, 31).ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Unsupported Controller USB update identity.");
        return new(serial, target, OtaProtocol.ReadUInt32(p, 10), maximum,
            (p[4] & 1) != 0, (ControllerImageHealth)p[5]);
    }

    internal static bool ValidSerial(string serial) => serial.Length == 12 &&
        serial.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static byte[] Payload(byte[] report, byte id)
    {
        if (report.Length != 64 || report[0] != id)
            throw new InvalidDataException("Invalid Controller USB report length or ID.");
        var p = report.AsSpan(1).ToArray();
        if (OtaProtocol.ReadUInt16(p, 0) != 0x5543 || p[2] != 1 ||
            OtaProtocol.ReadUInt16(p, 61) != OtaProtocol.Crc16(p, 61))
            throw new InvalidDataException("Invalid Controller USB report CRC or version.");
        return p;
    }
}
