namespace DiskStudio.Core;

public sealed record QueryOptions(string Search = "", string Extension = "", long MinimumBytes = 0,
    DateTime? ModifiedSinceUtc = null, bool RecursiveFiles = false, int Limit = 5000);
public sealed record QueryResult(IReadOnlyList<ScanNode> Nodes, long Matches, long Bytes);

public static class IndexQuery
{
    // Bounded min-heap: O(N log K), O(K) temporary space, instead of sorting every file.
    public static QueryResult Execute(ScanNode directory, QueryOptions options, CancellationToken token = default)
    {
        if (options.Limit < 1) throw new ArgumentOutOfRangeException(nameof(options));
        var heap = new PriorityQueue<ScanNode, long>();
        long count = 0, bytes = 0;
        string extension = options.Extension.Trim();
        if (extension.Length > 0 && !extension.StartsWith('.')) extension = "." + extension;
        IEnumerable<ScanNode> source = options.RecursiveFiles
            ? directory.Descendants().Where(n => !n.IsDirectory)
            : directory.Children ?? Enumerable.Empty<ScanNode>();
        foreach (var node in source)
        {
            token.ThrowIfCancellationRequested();
            if (node.Bytes < options.MinimumBytes) continue;
            if (options.Search.Length > 0 && !node.Name.Contains(options.Search, StringComparison.OrdinalIgnoreCase)) continue;
            if (extension.Length > 0 && !node.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)) continue;
            if (options.ModifiedSinceUtc is DateTime since && node.ModifiedUtc < since) continue;
            count++; bytes += node.Bytes;
            heap.Enqueue(node, node.Bytes);
            if (heap.Count > options.Limit) heap.Dequeue();
        }
        return new QueryResult(heap.UnorderedItems.Select(i => i.Element)
            .OrderByDescending(n => n.Bytes).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToArray(), count, bytes);
    }
}

public readonly record struct MapRect(int Index, double X, double Y, double Width, double Height);
public static class TreemapLayout
{
    public static IReadOnlyList<MapRect> Build(IReadOnlyList<long> sizes, double width, double height)
    {
        var result = new List<MapRect>();
        if (width <= 0 || height <= 0 || sizes.Count == 0) return result;
        var prefix = new double[sizes.Count + 1];
        for (int i = 0; i < sizes.Count; i++) prefix[i + 1] = prefix[i] + Math.Max(0, sizes[i]);
        if (prefix[^1] == 0) return result;
        var stack = new Stack<(int Start, int End, double X, double Y, double W, double H)>();
        stack.Push((0, sizes.Count, 0, 0, width, height));
        while (stack.TryPop(out var r))
        {
            double total = prefix[r.End] - prefix[r.Start];
            if (total <= 0) continue;
            if (r.End - r.Start == 1) { result.Add(new MapRect(r.Start, r.X, r.Y, r.W, r.H)); continue; }
            double target = prefix[r.Start] + total / 2;
            int lo = r.Start + 1, hi = r.End - 1;
            while (lo < hi) { int mid = (lo + hi) / 2; if (prefix[mid] < target) lo = mid + 1; else hi = mid; }
            int split = lo;
            if (split > r.Start + 1 && Math.Abs(prefix[split - 1] - target) < Math.Abs(prefix[split] - target)) split--;
            double ratio = (prefix[split] - prefix[r.Start]) / total;
            if (r.W >= r.H)
            {
                double first = r.W * ratio;
                stack.Push((split, r.End, r.X + first, r.Y, r.W - first, r.H));
                stack.Push((r.Start, split, r.X, r.Y, first, r.H));
            }
            else
            {
                double first = r.H * ratio;
                stack.Push((split, r.End, r.X, r.Y + first, r.W, r.H - first));
                stack.Push((r.Start, split, r.X, r.Y, r.W, first));
            }
        }
        return result;
    }
}
