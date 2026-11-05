using System.Diagnostics;
using System.Globalization;
using System.Text;
using DiskTreeScanner.Cli;
using DiskTreeScanner.Core.Analysis;
using DiskTreeScanner.Core.Export;
using DiskTreeScanner.Core.Fallback;
using DiskTreeScanner.Core.Formatting;
using DiskTreeScanner.Core.Model;
using DiskTreeScanner.Core.Scanning;
using DiskTreeScanner.Ntfs.Mft;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;

if (args is ["--usn-test", .. var usnTestFolder] && usnTestFolder.Length <= 1)
    return UsnTest.Run(usnTestFolder.Length == 1 ? usnTestFolder[0] : Path.GetTempPath());

CliOptions cli;
try
{
    cli = CliOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(CliOptions.Usage);
    return 2;
}
if (cli.Help || cli.Path is null)
{
    Console.WriteLine(CliOptions.Usage);
    return cli.Help ? 0 : 2;
}

IScanEngine[] engines = cli.Engine switch
{
    "mft" => [new MftScanEngine()],
    "dir" => [new DirectoryScanEngine()],
    _ => [new MftScanEngine(), new DirectoryScanEngine()],
};
var coordinator = new ScanCoordinator(engines);
var log = new ActivityLog();
if (cli.Verbose)
    log.EntryAdded += e => Console.WriteLine("  [log] " + e);

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

Console.WriteLine($"DiskTreeScanner CLI {typeof(ScanCoordinator).Assembly.GetName().Version?.ToString(3)} - scan of {cli.Path}");
long baselineWorkingSet = Process.GetCurrentProcess().WorkingSet64;
ScanResult? result = null;
var durations = new List<TimeSpan>();
try
{
    for (int run = 1; run <= cli.Repeat; run++)
    {
        result = null;
        // Give the previous run's heap back to the OS: the peak working set is a process-lifetime value.
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var progress = new ScanProgress();
        result = coordinator.Scan(cli.Path, cli.ScanOptions, progress, log, cancel.Token);
        durations.Add(result.Duration);
        if (cli.Repeat > 1)
            Console.WriteLine($"  run {run}: {Format.Milliseconds(result.Duration)} ({result.Engine})");
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Scan failed: {ex.Message}");
    return 1;
}

ScanResult r = result!;
TreeModel model = r.Model;
int root = r.ViewRoot;

// "First render": what the window computes before its first paint.
long renderStart = Stopwatch.GetTimestamp();
int[] topLevel = ChildOrder.Sorted(model, root, SortColumn.Allocated, descending: true);
int[] largest = LargestFiles.Find(model, root, cli.Top);
var rendered = new List<string>(topLevel.Length);
foreach (int c in topLevel)
    rendered.Add(model.GetName(c));
TimeSpan firstRender = Stopwatch.GetElapsedTime(renderStart);

var process = Process.GetCurrentProcess();
process.Refresh();
Console.WriteLine($"Engine    : {r.Engine}   target {r.Target.Path} ({r.Target.Kind}, {r.Target.FileSystem}, {(r.Target.IsElevated ? "elevated" : "not elevated")})");
Console.WriteLine($"Volume    : {Format.Bytes(r.VolumeTotalBytes)} total, {Format.Bytes(r.VolumeFreeBytes)} free, {r.Media}");
foreach (string warning in r.Warnings)
    Console.WriteLine($"Warning   : {warning}");
Console.WriteLine();
Console.WriteLine("Phases");
foreach (PhaseTiming phase in r.Timings.Phases)
    Console.WriteLine($"  {phase.Name,-14}{Format.Milliseconds(phase.Duration),12}   {phase.Detail}");
Console.WriteLine($"  {"first render",-14}{Format.Milliseconds(firstRender),12}   root children sorted by allocated, top {cli.Top} files of {model.FileCount(root):N0}");
Console.WriteLine($"  {"total",-14}{Format.Milliseconds(r.Duration + firstRender),12}   end to end (scan {Format.Milliseconds(r.Duration)})");
if (durations.Count > 1)
    Console.WriteLine($"  runs: best {Format.Milliseconds(durations.Min())}, mean {Format.Milliseconds(TimeSpan.FromTicks((long)durations.Average(d => d.Ticks)))}, worst {Format.Milliseconds(durations.Max())}");
Console.WriteLine();

Console.WriteLine($"Totals for {model.GetPath(root)}");
Console.WriteLine($"  Size       {model.Size[root],20:N0}  {Format.Bytes(model.Size[root])}");
Console.WriteLine($"  Allocated  {model.Allocated[root],20:N0}  {Format.Bytes(model.Allocated[root])}");
Console.WriteLine($"  Items      {model.ItemCount(root),20:N0}  files {model.FileCount(root):N0}, folders {model.FolderCount(root):N0}");
ScanStatistics s = r.Statistics;
if (r.Engine == "MFT")
{
    Console.WriteLine($"  MFT        {s.MftRecords:N0} records, {s.MftRecordsInUse:N0} in use, {s.ExtensionRecords:N0} extension, {s.BadRecords:N0} bad, {s.StaleExtensions:N0} stale references; {s.MftExtents} extent(s)");
}
else
{
    Console.WriteLine($"  Listing    {s.DirectoriesListed:N0} folders listed, {s.AccessDenied:N0} not accessible");
}
Console.WriteLine($"  Hard links {s.HardLinkExtraNames:N0} extra names ({cli.ScanOptions.HardLinks}); their files hold {Format.Bytes(s.HardLinkDuplicateSize)} size / {Format.Bytes(s.HardLinkDuplicateAllocated)} allocated");
Console.WriteLine($"  Streams    {s.AlternateStreams:N0} named streams, {Format.Bytes(s.AlternateStreamSize)} / {Format.Bytes(s.AlternateStreamAllocated)} allocated ({(cli.ScanOptions.IncludeAlternateStreams ? "included" : "not included")}); {s.WofCompressedFiles:N0} WOF-compressed files");
Console.WriteLine($"  Special    NTFS metadata files {Format.Bytes(s.SpecialFilesSize)} / {Format.Bytes(s.SpecialFilesAllocated)} allocated ({(cli.ScanOptions.IncludeNtfsSpecialFiles ? "included" : "excluded")})");
if (s.OrphanRoots > 0)
    Console.WriteLine($"  Orphans    {s.OrphanRoots:N0} roots, {s.OrphanNodes:N0} nodes under {TreeModel.OrphansName}");
Console.WriteLine($"  Memory     peak working set {Format.Bytes(process.PeakWorkingSet64)} (process baseline before the scan {Format.Bytes(baselineWorkingSet)}), peak private {Format.Bytes(process.PeakPagedMemorySize64)}, managed heap {Format.Bytes(GC.GetTotalMemory(false))}, names {Format.Bytes(s.NameArenaBytes)}");
Console.WriteLine();

Console.WriteLine($"{"Folder",-48}{"Size",16}{"Allocated",16}{"Items",12}{"Files",12}{"Folders",10}");
PrintTree(model, root, 0, cli.Depth, topLevel);
Console.WriteLine();
Console.WriteLine($"Largest files ({largest.Length})");
foreach (int f in largest)
    Console.WriteLine($"  {Format.Bytes(model.Size[f]),12}  {model.GetPath(f)}");

if (cli.CsvPath is not null)
{
    var exportClock = Stopwatch.StartNew();
    long rows = CsvExporter.Export(model, root, cli.CsvPath, CancellationToken.None);
    Console.WriteLine();
    Console.WriteLine($"CSV export: {rows:N0} rows to {cli.CsvPath} in {Format.Milliseconds(exportClock.Elapsed)}");
}

return 0;

static void PrintTree(TreeModel model, int node, int depth, int maxDepth, int[]? children = null)
{
    if (depth == 0)
        PrintRow(model, node, 0);
    if (depth >= maxDepth)
        return;
    children ??= ChildOrder.Sorted(model, node, SortColumn.Allocated, descending: true);
    int files = 0;
    long fileSize = 0, fileAllocated = 0;
    foreach (int c in children)
    {
        if (!model.IsDirectory(c))
        {
            files++;
            fileSize += model.Size[c];
            fileAllocated += model.Allocated[c];
            continue;
        }
        PrintRow(model, c, depth + 1);
        PrintTree(model, c, depth + 1, maxDepth);
    }
    if (files > 0)
    {
        string label = new string(' ', 2 * (depth + 1)) + $"[{files:N0} files]";
        Console.WriteLine($"{Truncate(label, 47),-48}{Format.Bytes(fileSize),16}{Format.Bytes(fileAllocated),16}{files,12:N0}{files,12:N0}{0,10:N0}");
    }
}

static void PrintRow(TreeModel model, int node, int depth)
{
    string label = new string(' ', 2 * depth) + (node == 0 ? model.RootPath : model.GetName(node));
    Console.WriteLine($"{Truncate(label, 47),-48}{Format.Bytes(model.Size[node]),16}{Format.Bytes(model.Allocated[node]),16}{model.ItemCount(node),12:N0}{model.FileCount(node),12:N0}{model.FolderCount(node),10:N0}");
}

static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

internal sealed class CliOptions
{
    public const string Usage = """
        Usage: DiskTreeScanner.Cli <path> [options]
               DiskTreeScanner.Cli --usn-test [FOLDER]
                                     check the USN journal refresh (admin) in a scratch folder
                                     created under FOLDER (default: %TEMP%)

          --engine auto|mft|dir      engine (default auto: MFT when elevated on NTFS, else directory)
          --no-special               exclude NTFS special files ($MFT, $LogFile, $Extend...)
          --ads                      add named data streams (ADS) to file sizes
          --hardlinks once|each|mixed
                                     once: count a hard-linked file once (default)
                                     each: count it on every link
                                     mixed: size on every link, allocation once
          --force-dir                same as --engine dir (corporate machines with EDR)
          --top N                    largest files to list (default 10)
          --depth N                  folder levels to print (default 1)
          --csv FILE                 export the scanned tree to CSV
          --repeat N                 scan N times and report best / mean / worst
          --threads N                MFT parser threads (default cores - 1)
          --block-mb N               MFT read block size, 4 to 16 MiB (default 4)
          --buffers N                MFT read buffers (default queue depth + 2)
          --queue N                  overlapped MFT reads in flight (default 2)
          --dir-threads N            directory engine threads (default automatic)
          --verbose                  print the activity log
        """;

    public string? Path { get; private set; }
    public string Engine { get; private set; } = "auto";
    public ScanOptions ScanOptions { get; private set; } = new();
    public int Top { get; private set; } = 10;
    public int Depth { get; private set; } = 1;
    public string? CsvPath { get; private set; }
    public int Repeat { get; private set; } = 1;
    public bool Verbose { get; private set; }
    public bool Help { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value.");
            int NextInt() => int.TryParse(Next(), out int v) && v >= 0 ? v : throw new ArgumentException($"{a} needs a number.");
            switch (a)
            {
                case "-h" or "--help" or "/?":
                    o.Help = true;
                    break;
                case "--engine":
                    o.Engine = Next().ToLowerInvariant() is var e and ("auto" or "mft" or "dir") ? e : throw new ArgumentException("--engine must be auto, mft or dir.");
                    break;
                case "--force-dir":
                    o.Engine = "dir";
                    o.ScanOptions = o.ScanOptions with { ForceDirectoryScan = true };
                    break;
                case "--no-special":
                    o.ScanOptions = o.ScanOptions with { IncludeNtfsSpecialFiles = false };
                    break;
                case "--ads":
                    o.ScanOptions = o.ScanOptions with { IncludeAlternateStreams = true };
                    break;
                case "--hardlinks":
                    o.ScanOptions = o.ScanOptions with
                    {
                        HardLinks = Next().ToLowerInvariant() switch
                        {
                            "once" => HardLinkMode.FirstLinkOnly,
                            "each" => HardLinkMode.EveryLink,
                            "mixed" => HardLinkMode.SizeEveryLinkAllocatedOnce,
                            _ => throw new ArgumentException("--hardlinks must be once, each or mixed."),
                        },
                    };
                    break;
                case "--top":
                    o.Top = NextInt();
                    break;
                case "--depth":
                    o.Depth = NextInt();
                    break;
                case "--csv":
                    o.CsvPath = Next();
                    break;
                case "--repeat":
                    o.Repeat = Math.Max(1, NextInt());
                    break;
                case "--threads":
                    o.ScanOptions = o.ScanOptions with { MftParserThreads = NextInt() };
                    break;
                case "--block-mb":
                    o.ScanOptions = o.ScanOptions with { MftBlockMegabytes = NextInt() };
                    break;
                case "--buffers":
                    o.ScanOptions = o.ScanOptions with { MftBufferCount = NextInt() };
                    break;
                case "--queue":
                    o.ScanOptions = o.ScanOptions with { MftQueueDepth = NextInt() };
                    break;
                case "--dir-threads":
                    o.ScanOptions = o.ScanOptions with { DirectoryParallelism = NextInt() };
                    break;
                case "--verbose":
                    o.Verbose = true;
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"Unknown option {a}.");
                    if (o.Path is not null)
                        throw new ArgumentException("Only one path can be scanned.");
                    o.Path = a;
                    break;
            }
        }
        return o;
    }
}
