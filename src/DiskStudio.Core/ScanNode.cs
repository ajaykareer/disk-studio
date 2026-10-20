namespace DiskStudio.Core;

public sealed class ScanNode
{
    public required string Name { get; init; }
    public ScanNode? Parent { get; init; }
    public bool IsDirectory { get; init; }
    public FileAttributes Attributes { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public long Bytes { get; internal set; }
    public long FileCount { get; internal set; }
    public bool Incomplete { get; internal set; }
    public List<ScanNode>? Children { get; init; }
    public bool IsLink => (Attributes & FileAttributes.ReparsePoint) != 0;
    public string Extension => IsDirectory ? "" : Path.GetExtension(Name).ToLowerInvariant();
    public string Kind => IsDirectory ? (IsLink ? "Folder link (not followed)" : Incomplete ? "Folder (partial)" : "Folder") : IsLink ? "File / placeholder" : Extension.Length > 0 ? Extension : "File";
    public string SizeText => SizeFormatter.Format(Bytes);
    public string ModifiedText => ModifiedUtc == default ? "" : ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    // Only directories in the work queue and visible rows need full paths.
    public string FullPath
    {
        get
        {
            var names = new Stack<string>();
            ScanNode? current = this;
            while (current?.Parent != null) { names.Push(current.Name); current = current.Parent; }
            var path = current?.Name ?? "";
            while (names.Count > 0) path = Path.Combine(path, names.Pop());
            return path;
        }
    }
    public IEnumerable<ScanNode> Descendants()
    {
        var stack = new Stack<ScanNode>();
        stack.Push(this);
        while (stack.TryPop(out var node))
        {
            yield return node;
            if (node.Children is not null)
                for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
    }
}

public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
    public static string Format(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes:N0} B" : $"{value:N2} {Units[unit]}";
    }
}

public sealed record ScanIssue(string Path, string Message);
public sealed record ScanProgress(long Files, long Folders, long Bytes, long Issues, long SkippedLinks, TimeSpan Elapsed);
public sealed record ScanResult(ScanNode Root, ScanProgress Statistics, IReadOnlyList<ScanIssue> Issues, bool Cancelled);
public sealed record ScanOptions(int Workers = 2);
