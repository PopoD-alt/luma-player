using System.Windows;
using System.IO;

namespace Luma.WindowsApp;
internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var smoke = args.Length == 3 && args[0] == "--smoke-test";
        var temporary = smoke ? Path.Combine(Path.GetTempPath(), "Luma-review-" + Guid.NewGuid().ToString("N")) : null;
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new PlayerWindow(temporary is null ? null : Path.Combine(temporary, "library.json"));
            app.MainWindow = window;
            window.Loaded += async (_, _) =>
            {
                if (smoke)
                {
                    try { await window.RunSmokeAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2])); app.Shutdown(0); }
                    catch (Exception e) { Console.Error.WriteLine("FAIL: Windows UI smoke: " + e); app.Shutdown(1); }
                }
                else if (args.Length > 0) await window.ImportAsync(args.Where(File.Exists));
            };
            return app.Run(window);
        }
        catch (Exception e)
        {
            if (smoke) Console.Error.WriteLine(e);
            else MessageBox.Show("Luma could not start.\n\n" + e.Message, "Luma Player", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally { if (temporary is not null) try { Directory.Delete(temporary, true); } catch (IOException) { } }
    }
}
