using RoyalTerminal.Terminal;

namespace Wraithcairn;

/// <summary>
/// Detects the user's shell and prepares a launch spec that loads the
/// RoyalTerminal shell-integration snippet (OSC 7 cwd + OSC 133 prompts)
/// without touching the user's dotfiles.
///
/// Mechanisms per shell:
///   bash  - bash --rcfile {generated file sourcing ~/.bashrc + snippet}
///   zsh   - ZDOTDIR={generated dir whose .zshrc sources user rc + snippet}
///   fish  - fish --init-command 'source {snippet.fish}'
///   pwsh  - pwsh -NoExit -Command '. {snippet.ps1}' (profile still loads)
///   cmd/other - launched plainly, no integration (cwd restore falls back)
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

            string shell = preferredShell
                ?? System.Environment.GetEnvironmentVariable("SHELL")
                ?? "/bin/bash";
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

    private static LaunchSpec ResolveBash(string shell)
    {
        string? script = BuildSnippet(TerminalShellIntegrationBootstrapShell.Bash);
        if (script is null)
        {
            return new LaunchSpec(null, null, "bash", false);
        }

        string path = WriteFile("bashrc",
            "[ -f /etc/bash.bashrc ] && source /etc/bash.bashrc\n" +
            "[ -f \"$HOME/.bashrc\" ] && source \"$HOME/.bashrc\"\n\n" +
            script + "\n" + BashHistorySync);
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

        // ZDOTDIR redirection: our .zshrc first restores the user's zsh setup,
        // then applies the snippet. User's ZDOTDIR (if any) is honored.
        string zdotdir = Path.Combine(ConfigDir, "zdotdir");
        Directory.CreateDirectory(zdotdir);
        string userZdotdir = System.Environment.GetEnvironmentVariable("ZDOTDIR")
            ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        File.WriteAllText(Path.Combine(zdotdir, ".zshrc"),
            $"export ZDOTDIR={Quote(userZdotdir)}\n" +
            "[ -f \"$ZDOTDIR/.zshrc\" ] && source \"$ZDOTDIR/.zshrc\"\n\n" +
            script + "\n");
        return new LaunchSpec(
            new TerminalCommandSpec(shell, []),
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
            new TerminalCommandSpec(shell, ["--init-command", $"source {Quote(path)}"]),
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
