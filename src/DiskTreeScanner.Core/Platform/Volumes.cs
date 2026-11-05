using Microsoft.Win32.SafeHandles;
using DiskTreeScanner.Core.Interop;

namespace DiskTreeScanner.Core.Platform;

public enum DriveKind
{
    Unknown,
    Fixed,
    Removable,
    Network,
    Wsl,
    CdRom,
    RamDisk,
}

public enum MediaKind
{
    Unknown,
    Ssd,
    Hdd,
}

/// <summary>Result of GetVolumeInformation.</summary>
public sealed record VolumeDetails(string Label, string FileSystem, uint SerialNumber, int MaxComponentLength, uint Flags);

/// <summary>Volume queries (no admin rights needed).</summary>
public static unsafe class Volumes
{
    /// <summary>Root of the volume holding <paramref name="path"/> (e.g. <c>C:\</c> or a mounted folder).</summary>
    public static string? GetVolumeRoot(string path)
    {
        char* buffer = stackalloc char[1024];
        if (!Win32.GetVolumePathName(path, buffer, 1024))
            return null;
        return new string(buffer);
    }

    public static VolumeDetails? GetDetails(string root)
    {
        char* label = stackalloc char[261];
        char* fileSystem = stackalloc char[261];
        if (!Win32.GetVolumeInformation(root, label, 261, out uint serial, out uint maxComponent, out uint flags, fileSystem, 261))
            return null;
        return new VolumeDetails(new string(label), new string(fileSystem), serial, (int)maxComponent, flags);
    }

    /// <summary><c>\\?\Volume{GUID}\</c> name of a local volume root.</summary>
    public static string? GetVolumeGuidPath(string root)
    {
        char* buffer = stackalloc char[64];
        if (!Win32.GetVolumeNameForVolumeMountPoint(root, buffer, 64))
            return null;
        return new string(buffer);
    }

    public static DriveKind GetKind(string root) => Win32.GetDriveType(root) switch
    {
        Win32.DRIVE_FIXED => DriveKind.Fixed,
        Win32.DRIVE_REMOVABLE => DriveKind.Removable,
        Win32.DRIVE_REMOTE => DriveKind.Network,
        Win32.DRIVE_CDROM => DriveKind.CdRom,
        Win32.DRIVE_RAMDISK => DriveKind.RamDisk,
        _ => DriveKind.Unknown,
    };

    /// <summary>Total and free bytes of the volume holding <paramref name="path"/>.</summary>
    public static (long Total, long Free) GetSpace(string path)
    {
        if (Win32.GetDiskFreeSpaceEx(path, out _, out ulong total, out ulong free))
            return ((long)total, (long)free);
        return (0, 0);
    }

    public static int GetClusterSize(string root)
    {
        if (Win32.GetDiskFreeSpace(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
            return (int)(sectorsPerCluster * bytesPerSector);
        return 4096;
    }

    /// <summary>SSD or HDD, from the device seek penalty (works without admin rights).</summary>
    public static MediaKind GetMediaKind(string devicePath)
    {
        using SafeFileHandle handle = Win32.CreateFile(devicePath, 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, 0, Win32.OPEN_EXISTING, 0, 0);
        if (handle.IsInvalid)
            return MediaKind.Unknown;
        var query = new Win32.STORAGE_PROPERTY_QUERY { PropertyId = Win32.StorageDeviceSeekPenaltyProperty, QueryType = 0 };
        Win32.DEVICE_SEEK_PENALTY_DESCRIPTOR descriptor;
        if (!Win32.DeviceIoControl(handle, Win32.IOCTL_STORAGE_QUERY_PROPERTY, &query, (uint)sizeof(Win32.STORAGE_PROPERTY_QUERY), &descriptor, (uint)sizeof(Win32.DEVICE_SEEK_PENALTY_DESCRIPTOR), out _, 0))
            return MediaKind.Unknown;
        return descriptor.IncursSeekPenalty != 0 ? MediaKind.Hdd : MediaKind.Ssd;
    }

    /// <summary>Final path of a directory, following junctions and symbolic links; null if it cannot be opened.</summary>
    public static string? ResolveFinalPath(string path)
    {
        using SafeFileHandle handle = Win32.CreateFile(path, Win32.FILE_READ_ATTRIBUTES, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE, 0, Win32.OPEN_EXISTING, Win32.FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (handle.IsInvalid)
            return null;
        char* buffer = stackalloc char[1024];
        uint length = Win32.GetFinalPathNameByHandle(handle, buffer, 1024, 0);
        if (length == 0 || length >= 1024)
            return null;
        string final = new(buffer, 0, (int)length);
        if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + final[8..];
        if (final.StartsWith(@"\\?\", StringComparison.Ordinal) && final.Length >= 6 && final[5] == ':')
            return final[4..];
        return final;
    }
}
