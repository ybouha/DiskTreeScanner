using DiskTreeScanner.Core.Platform;

namespace DiskTreeScanner.Core.Scanning;

/// <summary>What a path to scan is: volume, file system, drive kind, elevation.</summary>
public sealed class ScanTargetInfo
{
    /// <summary>Normalized full path. Drive roots end with a backslash (<c>C:\</c>).</summary>
    public required string Path { get; init; }

    /// <summary>Root of the volume holding <see cref="Path"/> (<c>C:\</c>, a mounted folder, <c>\\server\share\</c>).</summary>
    public string? VolumeRoot { get; init; }

    /// <summary>Device path for raw access (<c>\\.\C:</c> or <c>\\?\Volume{GUID}</c>), local volumes only.</summary>
    public string? VolumeDevicePath { get; init; }

    public string FileSystem { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public DriveKind Kind { get; init; }

    public bool IsVolumeRoot { get; init; }

    /// <summary><see cref="Path"/> relative to <see cref="VolumeRoot"/>, without leading separator.</summary>
    public string RelativePath { get; init; } = string.Empty;

    public bool IsElevated { get; init; }

    public string? WslDistribution { get; init; }

    public bool IsLocal => Kind is DriveKind.Fixed or DriveKind.Removable or DriveKind.RamDisk;

    public bool IsNtfs => string.Equals(FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);

    public static ScanTargetInfo Analyze(string path)
    {
        string full = Normalize(path);
        bool elevated = Elevation.IsElevated;

        if (WslDistributions.IsWslPath(full, out string distribution, out string uncRoot))
        {
            VolumeDetails? wsl = Volumes.GetDetails(uncRoot + @"\");
            return new ScanTargetInfo
            {
                Path = full,
                VolumeRoot = uncRoot + @"\",
                FileSystem = wsl?.FileSystem ?? "WSL",
                Label = distribution,
                Kind = DriveKind.Wsl,
                IsVolumeRoot = string.Equals(full, uncRoot, StringComparison.OrdinalIgnoreCase),
                RelativePath = full.Length > uncRoot.Length ? full[(uncRoot.Length + 1)..] : string.Empty,
                IsElevated = elevated,
                WslDistribution = distribution,
            };
        }

        // Follow junctions / symbolic links so the MFT engine finds the real folder.
        if (!full.StartsWith(@"\\", StringComparison.Ordinal) && !IsDriveRoot(full))
        {
            string? final = Volumes.ResolveFinalPath(full);
            if (final is not null && !string.Equals(final, full, StringComparison.OrdinalIgnoreCase))
                full = Normalize(final);
        }

        string? root = Volumes.GetVolumeRoot(full);
        VolumeDetails? details = root is null ? null : Volumes.GetDetails(root);
        DriveKind kind = full.StartsWith(@"\\", StringComparison.Ordinal) ? DriveKind.Network : root is null ? DriveKind.Unknown : Volumes.GetKind(root);

        string? device = null;
        if (root is not null && kind is DriveKind.Fixed or DriveKind.Removable or DriveKind.RamDisk)
        {
            if (IsDriveRoot(root))
                device = @"\\.\" + root[..2];
            else if (Volumes.GetVolumeGuidPath(root) is { } guid)
                device = guid.TrimEnd('\\');
        }

        string withSeparator = full.EndsWith('\\') ? full : full + @"\";
        bool isRoot = root is not null && string.Equals(withSeparator, root, StringComparison.OrdinalIgnoreCase);
        string relative = root is not null && withSeparator.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? withSeparator[root.Length..].TrimEnd('\\')
            : string.Empty;

        return new ScanTargetInfo
        {
            Path = full,
            VolumeRoot = root,
            VolumeDevicePath = device,
            FileSystem = details?.FileSystem ?? string.Empty,
            Label = details?.Label ?? string.Empty,
            Kind = kind,
            IsVolumeRoot = isRoot,
            RelativePath = relative,
            IsElevated = elevated,
        };
    }

    /// <summary>Full path; drive roots keep their trailing backslash, other paths lose it.</summary>
    public static string Normalize(string path)
    {
        string p = path.Trim().Trim('"');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) && p.Length >= 6 && p[5] == ':')
            p = p[4..];
        if (p.Length == 2 && p[1] == ':')
            p += @"\";
        p = System.IO.Path.GetFullPath(p);
        if (IsDriveRoot(p))
            return char.ToUpperInvariant(p[0]) + @":\";
        return p.TrimEnd('\\');
    }

    private static bool IsDriveRoot(string p) => p.Length == 3 && p[1] == ':' && p[2] == '\\';
}
