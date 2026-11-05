using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DiskTreeScanner.Core.Interop;

namespace DiskTreeScanner.Core.Platform;

/// <summary>Process elevation and privileges.</summary>
public static class Elevation
{
    private static readonly Lazy<bool> s_isElevated = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    /// <summary>True when the process runs with an elevated (administrator) token.</summary>
    public static bool IsElevated => s_isElevated.Value;

    /// <summary>Enables a privilege held by the token (e.g. SeBackupPrivilege); false when not held.</summary>
    public static unsafe bool TryEnablePrivilege(string name)
    {
        if (!Win32.OpenProcessToken(Win32.GetCurrentProcess(), Win32.TOKEN_ADJUST_PRIVILEGES | Win32.TOKEN_QUERY, out nint token))
            return false;
        try
        {
            if (!Win32.LookupPrivilegeValue(null, name, out Win32.LUID luid))
                return false;
            var privileges = new Win32.TOKEN_PRIVILEGES_1 { PrivilegeCount = 1, Luid = luid, Attributes = Win32.SE_PRIVILEGE_ENABLED };
            if (!Win32.AdjustTokenPrivileges(token, false, &privileges, 0, 0, 0))
                return false;
            return Marshal.GetLastWin32Error() == 0; // ERROR_NOT_ALL_ASSIGNED (1300) when the token lacks it
        }
        finally
        {
            Win32.CloseHandle(token);
        }
    }

    /// <summary>Restarts the current executable elevated (UAC prompt). Returns false if the user declined.</summary>
    public static bool RestartElevated(string? arguments = null)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
            return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = Environment.CurrentDirectory,
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // ERROR_CANCELLED: the user refused the UAC prompt
        }
    }
}
