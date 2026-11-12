using System.Diagnostics;

namespace Wraithcairn;

/// <summary>
/// Linux desktop integration for the single-file build. A window is shown in
/// the KDE/GNOME task manager with its name and icon only when a .desktop
/// file matches the window's class (WM_CLASS "wraithcairn"). This writes
///   ~/.local/share/applications/wraithcairn.desktop
///   ~/.local/share/icons/hicolor/NxN/apps/wraithcairn.png
/// pointing Exec at the running binary, so the app also shows up in the
/// launcher and can be pinned. Nothing outside the user's home is touched.
/// </summary>
public static class DesktopIntegration
{
    public const string AppId = "wraithcairn";

    private static readonly int[] IconSizes = [16, 32, 48, 64, 128, 256, 512];

    private static string DataHome =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dataHome
            ? dataHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    public static string DesktopFilePath => Path.Combine(DataHome, "applications", $"{AppId}.desktop");

    /// <summary>The binary to launch: the single-file exe, or the apphost of a folder build.</summary>
    public static string? ExecutablePath =>
        Environment.ProcessPath is { } path && !Path.GetFileName(path).StartsWith("dotnet", StringComparison.Ordinal)
            ? path
            : null;

    public static bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD();

    /// <summary>True when a desktop entry exists and launches this same binary.</summary>
    public static bool IsInstalledForThisBinary()
    {
        if (!File.Exists(DesktopFilePath) || ExecutablePath is not { } exe)
        {
            return false;
        }

        return File.ReadLines(DesktopFilePath).Any(line => line.StartsWith("Exec=", StringComparison.Ordinal)
            && line.Contains(Quote(exe), StringComparison.Ordinal));
    }

    /// <summary>Marks entries we wrote, so we only ever overwrite our own file.</summary>
    private const string GeneratedMarker = "X-Wraithcairn-Generated=true";

    /// <summary>The binary the existing entry launches (Exec= without quotes/args).</summary>
    private static string? CurrentTarget()
    {
        try
        {
            string? exec = File.ReadLines(DesktopFilePath)
                .FirstOrDefault(line => line.StartsWith("Exec=", StringComparison.Ordinal))?["Exec=".Length..].Trim();
            if (string.IsNullOrEmpty(exec))
            {
                return null;
            }

            return exec.StartsWith('"') && exec.IndexOf('"', 1) is var end and > 0
                ? exec[1..end]
                : exec.Split(' ')[0];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// On launch: create the entry if missing, or repoint it if it is ours and
    /// the binary it launches is gone (moved/reinstalled elsewhere). A copy
    /// run from somewhere else (an unpacked tarball, a test build) leaves a
    /// working entry alone. A hand-written or packaged entry is never touched.
    /// WRAITHCAIRN_NO_DESKTOP=1 skips this entirely.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (!IsSupported || ExecutablePath is null || IsInstalledForThisBinary()
            || Environment.GetEnvironmentVariable("WRAITHCAIRN_NO_DESKTOP") == "1")
        {
            return;
        }

        if (File.Exists(DesktopFilePath) && CurrentTarget() is { } target && File.Exists(target))
        {
            return; // points at another, still-present wraithcairn
        }

        bool ours = !File.Exists(DesktopFilePath)
            || File.ReadLines(DesktopFilePath).Any(line => line.Trim() == GeneratedMarker);
        if (!ours)
        {
            return;
        }

        try
        {
            Console.WriteLine($"wraithcairn: {Install()}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"wraithcairn: desktop entry not installed: {ex.Message}");
        }
    }

    /// <summary>Writes the desktop entry and icons. Returns a human-readable summary.</summary>
    public static string Install()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("Desktop integration is only needed on Linux.");
        }

        string exe = ExecutablePath
            ?? throw new InvalidOperationException("Can't tell which binary to launch (running under `dotnet`?).");

        int icons = 0;
        foreach (int size in IconSizes)
        {
            try
            {
                // Manifest resource, not avares://: works without a display.
                using Stream? source = typeof(DesktopIntegration).Assembly.GetManifestResourceStream($"icons/icon-{size}.png");
                if (source is null)
                {
                    continue;
                }

                string dir = Path.Combine(DataHome, "icons", "hicolor", $"{size}x{size}", "apps");
                Directory.CreateDirectory(dir);
                using FileStream target = File.Create(Path.Combine(dir, $"{AppId}.png"));
                source.CopyTo(target);
                icons++;
            }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
            {
                // size not bundled; the others still work
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DesktopFilePath)!);
        File.WriteAllText(DesktopFilePath, $"""
            [Desktop Entry]
            Type=Application
            Version=1.5
            Name=wraithcairn
            GenericName=Terminal
            Comment=Split-pane terminal with named layouts (libghostty-vt)
            Exec={Quote(exe)}
            Icon={AppId}
            Terminal=false
            Categories=System;TerminalEmulator;
            Keywords=terminal;shell;prompt;command;commandline;ghostty;
            StartupNotify=true
            StartupWMClass={AppId}
            Actions=new-window;
            {GeneratedMarker}

            [Desktop Action new-window]
            Name=New Window
            Exec={Quote(exe)}

            """);

        RefreshCaches();
        return $"installed {DesktopFilePath} ({icons} icon sizes) -> {exe}";
    }

    /// <summary>Removes what <see cref="Install"/> wrote.</summary>
    public static string Uninstall()
    {
        int removed = 0;
        if (File.Exists(DesktopFilePath))
        {
            File.Delete(DesktopFilePath);
            removed++;
        }

        foreach (int size in IconSizes)
        {
            string icon = Path.Combine(DataHome, "icons", "hicolor", $"{size}x{size}", "apps", $"{AppId}.png");
            if (File.Exists(icon))
            {
                File.Delete(icon);
                removed++;
            }
        }

        RefreshCaches();
        return $"removed {removed} file(s)";
    }

    /// <summary>Desktop-entry Exec quoting: wrap in quotes, escape \ " ` $.</summary>
    private static string Quote(string path)
    {
        string escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");
        return $"\"{escaped}\"";
    }

    private static void RefreshCaches()
    {
        Run("update-desktop-database", Path.Combine(DataHome, "applications"));
        Run("gtk-update-icon-cache", "-f", "-t", Path.Combine(DataHome, "icons", "hicolor"));
        Run("kbuildsycoca6");
        Run("kbuildsycoca5");
    }

    private static void Run(string tool, params string[] args)
    {
        try
        {
            ProcessStartInfo start = new(tool)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using Process? process = Process.Start(start);
            process?.WaitForExit(10_000);
        }
        catch
        {
            // tool not installed on this desktop; fine
        }
    }
}
