using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;

namespace DiskTreeScanner.Bench;

/// <summary>
/// <c>dotnet run -c Release --project bench\DiskTreeScanner.Bench -- --filter *</c>
/// (<c>--filter *Parsing*</c> or <c>*Tree*</c> for one class, <c>--job short</c> for a quick run).
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        IConfig config = DefaultConfig.Instance.AddDiagnoser(MemoryDiagnoser.Default);
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}
