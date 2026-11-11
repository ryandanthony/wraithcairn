using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

namespace Wraithcairn;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.WriteLine($"wraithcairn {AppInfo.Version}");
            return;
        }

        if (args.Contains("--install-desktop") || args.Contains("--uninstall-desktop"))
        {
            // Asset loading needs the Avalonia platform, but no window.
            BuildAvaloniaApp().SetupWithoutStarting();
            try
            {
                Console.WriteLine(args.Contains("--install-desktop")
                    ? DesktopIntegration.Install()
                    : DesktopIntegration.Uninstall());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"wraithcairn: {ex.Message}");
                Environment.ExitCode = 1;
            }

            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Prefer GPU compositing everywhere; software only as last resort.
            .With(new X11PlatformOptions
            {
                RenderingMode =
                [
                    X11RenderingMode.Glx,
                    X11RenderingMode.Egl,
                    X11RenderingMode.Software,
                ],
            })
            .With(new Win32PlatformOptions
            {
                RenderingMode =
                [
                    Win32RenderingMode.AngleEgl,
                    Win32RenderingMode.Wgl,
                    Win32RenderingMode.Software,
                ],
            })
            .With(new MacOSPlatformOptions())
            .WithInterFont()
            .LogToTrace();
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Linux task managers group/label windows via a matching .desktop
            // file; register ours on normal launches (cheap no-op once done).
            string[] cli = Environment.GetCommandLineArgs();
            if (!cli.Contains("--selftest") && !cli.Contains("--dump-panes"))
            {
                Task.Run(DesktopIntegration.EnsureRegistered);
            }

            // Splash first. The main window (whose restore blocks the UI
            // thread while panes are built) is only created once the splash
            // has actually painted, otherwise it never gets a frame on screen.
            if (MainWindow.SplashEnabled)
            {
                SplashWindow splash = new();
                MainWindow.Splash = splash;
                splash.Show();
                splash.FirstFrame.ContinueWith(
                    _ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        MainWindow main = new();
                        desktop.MainWindow = main;
                        main.Show();
                    }),
                    TaskScheduler.Default);
            }
            else
            {
                desktop.MainWindow = new MainWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
