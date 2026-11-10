using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RoyalTerminal.Terminal;

namespace Wraithcairn;

/// <summary>
/// Per-pane startup configuration, stored inline in the layout JSON as a
/// "startup" object on each leaf pane:
///
///   "rootPane": {
///     "id": "pane-1",
///     "workingDirectory": "/home/ryan/code/app",   (last seen cwd, informational)
///     "startup": {
///       "directory": "~/code/app",                  (where the pane starts; wins over last cwd)
///       "command": "npm",
///       "args": ["run", "dev"],
///       "runInShell": true                          (default true)
///     }
///   }
///
/// runInShell = true: the shell starts in Directory, then Command + Args are
///   typed into it. Command is inserted as-is (pipes, &&, $VARS work); each arg
///   is quoted for the shell. When the program exits you are back at a prompt.
/// runInShell = false: Command is executed directly as the pane's process with
///   Args as argv (no shell). When it exits the pane stays open with a notice;
///   Ctrl+Shift+R restarts it.
/// </summary>
public sealed class PaneStartup
{
    public string? Directory { get; set; }

    public string? Command { get; set; }

    public List<string> Args { get; set; } = [];

    public bool RunInShell { get; set; } = true;

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Directory) && string.IsNullOrWhiteSpace(Command);

    [JsonIgnore]
    public bool HasCommand => !string.IsNullOrWhiteSpace(Command);

    /// <summary>Exec mode: the program is the pane's process.</summary>
    [JsonIgnore]
    public bool IsExec => HasCommand && !RunInShell;

    public PaneStartup Clone() => new()
    {
        Directory = Directory,
        Command = Command,
        Args = [.. Args],
        RunInShell = RunInShell,
    };

    /// <summary>Expands a leading ~ and returns null when the result does not exist.</summary>
    public string? ResolveDirectory()
    {
        if (string.IsNullOrWhiteSpace(Directory))
        {
            return null;
        }

        string path = Directory.Trim();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path == "~")
        {
            path = home;
        }
        else if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = Path.Combine(home, path[2..]);
        }

        path = Environment.ExpandEnvironmentVariables(path);
        return System.IO.Directory.Exists(path) ? Path.GetFullPath(path) : null;
    }

    /// <summary>The line typed into the shell for runInShell mode (no trailing newline).</summary>
    public string BuildShellLine(string shellName)
    {
        StringBuilder sb = new(Command!.Trim());
        foreach (string arg in Args)
        {
            sb.Append(' ').Append(QuoteForShell(arg, shellName));
        }

        return sb.ToString();
    }

    public static string QuoteForShell(string arg, string shellName)
    {
        if (arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || "_./:=@%+,-".Contains(c)))
        {
            return arg;
        }

        return shellName switch
        {
            "pwsh" or "powershell" => "'" + arg.Replace("'", "''") + "'",
            "fish" => "'" + arg.Replace("\\", "\\\\").Replace("'", "\\'") + "'",
            "cmd" => "\"" + arg.Replace("\"", "\"\"") + "\"",
            _ => "'" + arg.Replace("'", "'\\''") + "'",
        };
    }

    /// <summary>Splits an argument string like a shell would (quotes, backslash escapes).</summary>
    public static List<string> SplitArgs(string? text)
    {
        List<string> result = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        StringBuilder current = new();
        bool inToken = false;
        char quote = '\0';

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (c == '\\' && quote == '"' && i + 1 < text.Length)
                {
                    current.Append(text[++i]);
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
            }
            else if (c == '\\' && i + 1 < text.Length)
            {
                current.Append(text[++i]);
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (inToken)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    /// <summary>Joins args back into an editable string (inverse of SplitArgs).</summary>
    public static string JoinArgs(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => QuoteForShell(a, "bash")));
}

/// <summary>
/// Reads and writes the layout file. The workspace document itself goes
/// through RoyalTerminal's serializer (validation + normalization); per-pane
/// "startup" objects are merged in afterwards because that serializer drops
/// unknown fields. Writes are atomic (temp file + rename).
/// </summary>
public static class LayoutFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static void Save(
        string path,
        TerminalWorkspaceDocument document,
        IReadOnlyDictionary<string, PaneStartup> startups)
    {
        string json = TerminalWorkspaceSerializer.ToJson(document);
        JsonNode root = JsonNode.Parse(json)!;

        foreach (JsonObject pane in EnumeratePanes(root))
        {
            if (pane["id"]?.GetValue<string>() is { } id
                && startups.TryGetValue(id, out PaneStartup? startup)
                && !startup.IsEmpty)
            {
                pane["startup"] = JsonSerializer.SerializeToNode(startup, JsonOptions);
            }
        }

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            System.IO.Directory.CreateDirectory(dir);
        }

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public static (TerminalWorkspaceDocument Document, Dictionary<string, PaneStartup> Startups) Load(string path)
    {
        Dictionary<string, PaneStartup> startups = new(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return (new TerminalWorkspaceDocument(), startups);
        }

        string json = File.ReadAllText(path);
        JsonNode? root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        if (root is not null)
        {
            foreach (JsonObject pane in EnumeratePanes(root))
            {
                if (pane["id"]?.GetValue<string>() is { } id
                    && pane["startup"] is JsonObject startupNode
                    && startupNode.Deserialize<PaneStartup>(JsonOptions) is { } startup)
                {
                    startups[id] = startup;
                }

                // RoyalTerminal's strict parser does not need to see it.
                pane.Remove("startup");
            }
        }

        TerminalWorkspaceDocument document = TerminalWorkspaceSerializer.FromJson(
            root?.ToJsonString() ?? json);
        return (document, startups);
    }

    /// <summary>All pane objects (leaves and splits) anywhere in the document.</summary>
    private static IEnumerable<JsonObject> EnumeratePanes(JsonNode root)
    {
        if (root["windows"] is not JsonArray windows)
        {
            yield break;
        }

        foreach (JsonNode? window in windows)
        {
            if (window?["tabs"] is not JsonArray tabs)
            {
                continue;
            }

            foreach (JsonNode? tab in tabs)
            {
                if (tab?["rootPane"] is JsonObject rootPane)
                {
                    foreach (JsonObject pane in Walk(rootPane))
                    {
                        yield return pane;
                    }
                }
            }
        }

        static IEnumerable<JsonObject> Walk(JsonObject pane)
        {
            yield return pane;
            if (pane["split"] is JsonObject split)
            {
                if (split["firstPane"] is JsonObject first)
                {
                    foreach (JsonObject p in Walk(first)) yield return p;
                }

                if (split["secondPane"] is JsonObject second)
                {
                    foreach (JsonObject p in Walk(second)) yield return p;
                }
            }
        }
    }
}
