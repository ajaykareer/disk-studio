using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiskStudio.Core;
using Microsoft.Win32;

namespace DiskStudio;

public partial class MainWindow : Window
{
    private ScanResult? result;
    private ScanNode? current;
    private ScanNode[] visible = [];
    private CancellationTokenSource? scanCancellation;
    private CancellationTokenSource? queryCancellation;
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool ready, dark = true, scanning, closingAfterCancel;
    private long scanGeneration;
    private string lastScanPath = "";
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KAREER", "Disk Studio", "theme.txt");

    public MainWindow()
    {
        try { dark = !File.Exists(settingsPath) || File.ReadAllText(settingsPath).Trim() != "light"; } catch { }
        ApplyTheme();
        InitializeComponent();
        ThemeButton.Content = dark ? "Light theme" : "Dark theme";
        SearchBox.ToolTip = "Search file and folder names. Switch to Largest files (all) to search the whole scan.";
        debounce.Tick += async (_, _) => { debounce.Stop(); await RefreshViewAsync(); };
        StorageMap.NodeSelected += node => { FilesGrid.SelectedItem = node; FilesGrid.ScrollIntoView(node); };
        StorageMap.NodeOpened += async node => { if (node.IsDirectory && !node.IsLink) { current = node; ViewPicker.SelectedIndex = 0; await RefreshViewAsync(); } };
        ready = true;
        LoadDrives();
    }

    private void ApplyTheme()
    {
        var colors = dark
            ? new Dictionary<string, string> { ["Bg"] = "#0D141E", ["Surface"] = "#151F2D", ["Surface2"] = "#1D2B3C",
                ["Fg"] = "#EEF3F9", ["Muted"] = "#A4B3C5", ["Line"] = "#2A3B50", ["Accent"] = "#087F8C", ["Selection"] = "#21465C" }
            : new Dictionary<string, string> { ["Bg"] = "#F0F4F7", ["Surface"] = "#FFFFFF", ["Surface2"] = "#E8EEF4",
                ["Fg"] = "#172639", ["Muted"] = "#506279", ["Line"] = "#CED8E3", ["Accent"] = "#087F8C", ["Selection"] = "#CBE5EE" };
        foreach (var (name, value) in colors)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
            brush.Freeze();
            Application.Current.Resources[name] = brush;
        }
    }
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        dark = !dark; ApplyTheme(); ThemeButton.Content = dark ? "Light theme" : "Dark theme";
        StorageMap.InvalidateVisual();
        try { Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!); File.WriteAllText(settingsPath, dark ? "dark" : "light"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusLabel.Text = "Theme changed for this session; could not save preference."; }
    }
    private void LoadDrives()
    {
        // Drive metadata is requested only for local fixed/removable drives, never all network mounts.
        DrivePicker.ItemsSource = DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable).Select(d => d.Name).ToArray();
        if (DrivePicker.Items.Count > 0) DrivePicker.SelectedIndex = 0;
    }
    private async void Drive_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || DrivePicker.SelectedItem is not string drive) return;
        PathBox.Text = drive;
        await UpdateCapacityAsync(drive);
    }
    private async Task UpdateCapacityAsync(string path)
    {
        var info = await Task.Run(() =>
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path))!;
                if (root.StartsWith(@"\\", StringComparison.Ordinal)) return ("Network folder", "Drive capacity not requested");
                var drive = new DriveInfo(root);
                if (!drive.IsReady) return ("Not ready", "Drive is disconnected or empty");
                return (SizeFormatter.Format(drive.AvailableFreeSpace) + " free",
                    $"{SizeFormatter.Format(drive.TotalSize - drive.TotalFreeSpace)} used of {SizeFormatter.Format(drive.TotalSize)}");
            }
            catch { return ("Unavailable", "Capacity information could not be read"); }
        });
        if (!IsLoaded && !ready) return;
        FreeLabel.Text = info.Item1; DriveLabel.Text = info.Item2;
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to scan", Multiselect = false };
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FolderName;
    }
    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanPathAsync(PathBox.Text);
    private async Task ScanPathAsync(string path)
    {
        if (scanning) return;
        queryCancellation?.Cancel(); debounce.Stop();
        long generation = ++scanGeneration;
        scanCancellation = new CancellationTokenSource();
        scanning = true; SetScanState();
        result = null; current = null; visible = [];
        FilesGrid.ItemsSource = null; StorageMap.SetItems([]); UpdateSelection();
        Breadcrumb.Text = path; ViewSummary.Text = "Indexing metadata…";
        int workers = SpeedPicker.SelectedIndex switch { 0 => 1, 2 => 4, _ => 2 };
        var progress = new Progress<ScanProgress>(p =>
        {
            if (generation != scanGeneration || !scanning) return;
            SizeLabel.Text = SizeFormatter.Format(p.Bytes); FilesLabel.Text = p.Files.ToString("N0");
            FoldersLabel.Text = $"{p.Folders:N0} folders · {p.SkippedLinks:N0} links skipped";
            StatusLabel.Text = $"Scanning · {p.Elapsed.TotalSeconds:N1}s · {p.Files / Math.Max(.001, p.Elapsed.TotalSeconds):N0} files/s · {p.Issues:N0} unreadable folders";
        });
        try
        {
            lastScanPath = Path.GetFullPath(path.Trim());
            _ = UpdateCapacityAsync(lastScanPath);
            // Validation, enumeration and aggregation all run away from the dispatcher.
            result = await Task.Run(() => new DiskScanner().ScanAsync(lastScanPath, new ScanOptions(workers), progress, scanCancellation.Token));
            current = result.Root;
            var p = result.Statistics;
            SizeLabel.Text = SizeFormatter.Format(p.Bytes); FilesLabel.Text = p.Files.ToString("N0");
            FoldersLabel.Text = $"{p.Folders:N0} folders · {p.SkippedLinks:N0} links skipped";
            string state = result.Cancelled ? "Cancelled — partial results" : p.Issues > 0 ? "Finished — some folders unreadable" : "Scan complete";
            StatusLabel.Text = $"{state} · {p.Elapsed.TotalSeconds:N2}s · {p.Files / Math.Max(.001, p.Elapsed.TotalSeconds):N0} files/s · {p.Issues:N0} errors · logical sizes";
            await RefreshViewAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            StatusLabel.Text = "Scan failed: " + ex.Message;
            ViewSummary.Text = "Choose an accessible folder and try again.";
        }
        finally
        {
            scanning = false; SetScanState(); UpdateSelection();
            scanCancellation.Dispose(); scanCancellation = null;
            if (closingAfterCancel) Close();
        }
    }
    private void SetScanState()
    {
        ScanButton.IsEnabled = !scanning; CancelButton.IsEnabled = scanning;
        PathBox.IsEnabled = !scanning; DrivePicker.IsEnabled = !scanning; SpeedPicker.IsEnabled = !scanning;
        ScanProgressBar.IsIndeterminate = scanning;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    { scanCancellation?.Cancel(); StatusLabel.Text = "Cancelling… waiting for the current filesystem request to return."; CancelButton.IsEnabled = false; }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (scanning) { e.Cancel = true; closingAfterCancel = true; scanCancellation?.Cancel(); StatusLabel.Text = "Finishing cancellation before closing…"; }
        else { queryCancellation?.Cancel(); debounce.Stop(); }
    }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    { if (!ready || result is null) return; debounce.Stop(); debounce.Start(); }
    private async void Filter_Changed(object sender, SelectionChangedEventArgs e)
    { if (ready && result is not null) await RefreshViewAsync(); }
    private async Task RefreshViewAsync()
    {
        if (!ready || result is null || current is null) return;
        queryCancellation?.Cancel();
        var cts = new CancellationTokenSource(); queryCancellation = cts;
        var snapshot = result;
        var folder = ViewPicker.SelectedIndex == 1 ? snapshot.Root : current;
        long minimum = MinimumPicker.SelectedIndex switch { 1 => 10L << 20, 2 => 100L << 20, 3 => 1L << 30, _ => 0 };
        DateTime? since = DatePicker.SelectedIndex switch { 1 => DateTime.UtcNow.AddDays(-7), 2 => DateTime.UtcNow.AddDays(-30), 3 => DateTime.UtcNow.AddYears(-1), _ => null };
        var options = new QueryOptions(SearchBox.Text.Trim(), ExtensionBox.Text.Trim(), minimum, since, ViewPicker.SelectedIndex == 1, 5000);
        Breadcrumb.Text = folder.FullPath; Breadcrumb.ToolTip = folder.FullPath;
        try
        {
            var query = await Task.Run(() => IndexQuery.Execute(folder, options, cts.Token));
            if (cts.IsCancellationRequested || result != snapshot) return;
            visible = query.Nodes.ToArray(); FilesGrid.ItemsSource = visible;
            var map = visible.Where(n => n.Bytes > 0).Take(500).Select(n => new MapItem(n.Name, n.Bytes, n)).ToList();
            long shownBytes = map.Sum(i => i.Bytes);
            if (query.Bytes > shownBytes) map.Add(new MapItem("Other matching items", query.Bytes - shownBytes, null));
            StorageMap.SetItems(map);
            ViewSummary.Text = $"{query.Matches:N0} matches · {SizeFormatter.Format(query.Bytes)}" +
                (query.Matches > visible.Length ? $" · showing largest {visible.Length:N0}" : "") +
                " · Map: top 500 + remainder";
            UpdateSelection();
        }
        catch (OperationCanceledException) { }
        finally { if (queryCancellation == cts) queryCancellation = null; cts.Dispose(); }
    }
    private async void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is ScanNode { IsDirectory: true, IsLink: false } node)
        { current = node; ViewPicker.SelectedIndex = 0; await RefreshViewAsync(); }
    }
    private async void Up_Click(object sender, RoutedEventArgs e)
    { if (current?.Parent is ScanNode parent) { current = parent; ViewPicker.SelectedIndex = 0; await RefreshViewAsync(); } }
    private async void Root_Click(object sender, RoutedEventArgs e)
    { if (result is not null) { current = result.Root; ViewPicker.SelectedIndex = 0; await RefreshViewAsync(); } }
    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();
    private void UpdateSelection()
    {
        if (OpenButton is null) return;
        var node = FilesGrid.SelectedItem as ScanNode;
        OpenButton.IsEnabled = CopyButton.IsEnabled = node is not null && !scanning;
        RecycleButton.IsEnabled = node is { IsDirectory: false, IsLink: false } && !scanning;
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not ScanNode node) return;
        try
        {
            // explorer selects a file instead of executing it.
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.Arguments = "/select,\"" + node.FullPath + "\"";
            Process.Start(start);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open location"); }
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not ScanNode node) return;
        try { Clipboard.SetText(node.FullPath); StatusLabel.Text = "Path copied."; }
        catch (Exception ex) { StatusLabel.Text = "Could not copy: " + ex.Message; }
    }
    private async void Recycle_Click(object sender, RoutedEventArgs e)
    {
        if (scanning || FilesGrid.SelectedItem is not ScanNode { IsDirectory: false, IsLink: false } node) return;
        string path = node.FullPath;
        try
        {
            // Re-check metadata because files can change after a scan.
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != node.Bytes || file.LastWriteTimeUtc != node.ModifiedUtc ||
                (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Directory)) != 0)
                throw new IOException("This file changed, is a link, or is a protected system file. Rescan and manage it in File Explorer.");
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (drive.DriveType != DriveType.Fixed) throw new IOException("Recycling here is limited to local fixed drives. Use Open location for this file.");
            if (MessageBox.Show(this, $"Move this file to the Recycle Bin?\n\n{path}\n\n{node.SizeText}\nThe scan will refresh afterward.",
                    "Recycle selected file", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var outcome = RecycleService.Recycle(path, new System.Windows.Interop.WindowInteropHelper(this).Handle);
            if (outcome) await ScanPathAsync(lastScanPath);
            else StatusLabel.Text = "Recycle operation cancelled.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "File was not recycled", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (visible.Length == 0) { StatusLabel.Text = "Scan and display some results first."; return; }
        var dialog = new SaveFileDialog { FileName = "Disk-Studio-results.csv", Filter = "CSV files|*.csv" };
        if (dialog.ShowDialog(this) != true) return;
        var rows = visible.ToArray();
        try
        {
            await Task.Run(() =>
            {
                static string Csv(string text)
                {
                    // Neutralize spreadsheet formula prefixes before quoting.
                    if (text.Length > 0 && "=+-@\t\r\n".Contains(text[0])) text = "'" + text;
                    return "\"" + text.Replace("\"", "\"\"") + "\"";
                }
                using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
                writer.WriteLine("Name,Path,Kind,LogicalBytes,ModifiedUtc,PartialFolder");
                foreach (var node in rows)
                    writer.WriteLine($"{Csv(node.Name)},{Csv(node.FullPath)},{Csv(node.Kind)},{node.Bytes.ToString(CultureInfo.InvariantCulture)},{Csv(node.ModifiedUtc.ToString("O"))},{node.Incomplete}");
            });
            StatusLabel.Text = $"Exported {rows.Length:N0} displayed rows.";
        }
        catch (Exception ex) { StatusLabel.Text = "Export failed: " + ex.Message; }
    }
    private void Issues_Click(object sender, RoutedEventArgs e)
    {
        if (result is null) { MessageBox.Show(this, "Scan a folder or drive first.", "Scan details"); return; }
        var p = result.Statistics;
        var text = new StringBuilder($"Files: {p.Files:N0}\nFolders: {p.Folders:N0}\nLogical file sizes: {SizeFormatter.Format(p.Bytes)}\nElapsed: {p.Elapsed.TotalSeconds:N2}s\nSkipped directory links: {p.SkippedLinks:N0}\nUnreadable folders: {p.Issues:N0}\nCancelled: {result.Cancelled}\n\n");
        text.AppendLine("Sizes include each file path. Hard links can be counted more than once. Sparse/compressed/cloud files may use less physical space. Directory links are not followed. Inaccessible folders are not included. This is a scan-time snapshot, not a live index.\n");
        foreach (var issue in result.Issues) text.AppendLine(issue.Path + "\n  " + issue.Message);
        if (p.Issues > result.Issues.Count) text.AppendLine("\nOnly the first 200 errors are retained.");
        var box = new TextBox { Text = text.ToString(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16), Background = (Brush)FindResource("Surface"), Foreground = (Brush)FindResource("Fg") };
        new Window { Owner = this, Title = "Scan details", Width = 720, Height = 500, Content = box, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Disk Studio 1.0.0\nCreated and provided by KAREER\n\nNative Windows interface · local metadata scanning · no telemetry.\n\n64 KiB enumeration buffers, 1–4 workers, virtualized results, and a bounded largest-file index.\n\nSizes are logical bytes, not physical allocation. Scanning a whole drive may miss folders your account cannot read. Use Scan details to inspect omissions.\n\nhttps://github.com/ajaykareer/disk-studio",
        "About Disk Studio", MessageBoxButton.OK, MessageBoxImage.Information);

    public async Task RunSmokeAsync(string folder, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        PathBox.Text = folder;
        await ScanPathAsync(folder);
        if (result is null || result.Cancelled || result.Statistics.Files == 0 || visible.Length == 0)
            throw new InvalidOperationException("UI scan did not produce results.");
        ViewPicker.SelectedIndex = 1;
        await RefreshViewAsync();
        if (visible.Any(n => n.IsDirectory)) throw new InvalidOperationException("Largest files view contains a directory.");
        ViewPicker.SelectedIndex = 0;
        await RefreshViewAsync();
        foreach (bool useDark in new[] { true, false })
        {
            dark = useDark; ApplyTheme(); ThemeButton.Content = dark ? "Light theme" : "Dark theme";
            StorageMap.InvalidateVisual();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.InvokeAsync(() =>
            {
                var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                image.Render(this);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(outputDirectory, dark ? "disk-studio-dark.png" : "disk-studio-light.png"));
                png.Save(stream);
            }, DispatcherPriority.ApplicationIdle);
        }
        File.WriteAllText(Path.Combine(outputDirectory, "ui-smoke.txt"), $"PASS: startup, real scan ({result.Statistics.Files:N0} files), largest files, navigation view, both themes, WPF rendering.");
    }
}
