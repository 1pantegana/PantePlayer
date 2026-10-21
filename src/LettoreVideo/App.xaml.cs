using System.IO;
using System.Windows;

namespace LettoreVideo;

public partial class App : Application
{
    private static readonly string CrashLogPath =
        Path.Combine(Path.GetTempPath(), "LettoreVideo-crash.txt");

    /// <summary>
    /// Folder passed on the command line, if any. Accepts either a folder or a
    /// video file (in which case its folder is loaded), so the exe works as a
    /// "Open with" / "Send to" target.
    /// </summary>
    public static string? StartupFolder { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Without this a failure inside the WPF layout pass kills the process with
        // no visible reason; surface it instead.
        DispatcherUnhandledException += (_, args) =>
        {
            Report(args.Exception, "Errore imprevisto");
            args.Handled = true;
        };

        // The mpv event loop runs on its own thread, which the dispatcher handler
        // above does not cover: without this, a failure there ends the process
        // silently. Nothing can be "handled" here — the runtime is already tearing
        // down — but the crash at least leaves a trace.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Report(ex, "Errore irreversibile");
        };

        if (e.Args.Length > 0)
        {
            string arg = e.Args[0];
            if (Directory.Exists(arg))
                StartupFolder = arg;
            else if (File.Exists(arg))
                StartupFolder = Path.GetDirectoryName(arg);
        }

        base.OnStartup(e);
    }

    private static void Report(Exception exception, string title)
    {
        string report = exception.ToString();

        try
        {
            File.WriteAllText(CrashLogPath, report);
        }
        catch
        {
            // Reporting must never mask the original failure.
        }

        MessageBox.Show($"{report}\n\nCopia salvata in:\n{CrashLogPath}",
            title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
