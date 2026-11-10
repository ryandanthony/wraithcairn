namespace SpikeRoyalTerminal;

/// <summary>
/// Named layouts. The default layout is workspace.json; each named one is
/// layouts/{name}.json. Launch with --workspace NAME (or -w NAME) to open and
/// keep saving into a named layout. "Save Layout As" copies the current
/// layout under a new name; "Open Layout" starts a new instance on it.
/// </summary>
public static class NamedLayouts
{
    public static string LayoutsDir { get; } = Path.Combine(AppConfig.ConfigDir, "layouts");

    /// <summary>The layout name this process uses (null = default workspace.json).</summary>
    public static string? ActiveName { get; } = ParseName(Environment.GetCommandLineArgs());

    public static string ActivePath => PathFor(ActiveName);

    public static string PathFor(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? Path.Combine(AppConfig.ConfigDir, "workspace.json")
            : Path.Combine(LayoutsDir, Sanitize(name) + ".json");

    public static IReadOnlyList<string> List()
    {
        if (!Directory.Exists(LayoutsDir))
        {
            return [];
        }

        return Directory.EnumerateFiles(LayoutsDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Copies the active layout file to a named slot.</summary>
    public static string SaveAs(string name)
    {
        Directory.CreateDirectory(LayoutsDir);
        string target = PathFor(name);
        if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(ActivePath), StringComparison.Ordinal))
        {
            File.Copy(ActivePath, target, overwrite: true);
        }

        return target;
    }

    public static void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Starts a new instance of this app on the given layout.</summary>
    public static void Launch(string name)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        System.Diagnostics.ProcessStartInfo psi = new(exe)
        {
            UseShellExecute = false,
        };

        // `dotnet run` / `dotnet app.dll` hosts: pass the dll first.
        string? entry = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet" && entry is not null)
        {
            psi.ArgumentList.Add(entry);
        }

        psi.ArgumentList.Add("--workspace");
        psi.ArgumentList.Add(name);
        System.Diagnostics.Process.Start(psi);
    }

    public static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new(name.Trim().Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray());
        return cleaned.Length == 0 ? "layout" : cleaned;
    }

    private static string? ParseName(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--workspace" or "-w")
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
