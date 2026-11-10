using System.Text;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Terminal;

namespace SpikeRoyalTerminal;

/// <summary>
/// Optional scrollback persistence. On save, each pane's visible screen plus
/// scrollback is exported as styled VT (colors kept) to
/// ~/.config/spike-royalterminal/scrollback/{paneId}.vt. On restore the bytes
/// are written into the fresh terminal before the shell starts, with
/// PreserveScrollbackOnSessionStart so the new prompt appears below the old
/// output instead of wiping it.
///
/// Off by default (config.json: "restoreScrollback": true). Files are capped
/// per pane; stale files for panes that no longer exist are pruned on save.
/// </summary>
public static class ScrollbackStore
{
    /// <summary>Per-pane cap. Trims from the front (oldest output) when larger.</summary>
    public const int MaxBytesPerPane = 512 * 1024;

    private const string Separator =
        "\r\n\x1b[0m\x1b[2m--- restored scrollback ---\x1b[0m\r\n";

    public static string Directory { get; } = NamedLayouts.ActiveName is { } layout
        ? Path.Combine(AppConfig.ConfigDir, "scrollback", NamedLayouts.Sanitize(layout))
        : Path.Combine(AppConfig.ConfigDir, "scrollback");

    private static string PathFor(string paneId) =>
        Path.Combine(Directory, SanitizeId(paneId) + ".vt");

    /// <summary>Saves every pane and removes files for panes no longer present.</summary>
    public static void SaveAll(IEnumerable<(string PaneId, TerminalControl Control)> panes)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            HashSet<string> kept = new(StringComparer.Ordinal);

            foreach ((string paneId, TerminalControl control) in panes)
            {
                string path = PathFor(paneId);
                kept.Add(Path.GetFileName(path));
                if (!TryExport(control, out string vt))
                {
                    continue;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(vt);
                if (bytes.Length > MaxBytesPerPane)
                {
                    bytes = TrimToLineBoundary(bytes, MaxBytesPerPane);
                }

                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, overwrite: true);
            }

            foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.vt"))
            {
                if (!kept.Contains(Path.GetFileName(file)))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"scrollback save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Replays saved output into a not-yet-started terminal. Returns true if
    /// anything was restored (caller should then preserve scrollback on start).
    /// </summary>
    public static bool TryRestore(string paneId, TerminalControl control)
    {
        try
        {
            string path = PathFor(paneId);
            if (!File.Exists(path))
            {
                return false;
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                return false;
            }

            control.WriteOutput(bytes);
            control.WriteOutput(Encoding.UTF8.GetBytes(Separator));
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"scrollback restore failed for {paneId}: {ex.Message}");
            return false;
        }
    }

    private static bool TryExport(TerminalControl control, out string vt)
    {
        vt = string.Empty;
        try
        {
            return control.TryExportSnapshot(
                       TerminalSnapshotExportFormat.StyledVt,
                       new TerminalSnapshotExportOptions(TrimTrailingWhitespace: true),
                       out vt)
                   && vt.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Keeps the newest bytes, starting at a line break so no escape is cut.</summary>
    private static byte[] TrimToLineBoundary(byte[] bytes, int max)
    {
        int start = bytes.Length - max;
        int newline = Array.IndexOf(bytes, (byte)'\n', start);
        start = newline >= 0 ? newline + 1 : start;
        byte[] trimmed = new byte[bytes.Length - start + 4];
        // Reset attributes first: the trimmed head may have dropped an SGR.
        "\x1b[0m"u8.CopyTo(trimmed);
        Array.Copy(bytes, start, trimmed, 4, bytes.Length - start);
        return trimmed;
    }

    private static string SanitizeId(string id)
    {
        StringBuilder sb = new(id.Length);
        foreach (char c in id)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        return sb.ToString();
    }
}
