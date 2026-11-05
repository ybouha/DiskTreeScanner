using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace DiskTreeScanner.Core.Platform;

public sealed record WslDistribution(string Name, string? BasePath, bool IsRunning)
{
    /// <summary>UNC root used to browse the distribution from Windows.</summary>
    public string UncRoot => @"\\wsl$\" + Name;
}

/// <summary>Installed WSL distributions (registry) and their running state (wsl.exe).</summary>
public static class WslDistributions
{
    public static IReadOnlyList<WslDistribution> List(bool queryRunning, TimeSpan timeout)
    {
        var running = queryRunning ? ListRunning(timeout) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<WslDistribution>();
        using RegistryKey? lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        if (lxss is null)
            return result;
        foreach (string id in lxss.GetSubKeyNames())
        {
            using RegistryKey? key = lxss.OpenSubKey(id);
            if (key?.GetValue("DistributionName") is not string name || name.Length == 0)
                continue;
            result.Add(new WslDistribution(name, key.GetValue("BasePath") as string, running.Contains(name)));
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static HashSet<string> ListRunning(TimeSpan timeout)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        if (!File.Exists(wsl))
            return names;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(wsl, "--list --running --quiet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.Unicode, // wsl.exe writes UTF-16
            });
            if (process is null)
                return names;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return names;
            }
            foreach (string line in output.Result.Split('\n'))
            {
                string name = line.Trim().Trim('\0');
                if (name.Length > 0)
                    names.Add(name);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }
        return names;
    }

    /// <summary>True for <c>\\wsl$\name\…</c> and <c>\\wsl.localhost\name\…</c>.</summary>
    public static bool IsWslPath(string path, out string distribution, out string uncRoot)
    {
        distribution = string.Empty;
        uncRoot = string.Empty;
        foreach (string prefix in (ReadOnlySpan<string>)[@"\\wsl$\", @"\\wsl.localhost\"])
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            string rest = path[prefix.Length..];
            int slash = rest.IndexOf('\\');
            distribution = slash < 0 ? rest : rest[..slash];
            uncRoot = prefix + distribution;
            return distribution.Length > 0;
        }
        return false;
    }
}
