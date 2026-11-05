using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DiskTreeScanner.Core.Model;
using DiskTreeScanner.Core.Scanning;
using DiskTreeScanner.Ntfs.Mft;
using DiskTreeScanner.Ntfs.Usn;

namespace DiskTreeScanner.Cli;

/// <summary>
/// End-to-end check of the USN refresh: a scratch folder is scanned, changed (data only, then
/// structure), refreshed from the journal, and compared with a fresh full scan. On a busy volume
/// other programs' changes make every refresh structural; on a quiet one the data-only step takes
/// the "ancestors only" path.
/// </summary>
internal static partial class UsnTest
{
    public static int Run(string baseFolder)
    {
        string folder = Path.Combine(Path.GetFullPath(baseFolder), "DiskTreeScanner.UsnTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        Write(Path.Combine(folder, "a.bin"), 100_000);
        Write(Path.Combine(folder, "b.txt"), 10);
        Write(Path.Combine(folder, @"sub\c.bin"), 2_000_000);
        var log = new ActivityLog();
        var engine = new MftScanEngine();
        var options = new ScanOptions();
        int failures = 0;
        try
        {
            ScanTargetInfo target = ScanTargetInfo.Analyze(folder);
            FlushVolume(target.VolumeDevicePath!);
            ScanResult scan = engine.Scan(target, options, new ScanProgress(), log, CancellationToken.None);
            Console.WriteLine($"Initial scan: {scan.Model.ItemCount(0):N0} items, folder {Totals(scan.Model, scan.ViewRoot)}");

            // 1. Data only: the file grows.
            using (var stream = new FileStream(Path.Combine(folder, "a.bin"), FileMode.Append))
                stream.Write(new byte[50_000]);
            UsnRefreshResult? data = UsnRefresher.TryRefresh(scan, log, CancellationToken.None);
            failures += Check(data is not null, "data refresh returned a result");
            if (data is not null)
            {
                TreeModel m = data.Result.Model;
                int file = m.FindRelative(scan.Target.RelativePath + @"\a.bin");
                Console.WriteLine($"Data change: {data.Changes} changed records in {data.Duration.TotalMilliseconds:F1} ms, structural={data.Structural}");
                failures += Check(file >= 0 && m.Size[file] == 150_000, $"a.bin size after refresh = {(file >= 0 ? m.Size[file] : -1):N0} (expected 150,000)");
                failures += Check(m.Size[data.Result.ViewRoot] == scan.Model.Size[scan.ViewRoot] + 50_000, "folder size grew by 50,000");
                scan = data.Result;
            }

            // 2. Structure: create, delete, rename, new folder, hard link.
            Write(Path.Combine(folder, "d.bin"), 300_000);
            File.Delete(Path.Combine(folder, "b.txt"));
            File.Move(Path.Combine(folder, @"sub\c.bin"), Path.Combine(folder, @"sub\c-renamed.bin"));
            Directory.CreateDirectory(Path.Combine(folder, @"sub2\deeper"));
            Write(Path.Combine(folder, @"sub2\deeper\e.bin"), 70_000);
            if (!CreateHardLink(Path.Combine(folder, @"sub2\link to d.bin"), Path.Combine(folder, "d.bin"), 0))
                Console.WriteLine($"  (hard link not created: error {Marshal.GetLastWin32Error()})");
            var clock = Stopwatch.StartNew();
            UsnRefreshResult? structure = UsnRefresher.TryRefresh(scan, log, CancellationToken.None);
            clock.Stop();
            failures += Check(structure is not null, "structural refresh returned a result");
            if (structure is not null)
            {
                Console.WriteLine($"Structural change: {structure.Changes} changed records in {structure.Duration.TotalMilliseconds:F1} ms, structural={structure.Structural}");
                FlushVolume(target.VolumeDevicePath!);
                ScanResult fresh = engine.Scan(target, options, new ScanProgress(), log, CancellationToken.None);
                TreeModel r = structure.Result.Model, f = fresh.Model;
                int rf = structure.Result.ViewRoot, ff = fresh.ViewRoot;
                Console.WriteLine($"  refreshed: {Totals(r, rf)}");
                Console.WriteLine($"  fresh    : {Totals(f, ff)}");
                failures += Check(r.Size[rf] == f.Size[ff] && r.Allocated[rf] == f.Allocated[ff], "folder size and allocation match a fresh scan");
                failures += Check(r.ItemCount(rf) == f.ItemCount(ff) && r.FolderCount(rf) == f.FolderCount(ff), "folder items and folders match a fresh scan");
                failures += Check(r.FindRelative(scan.Target.RelativePath + @"\b.txt") < 0, "deleted b.txt is gone");
                failures += Check(r.FindRelative(scan.Target.RelativePath + @"\sub\c-renamed.bin") >= 0 && r.FindRelative(scan.Target.RelativePath + @"\sub\c.bin") < 0, "c.bin renamed");
                failures += Check(r.FindRelative(scan.Target.RelativePath + @"\sub2\deeper\e.bin") >= 0, "new nested folder and file present");
                long volumeDelta = r.Size[0] - f.Size[0];
                Console.WriteLine($"  whole volume: refreshed {r.Size[0]:N0} vs fresh {f.Size[0]:N0} bytes " +
                    (volumeDelta == 0 ? "(identical)" : $"(difference {volumeDelta:N0}: other programs kept writing between the two)"));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                Directory.Delete(Path.GetDirectoryName(folder)!); // only if empty
            }
            catch (IOException)
            {
            }
        }
        Console.WriteLine(failures == 0 ? "USN refresh test: PASSED" : $"USN refresh test: {failures} check(s) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static string Totals(TreeModel m, int node) =>
        $"size {m.Size[node]:N0}, allocated {m.Allocated[node]:N0}, items {m.ItemCount(node)}, files {m.FileCount(node)}, folders {m.FolderCount(node)}";

    private static int Check(bool condition, string what)
    {
        Console.WriteLine($"  [{(condition ? "ok" : "FAIL")}] {what}");
        return condition ? 0 : 1;
    }

    private static void Write(string path, int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Raw MFT reads see the on-disk state: flush NTFS metadata first.</summary>
    private static void FlushVolume(string device)
    {
        using SafeFileHandle volume = CreateFile(device, 0xC000_0000, 3, 0, 3, 0, 0);
        if (!volume.IsInvalid)
            FlushFileBuffers(volume);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle handle);
}
