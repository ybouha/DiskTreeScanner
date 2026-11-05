using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskTreeScanner.Ntfs.Interop;

/// <summary>Raw volume access (source-generated P/Invoke).</summary>
internal static unsafe partial class NtfsNative
{
    public const uint GENERIC_READ = 0x8000_0000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_NO_BUFFERING = 0x2000_0000;
    public const uint FILE_FLAG_OVERLAPPED = 0x4000_0000;
    public const int ERROR_IO_PENDING = 997;

    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x0009_0064;
    public const uint FSCTL_GET_NTFS_FILE_RECORD = 0x0009_0068;
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x0009_00F4;
    public const uint FSCTL_READ_USN_JOURNAL = 0x0009_00BB;

    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    public const int ERROR_JOURNAL_NOT_ACTIVE = 1179;
    public const int ERROR_JOURNAL_ENTRY_DELETED = 1181;

    [StructLayout(LayoutKind.Sequential)]
    public struct NTFS_VOLUME_DATA_BUFFER
    {
        public long VolumeSerialNumber;
        public long NumberSectors;
        public long TotalClusters;
        public long FreeClusters;
        public long TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public long MftValidDataLength;
        public long MftStartLcn;
        public long Mft2StartLcn;
        public long MftZoneStart;
        public long MftZoneEnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct USN_JOURNAL_DATA_V0
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct READ_USN_JOURNAL_DATA_V0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize, void* outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(SafeFileHandle file, void* buffer, uint numberOfBytesToRead, uint* numberOfBytesRead, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetOverlappedResult(SafeFileHandle file, NativeOverlapped* overlapped, out uint numberOfBytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelIoEx(SafeFileHandle file, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true)]
    public static partial nint CreateEvent(nint eventAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);
}
