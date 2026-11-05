# DiskTreeScanner

Fast disk-space analyzer for Windows. On a local NTFS volume with admin rights it reads the
Master File Table directly instead of walking directories, which scans a ~2 M-item system drive
in about **2.3 s** with a peak memory under **200 MB**. Everywhere else (no elevation, ReFS,
exFAT, network shares, WSL) it falls back to a parallel directory enumerator that feeds the same
model, so the UI behaves identically.

C# 12 · .NET 8 · WinForms · x64 · no third-party runtime dependencies.

---

## Quick start

### Prerequisites

| Requirement | Why |
|---|---|
| Windows 10/11 x64 | P/Invoke into NTFS/Win32 APIs, WinForms. |
| [.NET SDK 8.0 or later](https://dotnet.microsoft.com/download) | Build. Any SDK ≥ 8 works; CI-equivalent builds were done with 10.0.403. |
| [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) | Run. A newer SDK does **not** ship the 8.0 runtime; install it separately if `dotnet --list-runtimes` shows no `Microsoft.WindowsDesktop.App 8.0.x`. |
| Git | Get the source. |

No IDE is required. Visual Studio 2022 17.8+ or Rider open `DiskTreeScanner.sln` directly.

### Get the source

```powershell
git clone https://github.com/ybouha/DiskTreeScanner.git
cd DiskTreeScanner
```

(or `gh repo clone ybouha/DiskTreeScanner`)

### Build, test, run

```powershell
dotnet build DiskTreeScanner.sln -c Release
dotnet test  tests\DiskTreeScanner.Tests -c Release
dotnet run   -c Release --project src\DiskTreeScanner.App
```

Expected: build with 0 warnings / 0 errors, 68 tests passing. One test compares both engines
against the live MFT and is skipped unless the console is elevated.

To exercise the fast path, start the app **as administrator** (or click *Restart as
administrator* in the banner shown when it is not elevated). Without elevation it still works,
using the directory engine.

### Publish

```powershell
dotnet publish src\DiskTreeScanner.App  -c Release -o publish\app   # single DiskTreeScanner.exe, ~1.3 MB, ReadyToRun
dotnet publish tools\DiskTreeScanner.Cli -c Release -o publish\cli
```

The app is framework-dependent (needs the .NET 8 Desktop Runtime). For a self-contained ~63 MB
exe add `-p:SelfContained=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=false`.
Trimming is not supported by WinForms (NETSDK1175).

---

## What to validate

A short checklist for a first review:

1. **Build & tests** — commands above, clean output.
2. **Non-elevated run** — app opens, banner offers elevation, scan of a user folder completes
   (directory engine; the activity log says `MFT engine skipped: not elevated`).
3. **Elevated run on `C:\`** — scan completes in a few seconds; totals look sane next to
   Explorer's *Properties* of the drive.
4. **UI** — expand/collapse, sort by column, column filters (funnel icons), search with `Ctrl+F`
   (`*.iso; *.vhdx`), context menu, *Largest files* and treemap panels, EN/FR switch.
5. **Refresh** (`Ctrl+R`) after creating/deleting files — incremental via the USN journal on NTFS.
6. **CLI** — `publish\cli\DiskTreeScanner.Cli.exe C:\ --repeat 3` prints per-phase timings and
   peak memory.

Feedback via GitHub issues or PR comments is welcome.

---

## Repository layout

| Path | Responsibility |
|---|---|
| `src/DiskTreeScanner.Core` | Data model (struct-of-arrays tree), tree builder, aggregation, directory fallback engine, analyses (largest files, search, treemap, CSV export). No UI dependency. |
| `src/DiskTreeScanner.Ntfs` | Raw MFT reader (`LibraryImport`, `unsafe`), FILE-record parser, USN-journal incremental refresh. |
| `src/DiskTreeScanner.App` | WinForms shell → `DiskTreeScanner.exe`. Owner-drawn virtual tree grid, treemap, custom dark chrome. |
| `tools/DiskTreeScanner.Cli` | Headless scanner: per-phase timings, totals, memory, USN self-test. |
| `tests/DiskTreeScanner.Tests` | xUnit: synthetic MFT records, parser robustness, filters, engine consistency on a real folder. |
| `bench/DiskTreeScanner.Bench` | BenchmarkDotNet: record parsing, tree build, aggregation. |
| `mockup/` | HTML mockup the UI was built against. |
| `docs/` | Design notes, detailed measurements, counting rules (French). |

Shared build settings (language version, nullable, x64, embedded symbols, version) live in
`Directory.Build.props`.

---

## Architecture in one page

```
            ┌──────────── ScanCoordinator ────────────┐
            │ picks engine: NTFS + elevated + !forced  │
            ▼                                          ▼
   MftScanEngine (Ntfs)                     DirectoryScanEngine (Core)
   1 reader thread, 2 overlapped reads      work-stealing, NtQueryDirectoryFile
   → bounded Channel → N-1 parsers          (FindFirstFileExW fallback)
            │                                          │
            └──────────────► TreeModel ◄───────────────┘
                     struct-of-arrays, DFS pre-order
                     subtree(i) = [i+1, SubtreeEnd[i])
                              │
             Aggregator (parallel post-order) · NameArena (Latin-1/UTF-16 blocks)
                              │
                   WinForms UI (virtual grid, treemap)
```

Key decisions:

- **No object per file.** Columns (`Parent`, `SubtreeEnd`, `Size`, `Allocated`, `Flags`,
  `Modified`, name offset) in flat arrays. Nodes are renumbered in DFS pre-order, so "everything
  under X" is a contiguous range: filtering, top-N, search and export are linear scans.
- **Disk-bound, not CPU-bound.** Parsing runs at ~11 GB/s on 15 threads in benchmarks; the
  reader saturates the NVMe at ~1.2 GB/s. Free MFT ranges (from `$MFT::$BITMAP`) are never read.
- **Defensive parsing.** Fixups are mandatory, every attribute is bounds-checked, bad records are
  counted and skipped. Tree build and aggregation are iterative; dangling parents and reference
  cycles land under a synthetic `<orphans>` node instead of crashing.
- **Immutable model swap.** Refresh builds a new model (incremental from the USN journal when
  possible, full rescan when the journal wrapped or >100 k records changed) and replaces the
  displayed one; selection is preserved.
- **UI thread never blocks.** Scan, refresh, search, largest-files and export run as cancellable
  background tasks.

Counting rules (hard links, ADS, NTFS metafiles, WOF/compressed/sparse) are documented in [`docs/DESIGN-NOTES.fr.md`](docs/DESIGN-NOTES.fr.md).

---

## Performance (reference machine)

Ryzen 7 PRO 4750U, Samsung 970 EVO Plus NVMe, Windows 11, .NET 8.0.33, `C:` = 335 GB / ~2.0 M items.

| Metric | Result |
|---|---|
| Full scan of `C:\`, MFT engine, end to end | 2.2–2.45 s |
| Peak working set, 2 M items | 193 MB |
| Tree click / expand (incl. repaint) | 6.3 ms avg · 3.4–13.1 ms (WinSxS, 28 k children) |
| Directory engine, `C:\` | 21–37 s, 291 MB peak |
| USN refresh | 30–270 ms depending on change type |

Full per-phase breakdown and benchmark tables: [`docs/DESIGN-NOTES.fr.md`](docs/DESIGN-NOTES.fr.md).

```powershell
dotnet run -c Release --project bench\DiskTreeScanner.Bench -- --filter *
```

---

## CLI

```text
DiskTreeScanner.Cli <path> [options]
DiskTreeScanner.Cli --usn-test [FOLDER]

  --engine auto|mft|dir        auto = MFT when elevated on NTFS, else directory engine
  --no-special                 exclude NTFS metafiles ($MFT, $LogFile, $Extend…)
  --ads                        add named streams to file sizes
  --hardlinks once|each|mixed
  --top N, --depth N           largest files / folder levels printed
  --csv FILE                   export the tree as CSV
  --repeat N                   N scans: best / mean / worst
  --verbose                    print the activity log
  --help                       all options, including engine tuning knobs
```

The app also has automation switches (`--screenshot`, `--ui-bench`, `--select`, `--size`,
`--lang`, `--scale`, …) used for UI verification; see the design notes.

---

## Runtime files & privacy

- Settings: `%LOCALAPPDATA%\DiskTreeScanner\settings.json`
- Activity log: `%LOCALAPPDATA%\DiskTreeScanner\logs\activity-YYYYMMDD.log` (also *View › Activity log*)

No telemetry, no network access other than the shares you choose to scan. Opening a raw volume
handle is logged because EDR/Defender may flag it; *Force directory scan* avoids it.

---

## Known limitations

- On a busy system volume, refresh is almost always "structural" (re-layout from existing nodes,
  still no MFT re-read). The ancestors-only fast path mostly applies to data volumes.
- The directory engine is ~10× slower and uses more memory on large system drives; that is the
  cost of running unelevated.
- During a rescan the previous result stays in memory until replaced.
- First expansion of a huge folder sorted *by name* can exceed 16 ms (~32 ms for WinSxS); the
  order is cached afterwards.
- Column filters apply to the tree and *Largest files*, not to the treemap.
- No delete action in v1.
