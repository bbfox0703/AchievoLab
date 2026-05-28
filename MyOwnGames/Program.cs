using Avalonia;
using CommonUtilities;

namespace MyOwnGames;

internal sealed class Program
{
    [System.STAThread]
    public static int Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (System.Exception ex)
        {
            // Native AOT stack traces are gutted, and this runs before Serilog and the
            // global exception handlers come online (those register inside App.Initialize,
            // after XAML load). A crash here is our only signal for bootstrap failures.
            ExceptionHandlingHelper.WriteStartupCrashLog("MyOwnGames", ex);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
