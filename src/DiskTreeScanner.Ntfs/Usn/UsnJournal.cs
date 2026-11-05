using Microsoft.Win32.SafeHandles;
using DiskTreeScanner.Core.Scanning;
using DiskTreeScanner.Ntfs.Interop;

namespace DiskTreeScanner.Ntfs.Usn;

/// <summary>USN change journal queries.</summary>
public static unsafe class UsnJournal
{
    /// <summary>Current journal id and next USN, or null when the journal is not active.</summary>
    public static UsnCheckpoint? Query(SafeFileHandle volume)
    {
        // V1/V2 structures extend V0: a larger buffer works on every Windows version.
        byte* buffer = stackalloc byte[128];
        if (!NtfsNative.DeviceIoControl(volume, NtfsNative.FSCTL_QUERY_USN_JOURNAL, null, 0, buffer, 128, out uint returned, 0)
            || returned < (uint)sizeof(NtfsNative.USN_JOURNAL_DATA_V0))
            return null;
        var data = *(NtfsNative.USN_JOURNAL_DATA_V0*)buffer;
        return new UsnCheckpoint(data.UsnJournalID, data.NextUsn);
    }

    /// <summary>Journal id, first and next USN.</summary>
    public static (ulong JournalId, long FirstUsn, long NextUsn)? QueryRange(SafeFileHandle volume)
    {
        byte* buffer = stackalloc byte[128];
        if (!NtfsNative.DeviceIoControl(volume, NtfsNative.FSCTL_QUERY_USN_JOURNAL, null, 0, buffer, 128, out uint returned, 0)
            || returned < (uint)sizeof(NtfsNative.USN_JOURNAL_DATA_V0))
            return null;
        var data = *(NtfsNative.USN_JOURNAL_DATA_V0*)buffer;
        return (data.UsnJournalID, data.FirstUsn, data.NextUsn);
    }
}
