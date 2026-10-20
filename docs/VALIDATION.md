# Validation scope

Automated CI validates the actual Windows build before release. Read the current workflow result and attached reports for pass/fail status.

## Covered automatically
- Real temporary files with exact expected counts and sizes, including zero-length files.
- Nested aggregation and Unicode names.
- Single-, two-, and four-worker consistency.
- Largest-file top-K selection and combined filters.
- Pre-cancelled scans and explicit partial-result labeling.
- Missing-root errors.
- Windows junction cycle avoidance and root-junction rejection.
- Paths exceeding the legacy MAX_PATH length.
- Treemap coverage, bounds, and area proportionality.
- Synthetic 20,000-file benchmark, with cache caveats.
- Startup of the published self-contained EXE, a real scan, view switching, and light/dark rendering.

## Manual validation still required
- A large real system drive with millions of entries and a long-running scan.
- Cancellation during a disconnected or stalled network filesystem request.
- Restricted directories under a standard non-administrator account.
- Sparse, compressed, cloud-placeholder, and hard-linked files (logical-size semantics).
- Explorer integration and interactive recycling, including cancellation and unavailable Recycle Bin policies.
- Windows 10/11 desktop display scaling and assistive technologies.
- ARM64 is not runtime-validated or included in the initial release.

Do not describe a passing synthetic benchmark as a whole-drive performance guarantee. Never suppress unreadable-folder counts or present logical bytes as physical allocation.
