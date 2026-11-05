namespace DiskTreeScanner.Core.Model;

/// <summary>
/// Per-node flags. The low 23 bits mirror the Win32 FILE_ATTRIBUTE_* values so attributes can be copied
/// as is; the high byte carries DiskTreeScanner's own markers.
/// </summary>
[Flags]
public enum NodeFlags : uint
{
    None = 0,
    ReadOnly = 0x0000_0001,
    Hidden = 0x0000_0002,
    System = 0x0000_0004,
    Directory = 0x0000_0010,
    Archive = 0x0000_0020,
    Temporary = 0x0000_0100,
    Sparse = 0x0000_0200,
    ReparsePoint = 0x0000_0400,
    Compressed = 0x0000_0800,
    Offline = 0x0000_1000,
    Encrypted = 0x0000_4000,
    RecallOnOpen = 0x0004_0000,
    Pinned = 0x0008_0000,
    Unpinned = 0x0010_0000,
    RecallOnDataAccess = 0x0040_0000,

    /// <summary>Win32 attribute bits kept from the file system.</summary>
    AttributeMask = 0x007F_FFFF,

    /// <summary>Second or later name of a hard-linked file.</summary>
    HardLinkSecondary = 0x0100_0000,
    /// <summary>NTFS metadata file ($MFT, $LogFile, $Extend and its content...).</summary>
    NtfsSpecial = 0x0200_0000,
    /// <summary>Node created by DiskTreeScanner (the &lt;orphans&gt; folder).</summary>
    Synthetic = 0x0400_0000,
    /// <summary>Node re-attached under &lt;orphans&gt; (missing parent, stale reference or loop).</summary>
    Orphan = 0x0800_0000,
    /// <summary>Directory whose content could not be listed (directory engine).</summary>
    AccessDenied = 0x1000_0000,
    /// <summary>File carrying named data streams.</summary>
    HasAlternateStreams = 0x2000_0000,
    /// <summary>File compressed by the Windows Overlay Filter (CompactOS / compact /exe).</summary>
    WofCompressed = 0x4000_0000,
    /// <summary>Node removed by an incremental (USN) refresh; kept until the next re-layout.</summary>
    Deleted = 0x8000_0000,

    /// <summary>Cloud placeholder bits (OneDrive and other cloud filter providers).</summary>
    CloudPlaceholderMask = Offline | RecallOnOpen | RecallOnDataAccess,
}
