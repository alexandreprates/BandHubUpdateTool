using System;
using System.Buffers.Binary;
using System.IO;
using Newtonsoft.Json;

namespace BandHub.FirmwareUpdate.Core;

public sealed record ControllerCalibration
{
    public ushort WhammyMin { get; init; }
    public ushort WhammyMax { get; init; } = 4095;
    public byte Deadband { get; init; } = 4;
    public byte ReleaseMs { get; init; } = 5;
    public byte StrumGuardMs { get; init; } = 2;
    public bool Inverted { get; init; }
    public ushort DisconnectedSleepSeconds { get; init; } = 60;
    public ushort ConnectedSleepSeconds { get; init; } = 600;
    public bool SleepEnabled { get; init; }

    public void Validate()
    {
        if (WhammyMax > 4095 || WhammyMax - WhammyMin < 128 || Deadband > 32 ||
            ReleaseMs is < 1 or > 20 || StrumGuardMs > 10 ||
            DisconnectedSleepSeconds is < 60 or > 3600 ||
            ConnectedSleepSeconds < DisconnectedSleepSeconds || ConnectedSleepSeconds > 7200)
            throw new InvalidDataException("Calibration is outside the supported safe ranges.");
    }

    public byte[] Encode()
    {
        Validate();
        var data = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(data, WhammyMin);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), WhammyMax);
        data[4] = Deadband; data[5] = ReleaseMs; data[6] = StrumGuardMs;
        data[7] = Inverted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), DisconnectedSleepSeconds);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), ConnectedSleepSeconds);
        data[12] = SleepEnabled ? (byte)1 : (byte)0;
        return data;
    }

    public static ControllerCalibration Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || data[7] > 1 || data[12] > 1 || data[13] != 0 || data[14] != 0 || data[15] != 0)
            throw new InvalidDataException("Invalid calibration record.");
        var result = new ControllerCalibration
        {
            WhammyMin = BinaryPrimitives.ReadUInt16LittleEndian(data),
            WhammyMax = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            Deadband = data[4], ReleaseMs = data[5], StrumGuardMs = data[6], Inverted = data[7] != 0,
            DisconnectedSleepSeconds = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            ConnectedSleepSeconds = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]), SleepEnabled = data[12] != 0,
        };
        result.Validate();
        return result;
    }
}

public sealed record CalibrationFile(int Version, byte Profile, ControllerCalibration Calibration)
{
    public string Export() { Calibration.Validate(); return JsonConvert.SerializeObject(this, Formatting.Indented); }
    public static CalibrationFile Import(string json, byte profile, bool canSleep = true)
    {
        var result = JsonConvert.DeserializeObject<CalibrationFile>(json,
            new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });
        if (result == null || result.Version != 1 || result.Profile != profile || result.Calibration == null)
            throw new InvalidDataException("Choose a version 1 calibration file for this instrument profile.");
        result.Calibration.Validate();
        if (result.Calibration.SleepEnabled && !canSleep)
            throw new InvalidDataException("This calibration enables sleep, which the connected firmware does not support.");
        return result;
    }
}

public static class ControllerManagementProtocol
{
    public const byte CommandId = 0x30, ResponseId = 0x31;
    public static byte[] Request(byte operation, byte profile, uint id, byte[]? payload = null)
    {
        if (payload?.Length > 49) throw new ArgumentException("Payload too large.");
        var report = new byte[63];
        BinaryPrimitives.WriteUInt32LittleEndian(report, 0x46434842);
        report[4] = 1; report[5] = operation; report[7] = profile;
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(8), id);
        payload?.CopyTo(report, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(61), Crc(report.AsSpan(0, 61)));
        return report;
    }
    public static byte[] Response(byte[] feature, byte operation, uint id, byte? profile = null)
    {
        if (feature.Length != 64 || feature[0] != ResponseId)
            throw new InvalidDataException("Invalid management feature length or report ID.");
        var r = feature.AsSpan(1);
        if (BinaryPrimitives.ReadUInt32LittleEndian(r) != 0x46434842 || r[4] != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(r[61..]) != Crc(r[..61]))
            throw new InvalidDataException("Invalid management response version or checksum.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(r[8..]) != id || r[5] != operation)
            throw new ManagementPendingException();
        if (profile.HasValue && r[7] != profile.Value) throw new InvalidDataException("Instrument profile changed.");
        if (r[6] != 0) throw new InvalidOperationException(r[6] switch
        {
            2 => "Release all controls and wait one second before saving calibration.",
            3 => "Settings could not be saved. The previous calibration is still active.",
            4 => "This firmware does not support the requested operation.",
            _ => "The controller rejected these settings.",
        });
        return r.ToArray();
    }
    private static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xffff;
        foreach (var value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (var i = 0; i < 8; ++i) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }
}
public sealed class ManagementPendingException : Exception { }
