using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BandHub.FirmwareUpdate.Core;

internal static class LinuxHidFeatureReport
{
    private const uint ReadWriteDirection = 3;
    private const uint HidIoctlType = (uint)'H';
    private const uint GetFeatureNumber = 0x07;
    private const int MaxIoctlSize = 0x3fff;

    public static byte[] Read(string hidDevicePath, byte reportId, int reportLength)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Native hidraw feature reports are available only on Linux.");
        }
        var buffer = new byte[reportLength];
        buffer[0] = reportId;
        var devicePath = ResolveDevicePath(hidDevicePath);
        using var stream = new FileStream(
            devicePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var result = Ioctl(stream.SafeFileHandle, BuildGetFeatureRequest(reportLength), buffer);
        if (result < 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Unable to read HID feature report from {devicePath}: " +
                new Win32Exception(error).Message);
        }
        ValidateResponse(buffer, result, reportId);
        return buffer;
    }

    internal static void ValidateResponse(byte[] buffer, int bytesRead, byte reportId)
    {
        if (bytesRead != buffer.Length || buffer.Length == 0 || buffer[0] != reportId)
        {
            throw new InvalidDataException(
                $"Invalid HID feature response for report 0x{reportId:X2}: " +
                $"received {bytesRead}/{buffer.Length} bytes, " +
                $"prefix {Convert.ToHexString(buffer.AsSpan(0, Math.Min(buffer.Length, 8)))}.");
        }
    }

    internal static nuint BuildGetFeatureRequest(int reportLength)
    {
        if (reportLength <= 0 || reportLength > MaxIoctlSize)
        {
            throw new ArgumentOutOfRangeException(nameof(reportLength));
        }
        return (nuint)((ReadWriteDirection << 30) |
                       ((uint)reportLength << 16) |
                       (HidIoctlType << 8) |
                       GetFeatureNumber);
    }

    internal static string ResolveDevicePath(string hidDevicePath)
    {
        var name = Path.GetFileName(hidDevicePath.TrimEnd(Path.DirectorySeparatorChar));
        const string prefix = "hidraw";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            name.Length == prefix.Length ||
            name[prefix.Length..].Any(character => !char.IsAsciiDigit(character)))
        {
            throw new InvalidOperationException(
                $"Unexpected Linux HID device path: {hidDevicePath}");
        }
        return $"/dev/{name}";
    }

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(
        SafeFileHandle fileDescriptor,
        nuint request,
        byte[] buffer);
}
