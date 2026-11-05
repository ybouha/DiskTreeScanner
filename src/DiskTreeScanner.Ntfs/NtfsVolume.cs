using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DiskTreeScanner.Ntfs.Interop;

namespace DiskTreeScanner.Ntfs;

/// <summary>Geometry returned by FSCTL_GET_NTFS_VOLUME_DATA.</summary>
public readonly record struct NtfsVolumeData(
    long SerialNumber,
    long TotalClusters,
    long FreeClusters,
    int BytesPerSector,
    int BytesPerCluster,
    int BytesPerFileRecordSegment,
    long MftValidDataLength,
    long MftStartLcn)
{
    public long TotalBytes => TotalClusters * BytesPerCluster;

    public long FreeBytes => FreeClusters * BytesPerCluster;
}

/// <summary>
/// A raw NTFS volume opened read-only with <c>FILE_FLAG_NO_BUFFERING</c>: reads bypass the cache and
/// must be sector-aligned (offset, length and buffer address). A synchronous handle serves the
/// FSCTLs and small reads; an overlapped handle serves the bulk MFT reads (several in flight).
/// </summary>
public sealed unsafe class NtfsVolume : IDisposable
{
    private SafeFileHandle? _asyncHandle;

    private NtfsVolume(string devicePath, SafeFileHandle handle, NtfsVolumeData data)
    {
        DevicePath = devicePath;
        Handle = handle;
        Data = data;
    }

    public string DevicePath { get; }

    /// <summary>Synchronous handle (FSCTLs, small reads).</summary>
    public SafeFileHandle Handle { get; }

    public NtfsVolumeData Data { get; }

    /// <summary>Overlapped handle for queued reads, opened on first use.</summary>
    public SafeFileHandle AsyncHandle => _asyncHandle ??= OpenHandle(DevicePath, NtfsNative.FILE_FLAG_NO_BUFFERING | NtfsNative.FILE_FLAG_OVERLAPPED);

    /// <summary>Opens <c>\\.\C:</c> (or <c>\\?\Volume{GUID}</c>). Requires administrator rights.</summary>
    public static NtfsVolume Open(string devicePath)
    {
        SafeFileHandle handle = OpenHandle(devicePath, NtfsNative.FILE_FLAG_NO_BUFFERING);
        try
        {
            NtfsNative.NTFS_VOLUME_DATA_BUFFER buffer;
            if (!NtfsNative.DeviceIoControl(handle, NtfsNative.FSCTL_GET_NTFS_VOLUME_DATA, null, 0, &buffer, (uint)sizeof(NtfsNative.NTFS_VOLUME_DATA_BUFFER), out _, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"FSCTL_GET_NTFS_VOLUME_DATA failed on {devicePath}");
            var data = new NtfsVolumeData(
                buffer.VolumeSerialNumber,
                buffer.TotalClusters,
                buffer.FreeClusters,
                (int)buffer.BytesPerSector,
                (int)buffer.BytesPerCluster,
                (int)buffer.BytesPerFileRecordSegment,
                buffer.MftValidDataLength,
                buffer.MftStartLcn);
            Validate(data);
            return new NtfsVolume(devicePath, handle, data);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenHandle(string devicePath, uint flags)
    {
        SafeFileHandle handle = NtfsNative.CreateFile(devicePath, NtfsNative.GENERIC_READ, NtfsNative.FILE_SHARE_READ | NtfsNative.FILE_SHARE_WRITE, 0, NtfsNative.OPEN_EXISTING, flags, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Cannot open {devicePath} for raw reading: {new Win32Exception(error).Message}");
        }
        return handle;
    }

    private static void Validate(NtfsVolumeData d)
    {
        static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;
        if (!IsPowerOfTwo(d.BytesPerSector) || !IsPowerOfTwo(d.BytesPerCluster) || !IsPowerOfTwo(d.BytesPerFileRecordSegment)
            || d.BytesPerFileRecordSegment < 512 || d.BytesPerCluster < d.BytesPerSector || d.MftValidDataLength <= 0 || d.MftStartLcn <= 0)
            throw new InvalidDataException($"Unexpected NTFS geometry: {d}");
    }

    /// <summary>Synchronous read; <paramref name="offset"/>, <paramref name="length"/> and the buffer must be sector-aligned.</summary>
    public void Read(long offset, byte* buffer, int length)
    {
        int done = 0;
        while (done < length)
        {
            var overlapped = new NativeOverlapped
            {
                OffsetLow = (int)(uint)((offset + done) & 0xFFFF_FFFF),
                OffsetHigh = (int)((offset + done) >> 32),
            };
            uint read;
            if (!NtfsNative.ReadFile(Handle, buffer + done, (uint)(length - done), &read, &overlapped))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Raw read of {length} bytes at offset {offset} failed");
            if (read == 0)
                throw new EndOfStreamException($"Raw read at offset {offset + done} returned no data");
            done += (int)read;
        }
    }

    public void Dispose()
    {
        _asyncHandle?.Dispose();
        Handle.Dispose();
    }
}

/// <summary>One overlapped read with its own event, reusable.</summary>
internal sealed unsafe class AsyncRead : IDisposable
{
    private readonly NativeOverlapped* _overlapped;
    private readonly nint _event;
    private int _expected;

    public AsyncRead()
    {
        _overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        _event = NtfsNative.CreateEvent(0, manualReset: true, initialState: false, 0);
        if (_event == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEvent failed");
        _overlapped->EventHandle = _event;
    }

    public bool Pending { get; private set; }

    public void Start(SafeFileHandle handle, long offset, byte* buffer, int length)
    {
        _overlapped->OffsetLow = (int)(uint)(offset & 0xFFFF_FFFF);
        _overlapped->OffsetHigh = (int)(offset >> 32);
        _overlapped->InternalLow = 0;
        _overlapped->InternalHigh = 0;
        _expected = length;
        if (!NtfsNative.ReadFile(handle, buffer, (uint)length, null, _overlapped))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != NtfsNative.ERROR_IO_PENDING)
                throw new Win32Exception(error, $"Raw read of {length} bytes at offset {offset} failed");
        }
        Pending = true;
    }

    /// <summary>Waits for completion; throws on error or short read.</summary>
    public void Wait(SafeFileHandle handle)
    {
        Pending = false;
        if (!NtfsNative.GetOverlappedResult(handle, _overlapped, out uint read, wait: true))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Raw MFT read failed");
        if (read != _expected)
            throw new EndOfStreamException($"Short raw read: {read} of {_expected} bytes");
    }

    /// <summary>Waits for completion ignoring the result (after a cancellation).</summary>
    public void WaitQuietly(SafeFileHandle handle)
    {
        if (!Pending)
            return;
        Pending = false;
        NtfsNative.GetOverlappedResult(handle, _overlapped, out _, wait: true);
    }

    public void Dispose()
    {
        NtfsNative.CloseHandle(_event);
        NativeMemory.Free(_overlapped);
    }
}
