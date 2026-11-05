using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskTreeScanner.App.Ui;

/// <summary>Shell actions of the context menu (no delete in v1).</summary>
internal static partial class Shell
{
    private const uint SEE_MASK_INVOKEIDLIST = 0x0000_000C;
    private const int SW_SHOW = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct SHELLEXECUTEINFOW
    {
        public int cbSize;
        public uint fMask;
        public nint hwnd;
        public nint lpVerb;
        public nint lpFile;
        public nint lpParameters;
        public nint lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        public nint lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIconOrMonitor;
        public nint hProcess;
    }

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellExecuteEx(ref SHELLEXECUTEINFOW info);

    public static void Open(string path) => Run(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));

    /// <summary>Opens a folder, or the parent folder with the file selected.</summary>
    public static void OpenInExplorer(string path, bool isDirectory) => Run(() =>
    {
        string arguments = isDirectory ? $"\"{path}\"" : $"/select,\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    });

    public static void CopyPath(string path)
    {
        try
        {
            Clipboard.SetText(path);
        }
        catch (ExternalException)
        {
        }
    }

    public static void Properties(IWin32Window owner, string path)
    {
        nint verb = Marshal.StringToHGlobalUni("properties");
        nint file = Marshal.StringToHGlobalUni(path);
        try
        {
            var info = new SHELLEXECUTEINFOW
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFOW>(),
                fMask = SEE_MASK_INVOKEIDLIST,
                hwnd = owner.Handle,
                lpVerb = verb,
                lpFile = file,
                nShow = SW_SHOW,
            };
            ShellExecuteEx(ref info);
        }
        finally
        {
            Marshal.FreeHGlobal(verb);
            Marshal.FreeHGlobal(file);
        }
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
        }
    }
}
