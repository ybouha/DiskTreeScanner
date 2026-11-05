using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DiskTreeScanner.Core.Fallback;
using DiskTreeScanner.Core.Model;
using DiskTreeScanner.Core.Platform;
using DiskTreeScanner.Core.Scanning;
using DiskTreeScanner.Ntfs.Mft;
using Xunit;

namespace DiskTreeScanner.Tests;

/// <summary>Runs only in an elevated test run (raw volume access).</summary>
public sealed class AdminFactAttribute : FactAttribute
{
    public AdminFactAttribute()
    {
        if (!Elevation.IsElevated)
            Skip = "Needs an elevated (administrator) test run to read the MFT.";
    }
}

/// <summary>A real folder on the system NTFS volume with known content.</summary>
public sealed class SampleFolder : IDisposable
{
    public const long ExpectedSize = 0 + 1 + 700 + 5000 + 3_000_000 + 100_000 + 12_345 + 42;
    public const int ExpectedFiles = 9;   // 8 files + 1 extra hard-link name
    public const int ExpectedFolders = 4; // sub, deeper, deepest, empty folder

    public SampleFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "DiskTreeScanner.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Write("empty.txt", 0);
        Write("one.txt", 1);
        Write("small.txt", 700);
        Write("medium.bin", 5000);
        Write("big.bin", 3_000_000);
        Write(@"sub\a.bin", 100_000);
        Write(@"sub\deeper\b.bin", 12_345);
        Write(@"sub\deeper\deepest\c.txt", 42);
        Directory.CreateDirectory(Path.Combine(Root, "empty folder"));
        if (!CreateHardLink(Path.Combine(Root, @"sub\link to big.bin"), Path.Combine(Root, "big.bin"), 0))
            throw new InvalidOperationException($"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
        try
        {
            File.WriteAllBytes(Path.Combine(Root, "medium.bin:extra stream"), new byte[20_000]); // ADS: counted apart
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
        }
    }

    public string Root { get; }

    private void Write(string relative, int size)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Raw MFT reads see the on-disk state: flush NTFS metadata first (needs admin).</summary>
    public static void FlushVolume(string volumeDevice)
    {
        using SafeFileHandle volume = CreateFile(volumeDevice, 0xC000_0000, 3, 0, 3, 0, 0);
        if (!volume.IsInvalid)
            FlushFileBuffers(volume);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);
}

public class EngineConsistencyTests : IClassFixture<SampleFolderFixture>
{
    private readonly SampleFolder _folder;

    public EngineConsistencyTests(SampleFolderFixture fixture) => _folder = fixture.Folder;

    [Fact]
    public void DirectoryEngine_GivesTheExpectedTotals()
    {
        ScanTargetInfo target = ScanTargetInfo.Analyze(_folder.Root);
        ScanResult r = new DirectoryScanEngine().Scan(target, new ScanOptions(), new ScanProgress(), new ActivityLog(), CancellationToken.None);
        TreeModel m = r.Model;
        Assert.Equal(SampleFolder.ExpectedSize, m.Size[0]);
        Assert.Equal(SampleFolder.ExpectedFiles, m.FileCount(0));
        Assert.Equal(SampleFolder.ExpectedFolders, m.FolderCount(0));
        Assert.Equal(1, r.Statistics.HardLinkExtraNames);
    }

    [AdminFact]
    public void MftAndDirectoryEngines_GiveTheSameTotals_OnTheSameFolder()
    {
        ScanTargetInfo target = ScanTargetInfo.Analyze(_folder.Root);
        Assert.True(target.IsNtfs);
        SampleFolder.FlushVolume(target.VolumeDevicePath!);
        var options = new ScanOptions();
        ScanResult mft = new MftScanEngine().Scan(target, options, new ScanProgress(), new ActivityLog(), CancellationToken.None);
        ScanResult dir = new DirectoryScanEngine().Scan(target, options, new ScanProgress(), new ActivityLog(), CancellationToken.None);

        int a = mft.ViewRoot;
        Assert.True(a > 0);
        Compare(mft.Model, a, dir.Model, 0, "", sizes: true);
        Assert.Equal(SampleFolder.ExpectedSize, mft.Model.Size[a]);
        foreach (int child in dir.Model.Children(0))
        {
            string name = dir.Model.GetName(child);
            int other = mft.Model.FindChild(a, name);
            Assert.True(other > 0, $"{name} missing from the MFT scan");
            // A hard-linked file is counted once, on its "first" link: the first $FILE_NAME of the
            // record for the MFT engine, the first name listed for the directory engine. Both links
            // are in the folder, so its totals match; which child carries the bytes may differ.
            bool holdsALink = name is "big.bin" or "sub";
            Compare(mft.Model, other, dir.Model, child, name, sizes: !holdsALink);
        }
    }

    private static void Compare(TreeModel mft, int a, TreeModel dir, int b, string what, bool sizes)
    {
        if (sizes)
        {
            Assert.True(dir.Size[b] == mft.Size[a], $"Size of '{what}': directory {dir.Size[b]}, MFT {mft.Size[a]}");
            Assert.True(dir.Allocated[b] == mft.Allocated[a], $"Allocated of '{what}': directory {dir.Allocated[b]}, MFT {mft.Allocated[a]}");
        }
        Assert.Equal(dir.ItemCount(b), mft.ItemCount(a));
        Assert.Equal(dir.FileCount(b), mft.FileCount(a));
        Assert.Equal(dir.FolderCount(b), mft.FolderCount(a));
    }
}

public sealed class SampleFolderFixture : IDisposable
{
    public SampleFolder Folder { get; } = new();

    public void Dispose() => Folder.Dispose();
}
