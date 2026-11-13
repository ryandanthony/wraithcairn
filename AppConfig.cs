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

    /// <summary>Show a splash window while the saved layout loads (null/missing = on).</summary>
    public bool? ShowSplash { get; set; }

    /// <summary>Optional explicit shell, e.g. "/usr/bin/fish". Null = $SHELL.</summary>
    public string? Shell { get; set; }

    /// <summary>
    /// Where voice input keeps its downloaded speech model. Null = "models"
    /// next to config.json. Relative paths are relative to the config folder;
    /// "~" and environment variables are expanded.
    /// </summary>
    public string? VoiceModelsDir { get; set; }

    /// <summary>Microphone for voice input (name or part of one). Null = system default.</summary>
    public string? VoiceInputDevice { get; set; }

    /// <summary>Voice input turns itself off after this many seconds without speech (0 = never).</summary>
    public double VoiceIdleStopSeconds { get; set; } = 30;

    /// <summary>
    /// Type words into the pane as they are recognized, fixing them up as the
    /// recognizer revises the phrase (default). False = type each phrase only
    /// once it is finished.
    /// </summary>
    public bool VoiceLiveTyping { get; set; } = true;

    /// <summary>Resolved model folder (see <see cref="VoiceModelsDir"/>).</summary>
    [JsonIgnore]
    public string ResolvedVoiceModelsDir => VoiceModel.ResolveModelsDir(VoiceModelsDir);

    [JsonIgnore]
    public TerminalTheme? ResolvedTheme { get; private set; }

    /// <summary>Theme set by config.json (null = inherit the Ghostty layer's theme).</summary>
    [JsonIgnore]
    public string? AppTheme => _appTheme;

    // Ghostty-layer theme bits, kept so the theme can be swapped live (and
    // reset back to Ghostty's) without re-reading every setting.
    private string? _appTheme;
    private string? _ghosttyTheme;
    private Dictionary<string, string> _ghosttyInline = new(StringComparer.Ordinal);

    /// <summary>Where each setting came from, for the status bar / selftest.</summary>
    [JsonIgnore]
    public List<string> Sources { get; } = [];

    /// <summary>
    /// ~/.config/wraithcairn (Linux), ~/Library/Application Support/wraithcairn
    /// (macOS), %APPDATA%\wraithcairn (Windows). WRAITHCAIRN_CONFIG_DIR
    /// overrides it, e.g. for isolated test runs (macOS ignores $HOME and
    /// XDG_CONFIG_HOME for Application Support).
    /// </summary>
    public static string ConfigDir { get; } =
        Environment.GetEnvironmentVariable("WRAITHCAIRN_CONFIG_DIR") is { Length: > 0 } overridden
            ? Path.GetFullPath(overridden)
            : Path.Combine(
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

    /// <summary>Downloaded themes (theme picker, --download-themes) go here.</summary>
    public static string UserThemesDir => Path.Combine(ConfigDir, "themes");

    public static AppConfig Load() => Load(writeDefault: true);

    public static AppConfig Load(bool writeDefault)
    {
        AppConfig config = new();
        Dictionary<string, string> themeKeys = new(StringComparer.Ordinal);

        // Peek at the app config first only for UseGhosttyConfig.
        AppConfig? appOverrides = TryReadAppConfig();

        if (appOverrides?.UseGhosttyConfig ?? true)
        {
            config.ApplyGhosttyConfig(themeKeys);
        }

        config._ghosttyTheme = config.Theme;
        config._ghosttyInline = new Dictionary<string, string>(themeKeys, StringComparer.Ordinal);

        if (appOverrides is not null)
        {
            if (appOverrides.FontFamily is not null) config.FontFamily = appOverrides.FontFamily;
            if (appOverrides._fontSizeSet) config.FontSize = appOverrides.FontSize;
            if (appOverrides.Theme is not null)
            {
                config.Theme = appOverrides.Theme;
                config._appTheme = appOverrides.Theme;
                themeKeys.Clear(); // app theme replaces any inline ghostty colors
            }

            if (appOverrides._copyOnSelectSet) config.CopyOnSelect = appOverrides.CopyOnSelect;
            config.RestoreLayout = appOverrides.RestoreLayout;
            config.RestoreScrollback = appOverrides.RestoreScrollback;
            config.UseGhosttyConfig = appOverrides.UseGhosttyConfig;
            config.ShowSplash = appOverrides.ShowSplash;
            if (appOverrides.Shell is not null) config.Shell = appOverrides.Shell;
            config.VoiceModelsDir = appOverrides.VoiceModelsDir;
            config.VoiceInputDevice = appOverrides.VoiceInputDevice;
            config.VoiceIdleStopSeconds = appOverrides.VoiceIdleStopSeconds;
            config.VoiceLiveTyping = appOverrides.VoiceLiveTyping;
            config.Sources.Add(AppConfigPath);
        }
        else if (writeDefault)
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
                  "useGhosttyConfig": true,
                  // Voice input (Ctrl+Shift+I) runs offline. Its speech model
                  // (~68 MB) downloads on first use into "models" next to this
                  // file; set a folder here to keep it elsewhere.
                  // "voiceModelsDir": "~/.cache/wraithcairn-models",
                  // "voiceInputDevice": "USB",
                  "voiceIdleStopSeconds": 30,
                  // Words appear as you speak; false = each phrase once it is finished.
                  "voiceLiveTyping": true
                }

                """);
        }
        catch
        {
            // non-fatal
        }
    }

    // ----- live theme changes ---------------------------------------------

    /// <summary>
    /// Switches the theme in memory: a name overrides the Ghostty layer (like
    /// "theme" in config.json), null falls back to Ghostty's theme and inline
    /// colors. Updates <see cref="ResolvedTheme"/>; callers repaint.
    /// </summary>
    public void SetTheme(string? appTheme)
    {
        _appTheme = string.IsNullOrWhiteSpace(appTheme) ? null : appTheme.Trim();
        Dictionary<string, string> inline;
        if (_appTheme is not null)
        {
            Theme = _appTheme;
            inline = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        else
        {
            Theme = _ghosttyTheme;
            inline = new Dictionary<string, string>(_ghosttyInline, StringComparer.Ordinal);
        }

        Sources.RemoveAll(s => s.StartsWith("theme:", StringComparison.Ordinal));
        ResolvedTheme = BuildTheme(inline);
    }

    /// <summary>Takes the theme settings of a freshly loaded config (file watcher).</summary>
    public void AdoptThemeFrom(AppConfig fresh)
    {
        _ghosttyTheme = fresh._ghosttyTheme;
        _ghosttyInline = fresh._ghosttyInline;
        SetTheme(fresh._appTheme);
    }

    /// <summary>
    /// Writes "theme" into config.json (null = inherit Ghostty's), keeping the
    /// file's comments and layout. Returns false if the file could not be written.
    /// </summary>
    public static bool SaveThemeSetting(string? theme)
    {
        try
        {
            if (!File.Exists(AppConfigPath))
            {
                WriteDefaultAppConfig();
            }

            string text = File.Exists(AppConfigPath) ? File.ReadAllText(AppConfigPath) : "{\n}\n";
            // Relaxed escaping keeps names readable for hand edits
            // ("Dracula+", not "Dracula\u002B"); still valid JSON.
            string value = theme is null
                ? "null"
                : JsonSerializer.Serialize(theme, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            string updated = Jsonc.SetTopLevelProperty(text, "theme", value);
            Directory.CreateDirectory(ConfigDir);
            string tmp = AppConfigPath + ".tmp";
            File.WriteAllText(tmp, updated);
            File.Move(tmp, AppConfigPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not save theme to config.json: {ex.Message}");
            return false;
        }
    }

    /// <summary>Stable comparison key for a resolved theme (skip no-op repaints).</summary>
    public static string Fingerprint(TerminalTheme? theme)
    {
        if (theme is null)
        {
            return string.Empty;
        }

        System.Text.StringBuilder sb = new();
        sb.Append(theme.DefaultForeground).Append(',').Append(theme.DefaultBackground).Append(',')
            .Append(theme.CursorColor).Append(',').Append(theme.CursorTextColor).Append(',')
            .Append(theme.SelectionForeground).Append(',').Append(theme.SelectionBackground);
        for (int i = 0; i < 16; i++)
        {
            sb.Append(',').Append(theme.Palette[i]);
        }

        return sb.ToString();
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
                foreach ((string key, string value) in ParseThemeFile(file))
                {
                    keys[key] = value;
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

        return ThemeFromKeys(keys);
    }

    /// <summary>
    /// Reads a Ghostty theme file into normalized keys ("background",
    /// "palette:3", ...). Palette entries keep their "N=#hex" value.
    /// </summary>
    internal static Dictionary<string, string> ParseThemeFile(string path)
    {
        Dictionary<string, string> keys = new(StringComparer.Ordinal);
        foreach ((string key, string value) in ParseGhosttyFile(path))
        {
            keys[key == "palette" ? $"palette:{value.Split('=')[0].Trim()}" : key] = value;
        }

        return keys;
    }

    /// <summary>Builds a terminal theme from normalized keys; gaps come from the dark default.</summary>
    internal static TerminalTheme ThemeFromKeys(Dictionary<string, string> keys)
    {
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
    internal static bool TryParseColor(string value, out uint color)
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

    /// <summary>Theme folders, searched in this order (first match wins).</summary>
    internal static string[] ThemeDirs
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return
            [
                Path.Combine(Path.GetDirectoryName(GhosttyConfigPath)!, "themes"),
                UserThemesDir,
                "/usr/share/ghostty/themes",
                "/usr/local/share/ghostty/themes",
                Path.Combine(home, ".local", "share", "ghostty", "themes"),
                "/Applications/Ghostty.app/Contents/Resources/ghostty/themes",
            ];
        }
    }

    internal static string GhosttyConfigFile => GhosttyConfigPath;

    internal static string? FindThemeFile(string name)
    {
        if (Path.IsPathRooted(name) && File.Exists(name))
        {
            return name;
        }

        if (!ThemeCatalog.IsSafeName(name))
        {
            return null;
        }

        foreach (string dir in ThemeDirs)
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
