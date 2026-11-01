using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EasySearcher.Core.Interop;

internal static class NativeMethods
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;

    public const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    public const int ERROR_JOURNAL_NOT_ACTIVE = 1179;
    public const int ERROR_JOURNAL_ENTRY_DELETED = 1181;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    // Pack = 4: los FILETIME son dos DWORD, no estan alineados a 8 bytes
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct WIN32_FILE_ATTRIBUTE_DATA
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileAttributesEx(string fileName, int infoLevelId, out WIN32_FILE_ATTRIBUTE_DATA data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode,
        byte[]? inBuffer, int inBufferSize,
        byte[] outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);
}
