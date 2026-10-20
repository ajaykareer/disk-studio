using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskStudio.Core;

namespace DiskStudio;

public sealed record MapItem(string Name, long Bytes, ScanNode? Node);
public sealed class TreemapControl : FrameworkElement
{
    private IReadOnlyList<MapItem> items = [];
    private IReadOnlyList<MapRect> rectangles = [];
    private readonly Brush[] palette = new[] { "#167D9A", "#087E8B", "#5363BC", "#9560B5", "#AE6641", "#337E58", "#AE527B", "#7970B0" }
        .Select(x => { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(x)!; b.Freeze(); return (Brush)b; }).ToArray();
    private int hover = -1;
    public event Action<ScanNode>? NodeSelected;
    public event Action<ScanNode>? NodeOpened;
    public TreemapControl()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        MouseMove += (_, e) =>
        {
            var p = e.GetPosition(this);
            int index = Hit(p);
            if (index == hover) return;
            hover = index;
            ToolTip = index >= 0 ? $"{items[index].Name}\n{SizeFormatter.Format(items[index].Bytes)}\nClick to select · Double-click folders to explore" : null;
        };
        MouseLeave += (_, _) => { hover = -1; ToolTip = null; };
        MouseLeftButtonDown += (_, e) =>
        {
            int index = Hit(e.GetPosition(this));
            if (index >= 0 && items[index].Node is ScanNode node)
            { NodeSelected?.Invoke(node); if (e.ClickCount == 2) NodeOpened?.Invoke(node); }
        };
        SizeChanged += (_, _) => InvalidateVisual();
    }
    public void SetItems(IReadOnlyList<MapItem> value)
    { items = value; hover = -1; ToolTip = null; InvalidateVisual(); }
    private int Hit(Point p)
    {
        foreach (var r in rectangles)
            if (p.X >= r.X && p.X < r.X + r.Width && p.Y >= r.Y && p.Y < r.Y + r.Height) return r.Index;
        return -1;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        rectangles = TreemapLayout.Build(items.Select(i => i.Bytes).ToArray(), ActualWidth, ActualHeight);
        if (rectangles.Count == 0)
        {
            var label = new FormattedText("Your storage map appears here after scanning.", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, (Brush)FindResource("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(16, 16));
            return;
        }
        foreach (var r in rectangles)
        {
            if (r.Width < 2 || r.Height < 2) continue;
            var item = items[r.Index];
            var rect = new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2));
            string key = item.Node?.Extension is string ext && ext.Length > 0 ? ext : item.Name;
            uint hash = 2166136261;
            foreach (char c in key) hash = (hash ^ c) * 16777619;
            dc.DrawRoundedRectangle(palette[hash % (uint)palette.Length], null, rect, 3, 3);
            if (rect.Width < 64 || rect.Height < 32) continue;
            var text = new FormattedText(item.Name + "\n" + SizeFormatter.Format(item.Bytes), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { MaxTextWidth = Math.Max(1, rect.Width - 14), MaxTextHeight = Math.Max(1, rect.Height - 10), Trimming = TextTrimming.CharacterEllipsis };
            dc.PushClip(new RectangleGeometry(rect));
            dc.DrawText(text, new Point(rect.X + 7, rect.Y + 5));
            dc.Pop();
        }
    }
}
