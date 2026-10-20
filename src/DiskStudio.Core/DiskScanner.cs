using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Threading.Channels;

namespace DiskStudio.Core;

/// <summary>
/// Metadata-only indexing. Uses native buffered enumeration through .NET's
/// FileSystemEnumerable; never opens file contents or follows directory links.
/// </summary>
public sealed class DiskScanner
{
    private readonly record struct Entry(string Name, long Length, FileAttributes Attributes, DateTime ModifiedUtc);
    private sealed record Work(ScanNode Node, string Path);

    public async Task<ScanResult> ScanAsync(string path, ScanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (options.Workers is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(options));
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        var rootAttributes = File.GetAttributes(path);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Choose the real target folder instead of a directory link or junction.");

        var root = new ScanNode { Name = path, IsDirectory = true, Attributes = rootAttributes, Children = [] };
        var queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions
        { SingleWriter = false, SingleReader = options.Workers == 1, AllowSynchronousContinuations = false });
        var issues = new ConcurrentQueue<ScanIssue>();
        long files = 0, folders = 1, bytes = 0, errors = 0, links = 0, pending = 1;
        var clock = Stopwatch.StartNew();
        var cancelled = false;

        ScanProgress Snapshot() => new(Interlocked.Read(ref files), Interlocked.Read(ref folders),
            Interlocked.Read(ref bytes), Interlocked.Read(ref errors), Interlocked.Read(ref links), clock.Elapsed);
        void AddIssue(string source, Exception error)
        {
            if (Interlocked.Increment(ref errors) <= 200)
                issues.Enqueue(new ScanIssue(source, error.Message));
        }
        using var tickerStop = new CancellationTokenSource();
        var ticker = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(150));
            try { while (await timer.WaitForNextTickAsync(tickerStop.Token)) progress?.Report(Snapshot()); }
            catch (OperationCanceledException) { }
        });
        queue.Writer.TryWrite(new Work(root, path));

        async Task Worker()
        {
            while (await queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (queue.Reader.TryRead(out var work))
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // 64 KiB buffers reduce native query calls. Metadata is taken from
                        // the enumeration entry, avoiding FileInfo allocations / per-file stat.
                        var entries = new FileSystemEnumerable<Entry>(work.Path,
                            (ref FileSystemEntry entry) => new Entry(entry.FileName.ToString(),
                                entry.IsDirectory ? 0 : entry.Length, entry.Attributes, entry.LastWriteTimeUtc.UtcDateTime),
                            new EnumerationOptions
                            {
                                RecurseSubdirectories = false, IgnoreInaccessible = false,
                                AttributesToSkip = 0, ReturnSpecialDirectories = false,
                                BufferSize = 64 * 1024
                            });
                        foreach (var entry in entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            bool directory = (entry.Attributes & FileAttributes.Directory) != 0;
                            var node = new ScanNode
                            {
                                Name = entry.Name, Parent = work.Node, IsDirectory = directory,
                                Attributes = entry.Attributes, ModifiedUtc = entry.ModifiedUtc,
                                Bytes = directory ? 0 : entry.Length, FileCount = directory ? 0 : 1,
                                Children = directory ? [] : null
                            };
                            work.Node.Children!.Add(node);
                            if (directory)
                            {
                                Interlocked.Increment(ref folders);
                                if (node.IsLink) { Interlocked.Increment(ref links); continue; }
                                Interlocked.Increment(ref pending);
                                queue.Writer.TryWrite(new Work(node, Path.Combine(work.Path, node.Name)));
                            }
                            else
                            {
                                Interlocked.Increment(ref files);
                                Interlocked.Add(ref bytes, node.Bytes);
                            }
                        }
                    }
                    catch (OperationCanceledException) { work.Node.Incomplete = true; throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    { work.Node.Incomplete = true; AddIssue(work.Path, ex); }
                    finally
                    {
                        if (Interlocked.Decrement(ref pending) == 0) queue.Writer.TryComplete();
                    }
                }
            }
        }

        try
        {
            var workers = Enumerable.Range(0, options.Workers).Select(_ => Task.Run(Worker)).ToArray();
            try { await Task.WhenAll(workers).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { cancelled = true; }
        }
        finally
        {
            tickerStop.Cancel();
            await ticker.ConfigureAwait(false);
            queue.Writer.TryComplete();
        }

        // Iterative post-order totals: no recursion / call-stack limits on deep trees.
        var directories = new List<ScanNode>();
        var stack = new Stack<ScanNode>();
        stack.Push(root);
        while (stack.TryPop(out var node))
        {
            directories.Add(node);
            foreach (var child in node.Children!)
                if (child.IsDirectory) stack.Push(child);
        }
        for (int i = directories.Count - 1; i >= 0; i--)
        {
            var directory = directories[i];
            foreach (var child in directory.Children!)
            {
                directory.Bytes += child.Bytes;
                directory.FileCount += child.FileCount;
                directory.Incomplete |= child.Incomplete;
            }
        }
        root.Incomplete |= cancelled;
        clock.Stop();
        var statistics = Snapshot();
        progress?.Report(statistics);
        return new ScanResult(root, statistics, issues.ToArray(), cancelled);
    }
}
