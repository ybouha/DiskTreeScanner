using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskTreeScanner.Core.Interop;

/// <summary>Win32 / NT entry points used by the Core (source-generated P/Invoke).</summary>
internal static unsafe partial class Win32
{
    public const uint FILE_LIST_DIRECTORY = 0x0001;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint SYNCHRONIZE = 0x0010_0000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint FILE_SHARE_DELETE = 0x4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x0200_0000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x0020_0000;

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_NO_MORE_FILES = 18;

    public const uint DRIVE_REMOVABLE = 2;
    public const uint DRIVE_FIXED = 3;
    public const uint DRIVE_REMOTE = 4;
    public const uint DRIVE_CDROM = 5;
    public const uint DRIVE_RAMDISK = 6;

    public const int FileIdBothDirectoryInformation = 37;
    public const int STATUS_SUCCESS = 0;
    public const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);
    public const int STATUS_NO_MORE_FILES = unchecked((int)0x80000006);
    public const int STATUS_INVALID_INFO_CLASS = unchecked((int)0xC0000003);
    public const int STATUS_INVALID_PARAMETER = unchecked((int)0xC000000D);
    public const int STATUS_NOT_SUPPORTED = unchecked((int)0xC00000BB);
    public const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);

    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const int StorageDeviceSeekPenaltyProperty = 7;

    public const int FindExInfoBasic = 1;
    public const int FindExSearchNameMatch = 0;
    public const int FIND_FIRST_EX_LARGE_FETCH = 2;

    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_STATUS_BLOCK
    {
        public nint Status;
        public nint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME_RAW
    {
        public uint Low;
        public uint High;

        public readonly long Value => (long)(((ulong)High << 32) | Low);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME_RAW ftCreationTime;
        public FILETIME_RAW ftLastAccessTime;
        public FILETIME_RAW ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        public fixed char cFileName[260];
        public fixed char cAlternateFileName[14];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES_1
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true)]
    public static partial SafeFileHandle CreateFile(char* fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryDirectoryFile(
        SafeFileHandle fileHandle,
        nint @event,
        nint apcRoutine,
        nint apcContext,
        IO_STATUS_BLOCK* ioStatusBlock,
        void* fileInformation,
        uint length,
        int fileInformationClass,
        byte returnSingleEntry,
        UNICODE_STRING* fileName,
        byte restartScan);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", SetLastError = true)]
    public static partial nint FindFirstFileEx(char* fileName, int infoLevel, WIN32_FIND_DATAW* findData, int searchOp, nint searchFilter, int additionalFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextFile(nint findHandle, WIN32_FIND_DATAW* findData);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindClose(nint findHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathName(string fileName, char* volumePathName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformation(string rootPathName, char* volumeNameBuffer, uint volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, char* fileSystemNameBuffer, uint fileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, char* volumeName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetDriveType(string rootPathName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpace(string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector, out uint numberOfFreeClusters, out uint totalNumberOfClusters);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    public static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* filePath, uint filePathLength, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize, void* outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(nint tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, TOKEN_PRIVILEGES_1* newState, uint bufferLength, nint previousState, nint returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);
}
