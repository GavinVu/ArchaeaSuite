using Avalonia;

namespace ArchaeaPhoto;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

/// <summary>Unhandled errors are appended to crash.log beside the settings file.</summary>
internal static class CrashLog
{
    public static void Write(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ArchaeaPhoto {App.Version}\n{ex}\n\n");
        }
        catch
        {
            // Nothing sensible left to do.
        }
    }
}
