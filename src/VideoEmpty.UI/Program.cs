using Avalonia;
using System;
using System.Threading.Tasks;
using VideoEmpty.Core.Diagnostics;

namespace VideoEmpty.UI;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("UnhandledException", "AppDomain unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("UnobservedTaskException", "Task scheduler unobserved exception", e.Exception);
            e.SetObserved();
        };

        Log.Info("Startup", $"VideoEmpty starting. Log path: {Log.LogPath}");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("Startup", "Fatal error during startup", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
