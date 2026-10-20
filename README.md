# Disk Studio
### Know your space. Created and provided by KAREER.

A native Windows disk-space analyzer with a responsive interface, metadata-only indexing, an interactive storage map, and a searchable largest-files view.

[Download the Windows app](https://github.com/ajaykareer/disk-studio/releases/latest) · [Build status](https://github.com/ajaykareer/disk-studio/actions/workflows/build.yml) · [Report an issue](https://github.com/ajaykareer/disk-studio/issues)

![Disk Studio dark theme](https://github.com/ajaykareer/disk-studio/releases/download/v1.0.0/disk-studio-dark.png)

*The screenshot is rendered from the actual Windows app using synthetic sample files.*

## Get started
1. Open **Releases** and download **DiskStudio-1.0.0-win-x64.exe**.
2. Run the EXE, choose a drive or browse to a folder, and select **Scan storage**.
3. Double-click a folder to explore it. Use **Up** or **Root** to navigate.
4. Switch to **Largest files (all)** to search every file in the scan.
5. Filter by name, extension, minimum size, or modified date.
6. Select an item to open its location or copy its path. The treemap selects the same items as the table.

The self-contained EXE includes its .NET runtime; a separate .NET install is not needed. First launch extracts native runtime components to the user's temporary .NET bundle area. It does not install a service or run in the background after closing. It saves only the selected theme under LocalAppData/KAREER/Disk Studio.

The executable is unsigned. Release assets include SHA-256 checksums so you can verify that a download matches the published build.

## Features
- Drive used/free capacity separate from indexed file-size totals.
- Folder navigation and an all-files view, largest first.
- Interactive proportional storage map, with hover information and click selection.
- Name, extension, size, and modified-date filters.
- Dark and light themes, resizable panels, keyboard-accessible native controls.
- Progress counters, elapsed time, average throughput, and cancellation.
- Partial results after cancellation, with explicit status.
- Scan details listing unreadable folders and skipped directory links.
- Export displayed results to UTF-8 CSV with spreadsheet formula-prefix protection.
- Open location without executing the selected file.
- Confirmed, single-file recycling on local fixed drives. No folder cleanup or automatic deletion.

## Performance design
The scanner uses .NET's FileSystemEnumerable with a **64 KiB enumeration buffer**, collecting size and timestamps from each entry. It does not read file contents, allocate a FileInfo for every file, hash files, or load shell icons during a scan.

- **Eco:** 1 worker; often appropriate for rotating disks.
- **Balanced:** 2 workers; default.
- **Fast:** 4 workers; may help SSDs or high-latency folders. More workers are not always faster.
- Each indexed item stores its name and parent reference; full paths are assembled when needed.
- Totals are aggregated iteratively after enumeration, avoiding recursive call-stack limits.
- Progress is throttled to roughly 150 ms.
- Scanning and filtering run off the UI thread.
- Largest-file filtering uses a bounded min-heap instead of sorting the whole index.
- The table virtualizes rows and displays up to 5,000 matches. The map draws up to 500 entries plus a remainder tile.
- The index itself uses memory proportional to the number of files and folders. The 5,000-row display limit does not cap scan size or index memory.

This version performs a fresh directory scan. It is **not** a raw NTFS MFT scanner, does not maintain a persistent USN index, and makes no claim to outperform WizTree. Runtime depends on storage, directory structure, permissions, antivirus, and cache state.

A reproducible benchmark scans **20,000 generated files in 100 folders**, three times in each worker mode. Its JSON report is included with validated releases. These are synthetic warm-cache measurements, not cold whole-drive performance claims.

## Understanding the numbers
The table and map show **logical file sizes**, using binary units (KiB, MiB, GiB). They do not measure allocated disk space:
- Hard-linked files are counted once per discovered path.
- Sparse, compressed, and online-only cloud files can occupy less physical space than their logical size.
- Directory junctions, symbolic links, and other directory reparse points are listed but not followed. Choosing one as the scan root is rejected.
- File placeholders are enumerated without intentionally reading or downloading their contents.
- Inaccessible folders are reported and omitted; only the first 200 error details are retained.
- Files can change during or after a scan. Results are a snapshot, not a live transactionally consistent index.
- Folder modified-date filters use that folder's own timestamp.
- **Largest files (all)** searches the whole selected scan, while **Folder contents** searches the current level.
- CSV exports the displayed rows, not hidden matches beyond the 5,000-row cap.
- Cancellation is checked between entries. A stalled filesystem/network request must return before cancellation completes.

The drive capacity card uses Windows drive statistics. Differences from indexed logical sizes are expected.

## Recycling
Recycling requires a selected non-link file, a local fixed drive, an unchanged size/timestamp, and explicit confirmation. System-attributed files and directories are rejected. The Windows shell operation requests recycle-on-delete and error reporting; the application contains no permanent-delete fallback. After recycling, a new scan refreshes results.

Do not infer that a file is unnecessary merely because it is large. Recycle Bin behavior depends on Windows policy and the target filesystem. Use **Open location** for other file-management operations.

## Build from source
Use a supported Windows x64 system and the **.NET 10 SDK**. The portable app is intended for Windows versions supported by .NET 10. The build pipeline uses a Windows GitHub Actions runner.

~~~powershell
git clone https://github.com/ajaykareer/disk-studio.git
cd disk-studio
.\Build.ps1 -Test
~~~

Output: **artifacts/win-x64/DiskStudio.exe**

For development:
~~~powershell
dotnet run --project src/DiskStudio
~~~

An ARM64 cross-publish option exists in Build.ps1, but the release and automated runtime checks currently cover **Windows x64**.

## Validation
~~~powershell
dotnet run --project tests/DiskStudio.Tests -c Release -- artifacts/validation
.\Build.ps1
.\scripts/Smoke-Test.ps1 -Executable (Resolve-Path artifacts/win-x64/DiskStudio.exe)
~~~

The core harness uses temporary, uniquely named fixtures to test exact sizes, nested totals, 1/2/4 workers, Unicode names, filters, top-K selection, cancellation, missing roots, Windows junction cycles, long paths, and proportional treemap geometry.

The UI smoke test starts the **published EXE**, scans synthetic sample data, exercises the largest-files view, and renders both themes. It does not delete or recycle personal files. Real-drive behavior, unavailable-network cancellation, and interactive shell recycling need manual checks; see [validation notes](docs/VALIDATION.md).

## Project layout
- src/DiskStudio.Core — scanner, in-memory index, queries, treemap geometry.
- src/DiskStudio — WPF interface and Windows shell integration.
- tests/DiskStudio.Tests — executable regression harness and benchmark.
- scripts/Smoke-Test.ps1 — published-app validation and screenshots.
- .github/workflows/build.yml — Windows build, validation, artifacts, and manual release publishing.

No telemetry, analytics, cloud scan uploads, startup tasks, or automatic cleanup.

## License
MIT. Copyright (c) 2026 Ajay Kareer.
