using System.Diagnostics;

namespace Wraithcairn;

/// <summary>
/// The environment a login shell would have, for panes that run a program
/// directly (exec mode, no shell in between).
///
/// A macOS app started from Finder, the Dock or Spotlight inherits launchd's
/// bare environment (PATH=/usr/bin:/bin:/usr/sbin:/sbin, no LANG), so
/// Homebrew tools and anything the user adds in ~/.zprofile or ~/.zshrc are
/// missing. Shell panes fix this themselves by starting as login shells
/// (<see cref="ShellIntegration"/>); exec panes get the variables captured
/// here by running the user's shell once as `$SHELL -l -i -c env`, the same
/// trick VS Code and other GUI editors use.
///
/// On by default on macOS. WRAITHCAIRN_LOGIN=1 turns it (and login shells)
/// on elsewhere, WRAITHCAIRN_LOGIN=0 turns it off. While capturing,
/// WRAITHCAIRN_RESOLVING_ENV=1 is set so slow rc files can skip work.
/// </summary>
internal static class LoginEnvironment
{
    private const string Marker = "__WRAITHCAIRN_ENV_7f3a__";
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Variables always taken from the login shell (they usually grow there).</summary>
    private static readonly HashSet<string> Overridable = ["PATH", "MANPATH", "INFOPATH"];

    /// <summary>Per-shell or per-terminal state that must not leak into other processes.</summary>
    private static readonly HashSet<string> Excluded =
    [
        "PWD", "OLDPWD", "SHLVL", "_", "TERM", "TERM_PROGRAM", "TERM_PROGRAM_VERSION",
        "TERM_SESSION_ID", "COLORTERM", "COLUMNS", "LINES", "ZDOTDIR", "PS1", "PS2", "PROMPT",
        "RPROMPT", "WRAITHCAIRN_RESOLVING_ENV", "WRAITHCAIRN_USER_ZDOTDIR",
    ];

    private static readonly string[] ExcludedPrefixes =
        ["STARSHIP_", "P9K_", "_P9K", "POWERLEVEL9K_", "__ROYALTERMINAL", "__wraithcairn", "BASH_FUNC_"];

    private static readonly Lock Gate = new();
    private static Task<IReadOnlyDictionary<string, string>>? s_capture;

    /// <summary>Login shells and login-env capture: macOS by default, WRAITHCAIRN_LOGIN overrides.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("WRAITHCAIRN_LOGIN") switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => OperatingSystem.IsMacOS(),
    };

    /// <summary>Starts capturing in the background (once). Cheap to call repeatedly.</summary>
    public static void Prefetch(string shell)
    {
        if (!Enabled || OperatingSystem.IsWindows())
        {
            return;
        }

        lock (Gate)
        {
            s_capture ??= Task.Run(() => Capture(shell));
        }
    }

    /// <summary>
    /// The variables to add to an exec pane's environment: empty when
    /// disabled, or when the shell could not be run in time.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> GetAsync(string shell)
    {
        Prefetch(shell);
        Task<IReadOnlyDictionary<string, string>>? capture;
        lock (Gate)
        {
            capture = s_capture;
        }

        if (capture is null)
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return await capture.WaitAsync(CaptureTimeout + TimeSpan.FromSeconds(1));
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Finds <paramref name="command"/> on <paramref name="path"/> like execvp
    /// would. Returns the command unchanged when it has a slash or isn't found.
    /// </summary>
    public static string ResolveCommand(string command, string? path)
    {
        if (command.Contains('/') || string.IsNullOrEmpty(path))
        {
            return command;
        }

        foreach (string dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, command);
            try
            {
                if (File.Exists(candidate)
                    && (OperatingSystem.IsWindows()
                        || (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute
                            | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
                {
                    return candidate;
                }
            }
            catch
            {
                // unreadable PATH entry, keep looking
            }
        }

        return command;
    }

    /// <summary>A UTF-8 LANG like Terminal.app sets, for when nothing set one (Finder launches).</summary>
    public static string? DefaultLang()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANG"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LC_ALL"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LC_CTYPE")))
        {
            return null;
        }

        string name = System.Globalization.CultureInfo.CurrentCulture.Name;
        return name.Length >= 5 && name[2] == '-'
            ? $"{name[..2]}_{name[3..5]}.UTF-8"
            : "en_US.UTF-8";
    }

    private static IReadOnlyDictionary<string, string> Capture(string shell)
    {
        Dictionary<string, string> result = [];
        try
        {
            string name = Path.GetFileNameWithoutExtension(shell);
            if (name is "pwsh" or "powershell" or "nu" or "elvish" or "xonsh")
            {
                return result; // not POSIX-ish enough for the `-l -i -c` probe
            }

            ProcessStartInfo info = new(shell)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            foreach (string arg in new[] { "-l", "-i", "-c", $"printf '%s' {Marker}; /usr/bin/env; printf '%s' {Marker}" })
            {
                info.ArgumentList.Add(arg);
            }

            info.Environment["WRAITHCAIRN_RESOLVING_ENV"] = "1";

            using Process process = Process.Start(info)!;
            process.StandardInput.Close();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(CaptureTimeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // already gone
                }

                Console.Error.WriteLine($"wraithcairn: login environment from {shell} timed out");
                return result;
            }

            foreach ((string key, string value) in Parse(stdout.GetAwaiter().GetResult()))
            {
                if (Wanted(key, value))
                {
                    result[key] = value;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"wraithcairn: login environment unavailable: {ex.Message}");
        }

        return result;
    }

    /// <summary>Parses `env` output between the markers. Multi-line values continue the previous entry.</summary>
    internal static Dictionary<string, string> Parse(string output)
    {
        Dictionary<string, string> vars = [];
        int start = output.IndexOf(Marker, StringComparison.Ordinal);
        int end = output.LastIndexOf(Marker, StringComparison.Ordinal);
        if (start < 0 || end <= start)
        {
            return vars;
        }

        string body = output[(start + Marker.Length)..end];
        string? current = null;
        foreach (string line in body.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0 && IsName(line.AsSpan(0, eq)))
            {
                current = line[..eq];
                vars[current] = line[(eq + 1)..];
            }
            else if (current is not null && line.Length > 0)
            {
                vars[current] += "\n" + line;
            }
        }

        return vars;
    }

    private static bool Wanted(string key, string value)
    {
        if (Excluded.Contains(key) || ExcludedPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
        {
            return false;
        }

        string? existing = Environment.GetEnvironmentVariable(key);
        return Overridable.Contains(key) ? existing != value : existing is null;
    }

    private static bool IsName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || char.IsDigit(name[0]))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
