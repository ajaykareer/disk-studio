using System.Diagnostics;
using System.Text.Json;
using DiskStudio.Core;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}
static void MakeFile(string path, int length)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var stream = File.Create(path); stream.SetLength(length);
}
static async Task<ScanResult> Scan(string path, int workers = 2, CancellationToken token = default)
    => await new DiskScanner().ScanAsync(path, new ScanOptions(workers), cancellationToken: token).WaitAsync(TimeSpan.FromSeconds(30));

string sandbox = Path.Combine(Path.GetTempPath(), "DiskStudio-Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
string output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(sandbox, "results");
Directory.CreateDirectory(output);
try
{
    string fixture = Path.Combine(sandbox, "accuracy");
    MakeFile(Path.Combine(fixture, "zero.txt"), 0);
    MakeFile(Path.Combine(fixture, "Photos", "sunset.jpg"), 1024);
    MakeFile(Path.Combine(fixture, "Photos", "portrait.JPG"), 2048);
    MakeFile(Path.Combine(fixture, "Documents", "report.txt"), 8192);
    MakeFile(Path.Combine(fixture, "Documents", "Nested", "report-final.txt"), 4096);
    MakeFile(Path.Combine(fixture, "中文 ਪੰਜਾਬੀ", "résumé.txt"), 512);
    Directory.CreateDirectory(Path.Combine(fixture, "empty"));
    File.SetLastWriteTimeUtc(Path.Combine(fixture, "zero.txt"), DateTime.UtcNow.AddYears(-2));
    long expected = 1024 + 2048 + 8192 + 4096 + 512;
    foreach (int workers in new[] { 1, 2, 4 })
    {
        var r = await Scan(fixture, workers);
        Check(r.Root.Bytes == expected && r.Statistics.Bytes == expected, $"exact size totals with {workers} worker(s)");
        Check(r.Root.FileCount == 6 && r.Statistics.Files == 6, $"file counts with {workers} worker(s)");
        Check(!r.Cancelled && r.Statistics.Issues == 0, "complete clean scan");
    }
    var result = await Scan(fixture);
    var photos = result.Root.Children!.Single(n => n.Name == "Photos");
    Check(photos.Bytes == 3072 && photos.FileCount == 2, "subfolder aggregation");
    Check(result.Root.Descendants().Single(n => n.Name == "résumé.txt").FullPath == Path.Combine(fixture, "中文 ਪੰਜਾਬੀ", "résumé.txt"), "Unicode path reconstruction");
    var top = IndexQuery.Execute(result.Root, new QueryOptions(RecursiveFiles: true, Limit: 2));
    Check(top.Matches == 6 && top.Nodes.Count == 2 && top.Nodes[0].Bytes == 8192 && top.Nodes[1].Bytes == 4096 && top.Bytes == expected, "bounded top-K preserves all-match totals");
    var jpg = IndexQuery.Execute(result.Root, new QueryOptions(Extension: "JPG", RecursiveFiles: true));
    Check(jpg.Matches == 2 && jpg.Bytes == 3072, "case-insensitive extension filter");
    Check(IndexQuery.Execute(result.Root, new QueryOptions(Search: "REPORT", MinimumBytes: 5000, RecursiveFiles: true)).Matches == 1, "combined name / size filter");
    Check(IndexQuery.Execute(result.Root, new QueryOptions(ModifiedSinceUtc: DateTime.UtcNow.AddDays(-1), RecursiveFiles: true)).Matches == 5, "modified-date filter");
    var empty = await Scan(Path.Combine(fixture, "empty"));
    Check(empty.Root.Bytes == 0 && empty.Statistics.Files == 0, "empty folder");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        var partial = await Scan(fixture, token: cancelled.Token);
        Check(partial.Cancelled && partial.Root.Incomplete, "cancellation completes with explicitly partial results");
    }
    bool missingFailed = false;
    try { await Scan(Path.Combine(sandbox, "missing")); } catch (DirectoryNotFoundException) { missingFailed = true; }
    Check(missingFailed, "missing root produces an actionable error");

    if (OperatingSystem.IsWindows())
    {
        string junction = Path.Combine(fixture, "cycle");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(junction); start.ArgumentList.Add(fixture);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "junction fixture created");
        var linked = await Scan(fixture);
        Check(linked.Root.Bytes == expected && linked.Statistics.SkippedLinks == 1, "junction cycle is skipped without double counting");
        bool rejected = false;
        try { await Scan(junction); } catch (IOException) { rejected = true; }
        Check(rejected, "junction scan root is rejected");
        Directory.Delete(junction);
        string deep = fixture;
        for (int i = 0; i < 40; i++) deep = Path.Combine(deep, "level-" + i.ToString("D3"));
        MakeFile(Path.Combine(deep, "deep.txt"), 17);
        Check((await Scan(fixture)).Root.Bytes == expected + 17, "long paths / deep trees scan without recursion limits");
    }
    var random = new Random(42);
    for (int run = 0; run < 30; run++)
    {
        long[] sizes = Enumerable.Range(0, 1 + run * 11).Select(_ => (long)random.Next(1, 100000)).ToArray();
        var map = TreemapLayout.Build(sizes, 1000, 600);
        Check(map.Count == sizes.Length, $"treemap coverage {run}");
        Check(Math.Abs(map.Sum(r => r.Width * r.Height) - 600000) < .01, "treemap total area");
        foreach (var r in map)
        {
            if (r.X < -.01 || r.Y < -.01 || r.X + r.Width > 1000.01 || r.Y + r.Height > 600.01) throw new Exception("Treemap outside bounds");
            double expectedArea = sizes[r.Index] / (double)sizes.Sum() * 600000;
            if (Math.Abs(r.Width * r.Height - expectedArea) > .01) throw new Exception("Treemap size proportion mismatch");
        }
    }
    Check(TreemapLayout.Build([0, 0], 100, 100).Count == 0, "zero-size treemap");
    Check(SizeFormatter.Format(1024).StartsWith("1"), "binary units");

    // Newly-created files imply warm caches. This is not a cold full-drive benchmark.
    string bench = Path.Combine(sandbox, "benchmark");
    long benchBytes = 0;
    const int count = 20000;
    for (int i = 0; i < count; i++)
    {
        int length = 256 + i % 4096; benchBytes += length;
        MakeFile(Path.Combine(bench, "batch-" + (i / 200).ToString("D3"), $"sample-{i:D6}.bin"), length);
    }
    var observations = new List<object>();
    foreach (int workers in new[] { 1, 2, 4 })
    {
        var times = new List<double>();
        for (int repeat = 0; repeat < 3; repeat++)
        {
            var r = await Scan(bench, workers);
            Check(r.Statistics.Files == count && r.Root.Bytes == benchBytes, $"benchmark accuracy {workers}/{repeat}");
            times.Add(r.Statistics.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        observations.Add(new { workers, medianMilliseconds = times[1], filesPerSecond = count / (times[1] / 1000), runsMilliseconds = times });
    }
    var report = new
    {
        description = "Synthetic metadata scan; 20,000 newly-created files / 100 folders. Warm caches. Not representative of cold full-drive scans.",
        os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount,
        files = count, logicalBytes = benchBytes, observations
    };
    File.WriteAllText(Path.Combine(output, "benchmark.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(report));
    File.WriteAllText(Path.Combine(output, "core-tests.txt"), "PASS: actual filesystem totals, parallel scans, Unicode, filters, top-K, cancellation, missing root, junction cycles, long paths, treemap invariants, benchmark accuracy.");
}
finally { Directory.Delete(sandbox, recursive: true); }
