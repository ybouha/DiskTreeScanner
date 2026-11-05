using System.Diagnostics;
using DiskTreeScanner.Core.Formatting;
using DiskTreeScanner.Core.Model;
using DiskTreeScanner.Core.Platform;
using DiskTreeScanner.Core.Scanning;
using DiskTreeScanner.Ntfs.Usn;

namespace DiskTreeScanner.Ntfs.Mft;

/// <summary>
/// Engine 1: reads the NTFS master file table directly from the raw volume (local NTFS, elevated).
/// The whole MFT is read even for a subfolder; the folder is then a DFS range of the volume tree.
/// </summary>
public sealed class MftScanEngine : IScanEngine
{
    public const int RootRecord = 5;
    public const int ExtendRecord = 11;
    public const int FirstUserRecord = 16;

    public string Name => "MFT";

    public EngineProbe Probe(ScanTargetInfo target, ScanOptions options)
    {
        if (options.ForceDirectoryScan)
            return new EngineProbe(false, "\"Force directory scan\" is enabled");
        if (!target.IsLocal || target.VolumeDevicePath is null)
            return new EngineProbe(false, $"{target.Kind} path, not a local volume");
        if (!target.IsNtfs)
            return new EngineProbe(false, $"{(target.FileSystem.Length > 0 ? target.FileSystem : "unknown")} file system (an MFT exists only on NTFS)");
        if (!target.IsElevated)
            return new EngineProbe(false, "not elevated (raw volume reads need administrator rights)");
        return new EngineProbe(true, $"local NTFS volume {target.VolumeRoot}, elevated");
    }

    public ScanResult Scan(ScanTargetInfo target, ScanOptions options, ScanProgress progress, ActivityLog log, CancellationToken cancellationToken)
    {
        var timings = new PhaseTimings();
        var stats = new ScanStatistics();
        long t = Stopwatch.GetTimestamp();

        progress.Phase = "Opening volume";
        log.Info($"Opening raw volume {target.VolumeDevicePath} read-only to read the MFT directly. " +
                 "Endpoint security software (EDR, Defender) may record this raw disk access; " +
                 "Options > Force directory scan avoids it.");
        using NtfsVolume volume = NtfsVolume.Open(target.VolumeDevicePath!);
        UsnCheckpoint? usn = UsnJournal.Query(volume.Handle); // before reading: later changes are replayed by Refresh
        MftLayout layout = MftLayout.Load(volume);
        UpCaseTable upCase = layout.ReadUpCase(volume) ?? UpCaseTable.Invariant;
        stats.MftRecords = layout.RecordCount;
        stats.MftExtents = layout.Extents.Length;
        t = timings.Mark("open", t, $"{layout.Extents.Length} MFT extent(s), {layout.RecordCount:N0} records, {layout.InUseCount:N0} in use (bitmap), case table {upCase.Source}");
        log.Info($"MFT: {Format.Bytes(layout.MftBytes)} in {layout.Extents.Length} extent(s), {layout.RecordCount:N0} records of {layout.RecordSize} bytes, {layout.InUseCount:N0} in use.");

        // Read + parse (pipelined).
        progress.Phase = "Reading MFT";
        var names = new NameArena();
        using var raw = new MftRawTable(layout.InUseCount);
        PipelineResult pipeline = MftPipeline.Run(volume, layout, raw, names, options, progress, cancellationToken);
        double cpu = pipeline.Workers.Sum(w => w.ParseTicks) * 1000.0 / Stopwatch.Frequency;
        stats.MftBytesRead = pipeline.BytesRead;
        stats.MftBytesSkipped = pipeline.BytesSkipped;
        stats.ReaderStallMilliseconds = pipeline.StallTime.TotalMilliseconds;
        stats.ParseCpuMilliseconds = cpu;
        stats.ParserThreads = pipeline.Workers.Length;
        double throughput = pipeline.ReadTime.TotalSeconds > 0 ? pipeline.BytesRead / pipeline.ReadTime.TotalSeconds : 0;
        timings.Add("MFT read", pipeline.ReadTime,
            $"{Format.Bytes(pipeline.BytesRead)} in {pipeline.Blocks} blocks at {Format.Bytes((long)throughput)}/s, queue depth {pipeline.QueueDepth}, " +
            $"{Format.Bytes(pipeline.BytesSkipped)} of free records skipped, reader stalled {pipeline.StallTime.TotalMilliseconds:F1} ms; {ProcessMemory.Describe()}");
        TimeSpan tail = pipeline.WallTime - pipeline.ReadTime - pipeline.StallTime;
        timings.Add("parsing", tail > TimeSpan.Zero ? tail : TimeSpan.Zero,
            $"overlapped with the read; {cpu:F0} ms CPU on {pipeline.Workers.Length} threads (shown: what remained after the last read)");
        t = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();

        // Tree.
        progress.Phase = "Building tree";
        TreeModel model = MftModelBuilder.Build(layout, raw, pipeline.Workers, names, options, target.VolumeRoot!, upCase, stats, log, out string breakdown);
        t = timings.Mark("tree", t, breakdown);

        progress.Phase = "Aggregating";
        Aggregator.Aggregate(model);
        timings.Mark("aggregation", t, ProcessMemory.Describe());
        MftModelBuilder.CountSpecial(model, stats);

        int viewRoot = 0;
        if (!target.IsVolumeRoot && target.RelativePath.Length > 0)
        {
            viewRoot = model.FindRelative(target.RelativePath);
            if (viewRoot < 0)
                throw new DirectoryNotFoundException($"{target.Path} was not found in the MFT of {target.VolumeRoot}.");
        }

        (long total, long free) = Volumes.GetSpace(target.VolumeRoot!);
        var result = new ScanResult
        {
            Model = model,
            ViewRoot = viewRoot,
            Engine = Name,
            Target = target,
            Options = options,
            VolumeTotalBytes = total,
            VolumeFreeBytes = free,
            Media = Volumes.GetMediaKind(target.VolumeDevicePath!),
            Statistics = stats,
            Timings = timings,
            Usn = usn,
        };
        if (stats.BadRecords > 0)
            result.Warnings.Add($"{stats.BadRecords:N0} unreadable MFT record(s) ignored.");
        log.Info($"MFT scan: {model.ItemCount(0):N0} items, {stats.ExtensionRecords:N0} extension records, {stats.HardLinkExtraNames:N0} extra hard-link names, " +
                 $"{stats.AlternateStreams:N0} named streams ({Format.Bytes(stats.AlternateStreamSize)}), {stats.BadRecords:N0} bad records.");
        return result;
    }

}
