using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

namespace Wraithcairn;

internal static partial class Program
{
    private static readonly string[] CliFlags =
    [
        "--version", "-v", "update", "--update", "--list-themes", "--download-themes", "--set-theme",
        "--install-desktop", "--uninstall-desktop", "voice",
    ];

    [STAThread]
    public static void Main(string[] args)
    {
        // A WinExe has no console; borrow the parent shell's for CLI commands
        // so `wraithcairn update` etc. print in cmd / PowerShell.
        if (OperatingSystem.IsWindows() && args.Any(CliFlags.Contains))
        {
            AttachParentConsole();
        }

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

        // `wraithcairn update [--check] [--yes]`: no Avalonia, no display.
        if (args.Length > 0 && args[0] is "update" or "--update")
        {
            Environment.ExitCode = UpdateCommandAsync(args[1..]).GetAwaiter().GetResult();
            return;
        }

        Updater.CleanupAfterUpdate();

        // `wraithcairn voice ...`: model download, devices, transcription. No display.
        if (args.Length > 0 && args[0] == "voice")
        {
            Environment.ExitCode = VoiceCommand.RunAsync(args[1..]).GetAwaiter().GetResult();
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

    /// <summary>
    /// wraithcairn update           check, then download + install if newer
    /// wraithcairn update --check   only report (exit 0 = up to date, 10 = update available)
    /// wraithcairn update --force   reinstall even if already up to date
    /// </summary>
    private static async Task<int> UpdateCommandAsync(string[] args)
    {
        if (args.Contains("-h") || args.Contains("--help"))
        {
            Console.WriteLine("""
                usage: wraithcairn update [--check] [--force]

                  (no options)  download and install the latest release if it is newer
                  --check       only check; exit code 0 = up to date, 10 = update available
                  --force       reinstall the latest release even if already up to date
                """);
            return 0;
        }

        bool checkOnly = args.Contains("--check");
        bool force = args.Contains("--force");
        string? unknown = args.FirstOrDefault(a => a is not ("--check" or "--force"));
        if (unknown is not null)
        {
            Console.Error.WriteLine($"wraithcairn update: unknown option {unknown} (try --help)");
            return 2;
        }

        Console.WriteLine($"Current version: {AppInfo.Version} ({Updater.Platform().Rid})");
        Console.WriteLine("Checking for the latest release\u2026");
        Updater.UpdateCheck check = await Updater.CheckAsync();
        if (check.Latest is not { } latest)
        {
            Console.Error.WriteLine($"wraithcairn update: {check.Error}");
            return 1;
        }

        Console.WriteLine($"Latest version:  {latest.Version}");
        if (!check.IsNewer && !force)
        {
            Console.WriteLine("wraithcairn is up to date.");
            return 0;
        }

        if (check.IsDevBuild)
        {
            Console.WriteLine("This is a development build (0.0.0-dev).");
        }
        else if (check.IsNewer)
        {
            Console.WriteLine($"Update available: {AppInfo.Version} -> {latest.Version}");
        }

        Console.WriteLine($"Release notes:   {Updater.ReleaseNotesUrl(latest.Version)}");
        if (checkOnly)
        {
            Console.WriteLine("Run `wraithcairn update` to install it.");
            return 10;
        }

        if (Updater.CannotSelfUpdateReason() is { } reason)
        {
            Console.Error.WriteLine($"wraithcairn update: can't update in place: {reason}");
            Console.Error.WriteLine(OperatingSystem.IsWindows()
                ? "  irm https://ryandanthony.github.io/wraithcairn/install.ps1 | iex"
                : "  curl -fsSL https://ryandanthony.github.io/wraithcairn/install | bash");
            return 1;
        }

        try
        {
            Progress<string> progress = new(Console.WriteLine);
            string installed = await Updater.InstallAsync(latest, progress);
            await Task.Delay(50); // let queued progress lines print first
            Console.WriteLine($"Updated to {latest.Version}: {installed}");
            Console.WriteLine("Restart wraithcairn to use the new version (running windows keep the old one).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"wraithcairn update: {ex.Message}");
            return 1;
        }
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

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    private static void AttachParentConsole()
    {
        try
        {
            if (AttachConsole(-1)) // ATTACH_PARENT_PROCESS
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            }
        }
        catch
        {
            // no parent console (started from Explorer); output is dropped
        }
    }

    /// <summary>
    /// WRAITHCAIRN_WAYLAND=1 opts into Avalonia's native Wayland backend
    /// (experimental in 12.1, so UsePlatformDetect never picks it). Without it,
    /// Linux runs on X11/XWayland as before.
    /// </summary>
    internal static bool WaylandRequested =>
        Environment.GetEnvironmentVariable("WRAITHCAIRN_WAYLAND") == "1";

    public static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App>().UsePlatformDetect();
        if (WaylandRequested)
        {
            // Falls back to the X11 backend picked above when no usable
            // compositor is reachable (X11 session, ssh -X, etc.).
            builder = builder.UseWaylandWithFallback();
        }

        return builder
            // xdg_toplevel app_id: Plasma/GNOME map it to wraithcairn.desktop,
            // the Wayland equivalent of LinuxWindowIdentity's X11 hints.
            .With(new WaylandPlatformOptions { AppId = DesktopIntegration.AppId })
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
