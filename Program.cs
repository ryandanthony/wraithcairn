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
            // No Avalonia here: icons come from manifest resources, so this
            // works headless (install script over SSH, CI, no $DISPLAY).
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

        // Theme commands, no Avalonia. A running wraithcairn watches
        // config.json, so --set-theme repaints open windows right away.
        if (args.Contains("--download-themes") || args.Contains("--set-theme") || args.Contains("--list-themes"))
        {
            Environment.ExitCode = ThemeCommandAsync(args).GetAwaiter().GetResult();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static async Task<int> ThemeCommandAsync(string[] args)
    {
        try
        {
            if (args.Contains("--list-themes"))
            {
                ThemeCatalog.CatalogResult catalog = await ThemeCatalog.LoadAsync();
                foreach (ThemeEntry t in catalog.Themes)
                {
                    Console.WriteLine($"{(t.Installed ? "*" : " ")} {t.Name}{(t.Dark ? string.Empty : "  (light)")}");
                }

                Console.Error.WriteLine($"{catalog.Themes.Count} themes, {catalog.Themes.Count(t => t.Installed)} downloaded (*)"
                    + (catalog.Online ? string.Empty : $"; offline: {catalog.Error}"));
                return 0;
            }

            if (args.Contains("--download-themes"))
            {
                int written = await ThemeCatalog.DownloadAllAsync();
                Console.WriteLine($"downloaded {written} themes to {AppConfig.UserThemesDir}");
            }

            int at = Array.IndexOf(args, "--set-theme");
            if (at >= 0)
            {
                if (at + 1 >= args.Length || args[at + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("usage: wraithcairn --set-theme NAME   (or --set-theme ghostty to inherit Ghostty's)");
                    return 2;
                }

                string name = args[at + 1];
                if (name.Equals("ghostty", StringComparison.OrdinalIgnoreCase) && !ThemeCatalog.IsInstalled(name))
                {
                    return AppConfig.SaveThemeSetting(null) ? Report("theme: inherit from Ghostty config") : 1;
                }

                if (!ThemeCatalog.IsInstalled(name))
                {
                    ThemeCatalog.CatalogResult catalog = await ThemeCatalog.LoadAsync();
                    ThemeEntry? entry = catalog.Themes.FirstOrDefault(t => t.Name == name)
                        ?? catalog.Themes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (entry is null)
                    {
                        Console.Error.WriteLine($"unknown theme: {name} (see --list-themes)");
                        return 1;
                    }

                    name = entry.Name;
                    if (!entry.Installed)
                    {
                        await ThemeCatalog.DownloadAsync(entry);
                    }
                }

                return AppConfig.SaveThemeSetting(name) ? Report($"theme: {name}") : 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"wraithcairn: {ex.Message}");
            return 1;
        }

        static int Report(string message)
        {
            Console.WriteLine($"{message} (saved to {AppConfig.AppConfigPath})");
            return 0;
        }
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
