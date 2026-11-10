using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Theming;

namespace Wraithcairn;

/// <summary>
/// App settings. Resolution order (later wins):
///   1. built-in defaults
///   2. ~/.config/ghostty/config  (font-family, font-size, theme,
///      background/foreground/palette/cursor-color/selection-*, copy-on-select)
///   3. ~/.config/wraithcairn/config.json  (this app's own overrides)
///
/// Ghostty themes are looked up by name in the usual Ghostty theme dirs
/// (user, system, and an extracted AppImage) so `theme = GitHub Dark Dimmed`
/// in the Ghostty config just works.
/// </summary>
public sealed class AppConfig
{
    public string? FontFamily { get; set; }

    public double FontSize { get; set; } = 11;

    /// <summary>Ghostty theme name, e.g. "GitHub Dark Dimmed".</summary>
    public string? Theme { get; set; }

    public bool CopyOnSelect { get; set; }

    /// <summary>Restore the saved layout on launch (false = always start fresh).</summary>
    public bool RestoreLayout { get; set; } = true;

    /// <summary>Also save/replay each pane's scrollback (styled text, capped per pane).</summary>
    public bool RestoreScrollback { get; set; }

    /// <summary>Read ~/.config/ghostty/config as a base layer.</summary>
    public bool UseGhosttyConfig { get; set; } = true;

    /// <summary>Show a splash window while the saved layout loads.</summary>
    public bool ShowSplash { get; set; } = true;

    /// <summary>Optional explicit shell, e.g. "/usr/bin/fish". Null = $SHELL.</summary>
    public string? Shell { get; set; }

    [JsonIgnore]
    public TerminalTheme? ResolvedTheme { get; private set; }

    /// <summary>Where each setting came from, for the status bar / selftest.</summary>
    [JsonIgnore]
    public List<string> Sources { get; } = [];

    public static string ConfigDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "wraithcairn");

    public static string AppConfigPath => Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppConfig Load()
    {
        AppConfig config = new();
        Dictionary<string, string> themeKeys = new(StringComparer.Ordinal);

        // Peek at the app config first only for UseGhosttyConfig.
        AppConfig? appOverrides = TryReadAppConfig();

        if (appOverrides?.UseGhosttyConfig ?? true)
        {
            config.ApplyGhosttyConfig(themeKeys);
        }

        if (appOverrides is not null)
        {
            if (appOverrides.FontFamily is not null) config.FontFamily = appOverrides.FontFamily;
            if (appOverrides._fontSizeSet) config.FontSize = appOverrides.FontSize;
            if (appOverrides.Theme is not null)
            {
                config.Theme = appOverrides.Theme;
                themeKeys.Clear(); // app theme replaces any inline ghostty colors
            }

            if (appOverrides._copyOnSelectSet) config.CopyOnSelect = appOverrides.CopyOnSelect;
            config.RestoreLayout = appOverrides.RestoreLayout;
            config.RestoreScrollback = appOverrides.RestoreScrollback;
            config.UseGhosttyConfig = appOverrides.UseGhosttyConfig;
            config.ShowSplash = appOverrides.ShowSplash;
            if (appOverrides.Shell is not null) config.Shell = appOverrides.Shell;
            config.Sources.Add(AppConfigPath);
        }
        else
        {
            WriteDefaultAppConfig();
        }

        config.ResolvedTheme = config.BuildTheme(themeKeys);
        return config;
    }

    // Track whether value-type fields were present in JSON so defaults from
    // the Ghostty layer are not clobbered by implicit JSON defaults.
    private bool _fontSizeSet;
    private bool _copyOnSelectSet;

    private static AppConfig? TryReadAppConfig()
    {
        try
        {
            if (!File.Exists(AppConfigPath))
            {
                return null;
            }

            string json = File.ReadAllText(AppConfigPath);
            AppConfig? parsed = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (parsed is null)
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            parsed._fontSizeSet = doc.RootElement.TryGetProperty("fontSize", out _);
            parsed._copyOnSelectSet = doc.RootElement.TryGetProperty("copyOnSelect", out _);
            return parsed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"config.json ignored: {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes a commented starter config so the user can discover the knobs.</summary>
    private static void WriteDefaultAppConfig()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(AppConfigPath,
                """
                {
                  // Overrides for wraithcairn. Anything left out comes from
                  // ~/.config/ghostty/config (font-family, font-size, theme,
                  // copy-on-select), then built-in defaults.
                  //
                  // "fontFamily": "JetBrainsMono NFM",
                  // "fontSize": 11,
                  // "theme": "GitHub Dark Dimmed",
                  // "copyOnSelect": true,
                  // "shell": "/usr/bin/fish",
                  "restoreLayout": true,
                  // Also bring back each pane's previous output (styled, capped).
                  "restoreScrollback": false,
                  // Splash window while the saved layout loads.
                  "showSplash": true,
                  "useGhosttyConfig": true
                }

                """);
        }
        catch
        {
            // non-fatal
        }
    }

    // ----- Ghostty config layer ---------------------------------------------

    private static string GhosttyConfigPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "ghostty",
        "config");

    private void ApplyGhosttyConfig(Dictionary<string, string> themeKeys)
    {
        if (!File.Exists(GhosttyConfigPath))
        {
            return;
        }

        foreach ((string key, string value) in ParseGhosttyFile(GhosttyConfigPath))
        {
            switch (key)
            {
                case "font-family" when FontFamily is null:
                    FontFamily = value;
                    break;
                case "font-size" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double size):
                    FontSize = size;
                    break;
                case "theme":
                    Theme = value;
                    break;
                case "copy-on-select":
                    CopyOnSelect = value is "true" or "clipboard" or "both";
                    break;
                case "background" or "foreground" or "cursor-color" or "cursor-text"
                    or "selection-background" or "selection-foreground" or "palette":
                    // Inline colors override the theme; keep palette entries distinct.
                    themeKeys[key == "palette" ? $"palette:{value.Split('=')[0].Trim()}" : key] = value;
                    break;
            }
        }

        Sources.Add(GhosttyConfigPath);
    }

    private static IEnumerable<(string Key, string Value)> ParseGhosttyFile(string path)
    {
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim().Trim('"');
            yield return (key, value);
        }
    }

    // ----- theme ------------------------------------------------------------

    private TerminalTheme BuildTheme(Dictionary<string, string> inline)
    {
        Dictionary<string, string> keys = new(StringComparer.Ordinal);

        if (Theme is not null)
        {
            // Ghostty supports "light:A,dark:B"; take the dark variant.
            string name = Theme;
            if (name.Contains("dark:", StringComparison.Ordinal))
            {
                name = name[(name.IndexOf("dark:", StringComparison.Ordinal) + 5)..].Split(',')[0].Trim();
            }

            string? file = FindThemeFile(name);
            if (file is not null)
            {
                foreach ((string key, string value) in ParseGhosttyFile(file))
                {
                    keys[key == "palette" ? $"palette:{value.Split('=')[0].Trim()}" : key] = value;
                }

                Sources.Add($"theme:{name}");
            }
            else
            {
                Sources.Add($"theme:{name} (not found, using default)");
            }
        }

        foreach ((string key, string value) in inline)
        {
            keys[key] = value;
        }

        if (keys.Count == 0)
        {
            return TerminalTheme.Dark;
        }

        TerminalTheme baseline = TerminalTheme.Dark;
        uint[] base16 = new uint[16];
        for (int i = 0; i < 16; i++)
        {
            base16[i] = baseline.Palette[i];
            if (keys.TryGetValue($"palette:{i}", out string? entry))
            {
                string hex = entry[(entry.IndexOf('=') + 1)..].Trim();
                if (TryParseColor(hex, out uint color))
                {
                    base16[i] = color;
                }
            }
        }

        uint fg = Color(keys, "foreground") ?? baseline.DefaultForeground;
        uint bg = Color(keys, "background") ?? baseline.DefaultBackground;

        return TerminalTheme.FromBase16(
            base16,
            fg,
            bg,
            cursorColor: Color(keys, "cursor-color"),
            selectionForeground: Color(keys, "selection-foreground"),
            selectionBackground: Color(keys, "selection-background"),
            cursorTextColor: Color(keys, "cursor-text"));
    }

    private static uint? Color(Dictionary<string, string> keys, string key) =>
        keys.TryGetValue(key, out string? value) && TryParseColor(value, out uint color) ? color : null;

    /// <summary>Parses #rrggbb / rrggbb into 0xAARRGGBB with full alpha.</summary>
    private static bool TryParseColor(string value, out uint color)
    {
        color = 0;
        string hex = value.Trim().TrimStart('#');
        if (hex.Length != 6
            || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            return false;
        }

        color = 0xFF000000u | rgb;
        return true;
    }

    private static string? FindThemeFile(string name)
    {
        if (Path.IsPathRooted(name) && File.Exists(name))
        {
            return name;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] dirs =
        [
            Path.Combine(Path.GetDirectoryName(GhosttyConfigPath)!, "themes"),
            Path.Combine(ConfigDir, "themes"),
            "/usr/share/ghostty/themes",
            "/usr/local/share/ghostty/themes",
            Path.Combine(home, ".local", "share", "ghostty", "themes"),
            "/Applications/Ghostty.app/Contents/Resources/ghostty/themes",
        ];

        foreach (string dir in dirs)
        {
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
