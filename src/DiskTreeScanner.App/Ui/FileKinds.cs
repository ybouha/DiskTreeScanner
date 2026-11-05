using DiskTreeScanner.Core.Model;

namespace DiskTreeScanner.App.Ui;

internal enum FileKind
{
    File,
    WslDisk,
    VirtualDisk,
    Hibernation,
    PageFile,
    SwapFile,
    NtfsMetadata,
    System,
    Installer,
    Dump,
    Archive,
    Video,
    Database,
    Log,
}

/// <summary>Category pill of the Largest files panel: amber = WSL disks, grey = system, green = removable.</summary>
internal static class FileKinds
{
    private static readonly HashSet<string> Installers = new(StringComparer.OrdinalIgnoreCase) { ".iso", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".msp", ".cab", ".img" };
    private static readonly HashSet<string> Dumps = new(StringComparer.OrdinalIgnoreCase) { ".dmp", ".hdmp", ".mdmp" };
    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".nupkg" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm" };
    private static readonly HashSet<string> Databases = new(StringComparer.OrdinalIgnoreCase) { ".mdf", ".ldf", ".ndf", ".bak", ".db", ".sqlite", ".edb", ".accdb" };
    private static readonly HashSet<string> Disks = new(StringComparer.OrdinalIgnoreCase) { ".vhd", ".vmdk", ".qcow2", ".vdi", ".avhdx" };

    public static FileKind Classify(TreeModel model, int node, string name, string folder)
    {
        if (model.HasFlag(node, NodeFlags.NtfsSpecial))
            return FileKind.NtfsMetadata;
        string extension = Path.GetExtension(name);
        if (string.Equals(extension, ".vhdx", StringComparison.OrdinalIgnoreCase))
        {
            bool wsl = name.Equals("ext4.vhdx", StringComparison.OrdinalIgnoreCase)
                || folder.Contains(@"\wsl", StringComparison.OrdinalIgnoreCase)
                || folder.Contains("CanonicalGroupLimited", StringComparison.OrdinalIgnoreCase)
                || folder.Contains("Docker", StringComparison.OrdinalIgnoreCase);
            return wsl ? FileKind.WslDisk : FileKind.VirtualDisk;
        }
        if (Disks.Contains(extension))
            return FileKind.VirtualDisk;
        if (name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase))
            return FileKind.Hibernation;
        if (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase))
            return FileKind.PageFile;
        if (name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
            return FileKind.SwapFile;
        if (Dumps.Contains(extension))
            return FileKind.Dump;
        if (Installers.Contains(extension) || (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && folder.Contains("Downloads", StringComparison.OrdinalIgnoreCase)))
            return FileKind.Installer;
        if (model.HasFlag(node, NodeFlags.System))
            return FileKind.System;
        if (Archives.Contains(extension))
            return FileKind.Archive;
        if (Videos.Contains(extension))
            return FileKind.Video;
        if (Databases.Contains(extension))
            return FileKind.Database;
        if (extension.Equals(".log", StringComparison.OrdinalIgnoreCase) || extension.Equals(".etl", StringComparison.OrdinalIgnoreCase))
            return FileKind.Log;
        return FileKind.File;
    }

    public static Color Dot(FileKind kind) => kind switch
    {
        FileKind.WslDisk or FileKind.VirtualDisk => Theme.Amber,
        FileKind.Hibernation or FileKind.PageFile or FileKind.SwapFile or FileKind.NtfsMetadata or FileKind.System => Theme.TextMuted,
        FileKind.Installer or FileKind.Dump or FileKind.Log => Theme.Green,
        _ => Theme.Bar,
    };

    public static string Label(FileKind kind) => L.S(kind switch
    {
        FileKind.WslDisk => "KindWslDisk",
        FileKind.VirtualDisk => "KindVirtualDisk",
        FileKind.Hibernation => "KindHibernation",
        FileKind.PageFile => "KindPageFile",
        FileKind.SwapFile => "KindSwapFile",
        FileKind.NtfsMetadata => "KindNtfsMetadata",
        FileKind.System => "KindSystem",
        FileKind.Installer => "KindInstaller",
        FileKind.Dump => "KindDump",
        FileKind.Archive => "KindArchive",
        FileKind.Video => "KindVideo",
        FileKind.Database => "KindDatabase",
        FileKind.Log => "KindLog",
        _ => "KindFile",
    });
}
