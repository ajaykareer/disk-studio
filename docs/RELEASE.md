Disk Studio 1.0.0 — created and provided by KAREER.

Download **DiskStudio-1.0.0-win-x64.exe** to run the portable Windows app. A ZIP with the EXE, README, and license is also available.

Features:
- Buffered metadata indexing with 1, 2, or 4 workers and cancellation.
- Folder exploration, largest files, filters, and a proportional storage map.
- Dark/light themes, virtualized results, CSV export, and file-location actions.
- Confirmed single-file recycling via the Windows shell.

Validation includes exact filesystem totals, parallel scans, Unicode, filters, top-K queries, cancellation, junction cycles, long paths, treemap geometry, and launching/rendering the published WPF executable. Benchmark JSON and test reports are attached.

The benchmark uses 20,000 generated files with warm filesystem caches. It is not a cold-drive speed guarantee. Sizes are logical bytes rather than allocated physical storage; hard links can be counted per path. This version uses directory enumeration, not raw MFT scanning.

The executable is unsigned. SHA256SUMS.txt contains download checksums. Interactive recycling and real-drive performance still require testing on the user's Windows system.
