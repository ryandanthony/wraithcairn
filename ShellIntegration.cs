using RoyalTerminal.Terminal;

namespace Wraithcairn;

/// <summary>
/// Detects the user's shell and prepares a launch spec that loads the
/// RoyalTerminal shell-integration snippet (OSC 7 cwd + OSC 133 prompts)
/// without touching the user's dotfiles.
///
/// Mechanisms per shell:
///   bash  - bash --rcfile {generated file sourcing ~/.bashrc + snippet}
///   zsh   - ZDOTDIR={generated dir whose .zshenv/.zprofile/.zshrc/.zlogin
///           each source the user's own file, then .zshrc adds the snippet}
///   fish  - fish --init-command 'source {snippet.fish}'
///   pwsh  - pwsh -NoExit -Command '. {snippet.ps1}' (profile still loads)
///   cmd/other - launched plainly, no integration (cwd restore falls back)
///
/// On macOS (or with WRAITHCAIRN_LOGIN=1) shells start as login shells, as
/// Terminal.app, iTerm2 and Ghostty do there. An app launched from Finder or
/// the Dock inherits launchd's bare PATH, and macOS puts PATH setup in login
/// files (/etc/zprofile runs path_helper, Homebrew's `brew shellenv` lives
/// in ~/.zprofile), so a non-login shell can't find starship, jcode, etc.
/// </summary>
public static class ShellIntegration
{
    public sealed record LaunchSpec(
        TerminalCommandSpec? Command,
        IReadOnlyDictionary<string, string>? Environment,
        string ShellName,
        bool Integrated);

    private static readonly string ConfigDir = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "wraithcairn");

    public static LaunchSpec Resolve(string? preferredShell = null)
    {
        try
        {
            if (OperatingSystem.IsWindows() && preferredShell is null)
            {
                return ResolveWindows();
            }

            string shell = UserShell(preferredShell);
            string name = Path.GetFileNameWithoutExtension(shell);

            return name switch
            {
                "bash" => ResolveBash(shell),
                "zsh" => ResolveZsh(shell),
                "fish" => ResolveFish(shell),
                "pwsh" => ResolvePwsh(shell),
                _ => new LaunchSpec(null, null, name, Integrated: false),
            };
        }
        catch
        {
            return new LaunchSpec(null, null, "default", Integrated: false);
        }
    }

    /// <summary>Configured shell, else $SHELL, else the platform's default shell.</summary>
    public static string UserShell(string? preferredShell = null) =>
        preferredShell is { Length: > 0 } ? preferredShell
        : System.Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } env ? env
        : OperatingSystem.IsMacOS() ? "/bin/zsh"
        : "/bin/bash";

    private static LaunchSpec ResolveBash(string shell)
    {
        string? script = BuildSnippet(TerminalShellIntegrationBootstrapShell.Bash);
        if (script is null)
        {
            return new LaunchSpec(null, null, "bash", false);
        }

        // bash ignores --rcfile in a login shell, so on macOS the generated
        // file runs bash's login chain itself instead of passing --login:
        // /etc/profile (path_helper), then the first of ~/.bash_profile,
        // ~/.bash_login, ~/.profile, exactly as a login bash would.
        string path = WriteFile("bashrc",
            (LoginEnvironment.Enabled
                ? "[ -f /etc/profile ] && source /etc/profile\n" +
                  "if [ -f \"$HOME/.bash_profile\" ]; then source \"$HOME/.bash_profile\"\n" +
                  "elif [ -f \"$HOME/.bash_login\" ]; then source \"$HOME/.bash_login\"\n" +
                  "elif [ -f \"$HOME/.profile\" ]; then source \"$HOME/.profile\"; fi\n"
                : "[ -f /etc/bash.bashrc ] && source /etc/bash.bashrc\n" +
                  "[ -f \"$HOME/.bashrc\" ] && source \"$HOME/.bashrc\"\n") +
            "\n" + script + "\n" + BashHistorySync);
        return new LaunchSpec(
            new TerminalCommandSpec(shell, ["--rcfile", path]),
            null,
            "bash",
            Integrated: true);
    }

    /// <summary>
    /// The upstream bash snippet detects a new command (OSC 133 C) when the
    /// DEBUG trap sees a history entry it has not reported yet. Other
    /// PROMPT_COMMAND hooks (starship, etc.) also trip the DEBUG trap, so at a
    /// new shell's first prompt it reports the previous session's last
    /// history line as "running", which also swallows the next real
    /// command's 133 C and reports that one after it finished. Marking the
    /// current history entry as seen at every prompt makes only commands
    /// entered at this prompt count.
    /// </summary>
    private const string BashHistorySync = """
        __wraithcairn_sync_history() {
          local line
          line="$(HISTTIMEFORMAT= history 1 2>/dev/null)" || line=""
          line="${line#"${line%%[![:space:]]*}"}"
          __ROYALTERMINAL_LAST_HISTORY_ID="${line%%[[:space:]]*}"
        }
        if declare -F __royalterminal_prompt_command >/dev/null; then
          eval "__wraithcairn_upstream_prompt_command() $(declare -f __royalterminal_prompt_command | tail -n +2)"
          __royalterminal_prompt_command() {
            local rc=$?
            __wraithcairn_sync_history
            (exit "$rc")
            __wraithcairn_upstream_prompt_command
          }
        fi

        """;

    private static LaunchSpec ResolveZsh(string shell)
    {
        string? script = BuildSnippet(TerminalShellIntegrationBootstrapShell.Zsh);
        if (script is null)
        {
            return new LaunchSpec(null, null, "zsh", false);
        }

        // ZDOTDIR redirection. zsh reads .zshenv, .zprofile (login), .zshrc
        // (interactive) and .zlogin (login) from $ZDOTDIR, so the generated
        // dir has a stand-in for every one of them that sources the user's
        // real file with ZDOTDIR pointing back at the user's dir (their
        // files may use $ZDOTDIR, or set it in ~/.zshenv). Only .zshenv
        // used to be proxied as .zshrc, so ~/.zshenv and ~/.zprofile (where
        // Homebrew's PATH goes on macOS) never ran. The real ZDOTDIR is
        // restored for good in .zlogin / .zshrc, whichever runs last.
        string zdotdir = Path.Combine(ConfigDir, "zdotdir");
        Directory.CreateDirectory(zdotdir);
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        string userZdotdir = System.Environment.GetEnvironmentVariable("ZDOTDIR") is { Length: > 0 } z
            && !string.Equals(Path.GetFullPath(z).TrimEnd('/'), Path.GetFullPath(zdotdir).TrimEnd('/'), StringComparison.Ordinal)
                ? z
                : home;
        string ours = Quote(zdotdir);

        // Source the user's file (if any) with their ZDOTDIR, then come back.
        // A user .zshenv that sets ZDOTDIR moves where their other files are.
        static string Proxy(string file, string ours) =>
            "ZDOTDIR=\"${WRAITHCAIRN_USER_ZDOTDIR:-$HOME}\"\n" +
            $"[ -f \"$ZDOTDIR/{file}\" ] && source \"$ZDOTDIR/{file}\"\n" +
            "WRAITHCAIRN_USER_ZDOTDIR=\"$ZDOTDIR\"\n" +
            $"ZDOTDIR={ours}\n";

        // The last file zsh reads hands ZDOTDIR back to the user for good:
        // .zlogin for login shells, .zshrc otherwise.
        const string Restore =
            "export ZDOTDIR=\"$WRAITHCAIRN_USER_ZDOTDIR\"\n" +
            "[ \"$ZDOTDIR\" = \"$HOME\" ] && unset ZDOTDIR\n" +
            "unset WRAITHCAIRN_USER_ZDOTDIR\n";

        File.WriteAllText(Path.Combine(zdotdir, ".zshenv"),
            $"WRAITHCAIRN_USER_ZDOTDIR={Quote(userZdotdir)}\n" + Proxy(".zshenv", ours) +
            // `zsh -c ...` (no rc files after this): give ZDOTDIR back now.
            "[[ -o interactive ]] || { " + Restore.Replace("\n", "; ") + "}\n");
        File.WriteAllText(Path.Combine(zdotdir, ".zprofile"), Proxy(".zprofile", ours));
        File.WriteAllText(Path.Combine(zdotdir, ".zshrc"),
            Proxy(".zshrc", ours) + "\n" + script + "\n\n" +
            "[[ -o login ]] || {\n" + Restore + "}\n");
        File.WriteAllText(Path.Combine(zdotdir, ".zlogin"),
            Proxy(".zlogin", ours) + Restore);
        return new LaunchSpec(
            new TerminalCommandSpec(shell, LoginEnvironment.Enabled ? ["-l"] : []),
            new Dictionary<string, string> { ["ZDOTDIR"] = zdotdir },
            "zsh",
            Integrated: true);
    }

    private static LaunchSpec ResolveFish(string shell)
    {
        string? script = BuildSnippet(TerminalShellIntegrationBootstrapShell.Fish);
        if (script is null)
        {
            return new LaunchSpec(null, null, "fish", false);
        }

        string path = WriteFile("integration.fish", script + "\n");
        return new LaunchSpec(
            new TerminalCommandSpec(shell, LoginEnvironment.Enabled
                ? ["--login", "--init-command", $"source {Quote(path)}"]
                : ["--init-command", $"source {Quote(path)}"]),
            null,
            "fish",
            Integrated: true);
    }

    private static LaunchSpec ResolvePwsh(string shell)
    {
        string? script = BuildSnippet(TerminalShellIntegrationBootstrapShell.PowerShell);
        if (script is null)
        {
            return new LaunchSpec(null, null, "pwsh", false);
        }

        string path = WriteFile("integration.ps1", script + "\n");
        return new LaunchSpec(
            new TerminalCommandSpec(shell, ["-NoExit", "-Command", $". '{path}'"]),
            null,
            "pwsh",
            Integrated: true);
    }

    private static LaunchSpec ResolveWindows()
    {
        // Prefer pwsh, then Windows PowerShell, then cmd (no integration).
        string? pwsh = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe");
        if (pwsh is not null)
        {
            string? script = BuildSnippet(TerminalShellIntegrationBootstrapShell.PowerShell);
            if (script is not null)
            {
                string path = WriteFile("integration.ps1", script + "\r\n");
                return new LaunchSpec(
                    new TerminalCommandSpec(pwsh, ["-NoExit", "-Command", $". '{path}'"]),
                    null,
                    Path.GetFileNameWithoutExtension(pwsh),
                    Integrated: true);
            }

            return new LaunchSpec(new TerminalCommandSpec(pwsh, []), null,
                Path.GetFileNameWithoutExtension(pwsh), false);
        }

        return new LaunchSpec(null, null, "cmd", false);
    }

    private static string? BuildSnippet(TerminalShellIntegrationBootstrapShell shell) =>
        TerminalShellIntegrationBootstrapBuilder.Build(
            new TerminalShellIntegrationBootstrapOptions(shell));

    private static string WriteFile(string name, string content)
    {
        Directory.CreateDirectory(ConfigDir);
        string path = Path.Combine(ConfigDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string? FindOnPath(string exe)
    {
        string? pathEnv = System.Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null)
        {
            return null;
        }

        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Quote(string value) => $"'{value.Replace("'", "'\\''")}'";
}
