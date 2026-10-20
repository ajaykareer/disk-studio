using System.IO;
using System.Windows;

namespace DiskStudio;
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        bool smoke = args.Length == 3 && args[0] == "--smoke-test";
        app.DispatcherUnhandledException += (_, e) =>
        {
            if (smoke)
            {
                Directory.CreateDirectory(args[2]);
                File.WriteAllText(Path.Combine(args[2], "ui-error.txt"), e.Exception.ToString());
                e.Handled = true;
                app.Shutdown(2);
            }
            else
            {
                MessageBox.Show(e.Exception.Message, "Disk Studio", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            }
        };
        try
        {
        var window = new MainWindow();
        if (smoke)
            window.Loaded += async (_, _) =>
            {
                try { await window.RunSmokeAsync(args[1], args[2]); app.Shutdown(0); }
                catch (Exception ex)
                {
                    Directory.CreateDirectory(args[2]);
                    File.WriteAllText(Path.Combine(args[2], "ui-error.txt"), ex.ToString());
                    app.Shutdown(2);
                }
            };
        return app.Run(window);
        }
        catch (Exception ex)
        {
            if (smoke)
            {
                Directory.CreateDirectory(args[2]);
                File.WriteAllText(Path.Combine(args[2], "ui-error.txt"), ex.ToString());
            }
            else
                MessageBox.Show(ex.ToString(), "Disk Studio could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            return 2;
        }
    }
}
