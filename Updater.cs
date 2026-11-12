using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Wraithcairn;

/// <summary>
/// Self-update from GitHub Releases, the same way the install scripts do it:
/// read the latest release's SHA256SUMS (releases/latest/download avoids the
/// rate-limited API), pick this platform's archive, verify its SHA-256, and
/// swap the new binary in place.
///
///   Linux    the single-file binary is replaced atomically (rename), so a
///            running wraithcairn keeps its old inode
///   macOS    the whole wraithcairn.app bundle is swapped (ditto keeps
///            the signature and symlinks)
///   Windows  the running exe is renamed to .old (allowed while running),
///            the new one copied in; .old is removed on the next start
///
/// WRAITHCAIRN_RELEASES_URL overrides the releases base (an http(s) URL or a
/// local folder laid out as latest/download/SHA256SUMS and
/// download/vX.Y.Z/asset), for tests.
/// </summary>
public static class Updater
{
    public const string Repo = "ryandanthony/wraithcairn";
    public const string DefaultReleasesUrl = $"https://github.com/{Repo}/releases";

    private static readonly HttpClient Http = CreateClient();

    public sealed record ReleaseInfo(string Version, string Asset, string Sha256, string Rid);

    public sealed record UpdateCheck(string Current, ReleaseInfo? Latest, bool IsNewer, string? Error)
    {
        public bool IsDevBuild => Updater.IsDevBuild(Current);
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"wraithcairn/{AppInfo.Version}");
        return client;
    }

    public static string ReleasesUrl
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("WRAITHCAIRN_RELEASES_URL");
            return string.IsNullOrWhiteSpace(env) ? DefaultReleasesUrl : env.TrimEnd('/', '\\');
        }
    }

    public static string ReleaseNotesUrl(string version) => $"https://github.com/{Repo}/releases/tag/v{version}";

    /// <summary>Release asset name for this OS/CPU, e.g. linux-x64 + tar.gz.</summary>
    public static (string Rid, string Ext) Platform()
    {
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        if (OperatingSystem.IsWindows())
        {
            return ($"win-{arch}", "zip");
        }

        if (OperatingSystem.IsMacOS())
        {
            return ($"osx-{arch}", "zip");
        }

        return ($"linux-{arch}", "tar.gz");
    }

    /// <summary>Local builds report 0.0.0-dev; they never auto-update.</summary>
    public static bool IsDevBuild(string version) =>
        version.StartsWith("0.0.0", StringComparison.Ordinal) || version.Contains("-dev", StringComparison.Ordinal);

    /// <summary>Compares X.Y.Z[-pre] versions; a release beats its own prerelease.</summary>
    public static int CompareVersions(string a, string b)
    {
        static (Version Core, string Pre) Parse(string v)
        {
            v = v.Trim().TrimStart('v', 'V').Split('+')[0];
            int dash = v.IndexOf('-');
            string core = dash >= 0 ? v[..dash] : v;
            string pre = dash >= 0 ? v[(dash + 1)..] : string.Empty;
            return (Version.TryParse(core, out Version? parsed) ? parsed : new Version(0, 0, 0), pre);
        }

        (Version ca, string pa) = Parse(a);
        (Version cb, string pb) = Parse(b);
        int c = ca.CompareTo(cb);
        if (c != 0)
        {
            return c;
        }

        if (pa.Length == 0 || pb.Length == 0)
        {
            // "1.2.0" > "1.2.0-rc1"; equal when neither has a prerelease.
            return (pa.Length == 0 ? 1 : 0) - (pb.Length == 0 ? 1 : 0);
        }

        return string.CompareOrdinal(pa, pb);
    }

    private static bool IsLocal(string url) =>
        !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string LocalPath(string url) =>
        url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(url).LocalPath : url;

    private static async Task<byte[]> GetBytesAsync(string relative, CancellationToken ct)
    {
        string baseUrl = ReleasesUrl;
        if (IsLocal(baseUrl))
        {
            return await File.ReadAllBytesAsync(
                Path.Combine(LocalPath(baseUrl), relative.Replace('/', Path.DirectorySeparatorChar)), ct);
        }

        return await Http.GetByteArrayAsync($"{baseUrl}/{relative}", ct);
    }

    /// <summary>Looks up the latest release for this platform.</summary>
    public static async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        string current = AppInfo.Version;
        try
        {
            byte[] sums = await GetBytesAsync("latest/download/SHA256SUMS", ct);
            ReleaseInfo latest = ParseSums(System.Text.Encoding.UTF8.GetString(sums));
            bool newer = IsDevBuild(current) || CompareVersions(latest.Version, current) > 0;
            return new UpdateCheck(current, latest, newer, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new UpdateCheck(current, null, false, Describe(ex));
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "no published release found",
        HttpRequestException http => $"could not reach GitHub ({http.Message})",
        TaskCanceledException => "timed out talking to GitHub",
        _ => ex.Message,
    };

    /// <summary>Finds this platform's archive in a SHA256SUMS file.</summary>
    public static ReleaseInfo ParseSums(string sums)
    {
        (string rid, string ext) = Platform();
        string suffix = $"-{rid}.{ext}";
        foreach (string raw in sums.Split('\n'))
        {
            string[] parts = raw.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            string hash = parts[0].ToLowerInvariant();
            string asset = parts[1].TrimStart('*').Trim();
            if (!asset.StartsWith("wraithcairn-", StringComparison.Ordinal) || !asset.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            string version = asset["wraithcairn-".Length..^suffix.Length];
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || version.Length == 0 || !char.IsDigit(version[0])
                || version.IndexOfAny(['/', '\\']) >= 0)
            {
                throw new InvalidDataException($"bad SHA256SUMS line for {asset}");
            }

            return new ReleaseInfo(version, asset, hash, rid);
        }

        throw new InvalidDataException($"the latest release has no {rid} build");
    }

    /// <summary>The installed binary, or the .app bundle on macOS.</summary>
    public static string? InstallTarget()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            int at = exe.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
            return at >= 0 ? exe[..(at + 4)] : exe;
        }

        return exe;
    }

    /// <summary>Null when this process can update itself, else why not.</summary>
    public static string? CannotSelfUpdateReason()
    {
        string? target = InstallTarget();
        if (target is null)
        {
            return "could not find the running executable";
        }

        // Releases are single-file publishes; `dotnet run` / bin/ builds are not.
        if (!string.IsNullOrEmpty(typeof(Updater).Assembly.Location))
        {
            return "this is not an installed release build (run from source); use the install script or git pull";
        }

        if (OperatingSystem.IsMacOS() && !target.EndsWith(".app", StringComparison.Ordinal))
        {
            return "not running from wraithcairn.app; reinstall with the install script";
        }

        string dir = Path.GetDirectoryName(target) ?? ".";
        try
        {
            string probe = Path.Combine(dir, $".wraithcairn-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch
        {
            return $"no write access to {dir}; rerun the install script (or update with sudo)";
        }

        return null;
    }

    /// <summary>
    /// Downloads, verifies, and installs <paramref name="release"/> over the
    /// running installation. Returns the installed path. Restart to use it.
    /// </summary>
    public static async Task<string> InstallAsync(ReleaseInfo release, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (CannotSelfUpdateReason() is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        string target = InstallTarget()!;
        string tmp = Directory.CreateTempSubdirectory("wraithcairn-update-").FullName;
        try
        {
            progress?.Report($"Downloading {release.Asset}\u2026");
            string archive = Path.Combine(tmp, release.Asset);
            await DownloadAsync($"download/v{release.Version}/{release.Asset}", archive, progress, ct);

            progress?.Report("Verifying SHA-256\u2026");
            string actual;
            await using (FileStream fs = File.OpenRead(archive))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
            }

            if (actual != release.Sha256)
            {
                throw new InvalidDataException($"SHA-256 mismatch for {release.Asset} (expected {release.Sha256}, got {actual})");
            }

            progress?.Report("Installing\u2026");
            // A single-file app loads assemblies lazily from its own file, by
            // path. Once the new binary sits at that path, a later load would
            // read the new bundle and fail (FileNotFoundException), so pull
            // everything this process can still need into memory first.
            PreloadAssemblies();
            if (OperatingSystem.IsMacOS())
            {
                await InstallMacAsync(archive, tmp, target, ct);
            }
            else if (OperatingSystem.IsWindows())
            {
                InstallWindows(archive, tmp, target);
            }
            else
            {
                await InstallLinuxAsync(archive, target, ct);
            }

            string? installedVersion = await RunVersionAsync(ExecutableFor(target));
            if (installedVersion is not null && installedVersion != release.Version)
            {
                throw new InvalidOperationException($"installed binary reports {installedVersion}, expected {release.Version}");
            }

            return target;
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, recursive: true);
            }
            catch
            {
                // temp cleanup is best effort
            }
        }
    }

    /// <summary>Loads every assembly reachable from the app (transitively).</summary>
    public static int PreloadAssemblies()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        Stack<System.Reflection.Assembly> pending = new(AppDomain.CurrentDomain.GetAssemblies());
        foreach (System.Reflection.Assembly loaded in pending)
        {
            seen.Add(loaded.GetName().Name ?? string.Empty);
        }

        while (pending.Count > 0)
        {
            System.Reflection.Assembly assembly = pending.Pop();
            foreach (System.Reflection.AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is null || !seen.Add(reference.Name))
                {
                    continue;
                }

                try
                {
                    pending.Push(System.Reflection.Assembly.Load(reference));
                }
                catch
                {
                    // optional/platform-specific reference not in this bundle
                }
            }
        }

        return seen.Count;
    }

    private static async Task DownloadAsync(string relative, string dest, IProgress<string>? progress, CancellationToken ct)
    {
        string baseUrl = ReleasesUrl;
        if (IsLocal(baseUrl))
        {
            File.Copy(Path.Combine(LocalPath(baseUrl), relative.Replace('/', Path.DirectorySeparatorChar)), dest, overwrite: true);
            return;
        }

        using HttpResponseMessage response = await Http.GetAsync($"{baseUrl}/{relative}", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using Stream source = await response.Content.ReadAsStreamAsync(ct);
        await using FileStream file = File.Create(dest);
        byte[] buffer = new byte[81920];
        long done = 0;
        long lastReport = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (progress is not null && done - lastReport > 1_000_000)
            {
                lastReport = done;
                progress.Report(total is > 0
                    ? $"Downloading\u2026 {done / 1_048_576.0:0.0} / {total.Value / 1_048_576.0:0.0} MB"
                    : $"Downloading\u2026 {done / 1_048_576.0:0.0} MB");
            }
        }
    }

    private static async Task InstallLinuxAsync(string archive, string target, CancellationToken ct)
    {
        string staged = $"{target}.new.{Environment.ProcessId}";
        bool found = false;
        await using (FileStream fs = File.OpenRead(archive))
        await using (GZipStream gz = new(fs, CompressionMode.Decompress))
        await using (TarReader tar = new(gz))
        {
            TarEntry? entry;
            while ((entry = await tar.GetNextEntryAsync(cancellationToken: ct)) is not null)
            {
                if (entry.Name.TrimStart('.', '/') == "wraithcairn/wraithcairn" && entry.DataStream is not null)
                {
                    await using FileStream outFile = File.Create(staged);
                    await entry.DataStream.CopyToAsync(outFile, ct);
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            throw new InvalidDataException("archive did not contain wraithcairn/wraithcairn");
        }

        File.SetUnixFileMode(staged,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(staged, target, overwrite: true); // rename(2): atomic
    }

    private static async Task InstallMacAsync(string archive, string tmp, string bundle, CancellationToken ct)
    {
        string unpacked = Path.Combine(tmp, "unpacked");
        Directory.CreateDirectory(unpacked);
        // ditto keeps the bundle's symlinks, permissions and signature.
        await RunAsync("ditto", ["-x", "-k", archive, unpacked], ct);
        string fresh = Path.Combine(unpacked, "wraithcairn.app");
        if (!Directory.Exists(fresh))
        {
            throw new InvalidDataException("archive did not contain wraithcairn.app");
        }

        try
        {
            await RunAsync("xattr", ["-dr", "com.apple.quarantine", fresh], ct);
        }
        catch
        {
            // not quarantined
        }

        string old = $"{bundle}.old.{Environment.ProcessId}";
        Directory.Move(bundle, old);
        try
        {
            await RunAsync("mv", [fresh, bundle], ct); // may cross filesystems
        }
        catch
        {
            Directory.Move(old, bundle);
            throw;
        }

        try
        {
            Directory.Delete(old, recursive: true);
        }
        catch
        {
            // the running process may still map files from it; harmless
        }
    }

    private static void InstallWindows(string archive, string tmp, string target)
    {
        string unpacked = Path.Combine(tmp, "unpacked");
        ZipFile.ExtractToDirectory(archive, unpacked);
        string fresh = Path.Combine(unpacked, "wraithcairn", "wraithcairn.exe");
        if (!File.Exists(fresh))
        {
            throw new InvalidDataException("archive did not contain wraithcairn/wraithcairn.exe");
        }

        string old = target + ".old";
        try
        {
            File.Delete(old);
        }
        catch
        {
            // an older .old may still be running; a fresh name keeps going
            old = $"{target}.{Environment.ProcessId}.old";
        }

        File.Move(target, old); // a running exe can be renamed, not overwritten
        try
        {
            File.Copy(fresh, target);
        }
        catch
        {
            File.Move(old, target);
            throw;
        }
    }

    /// <summary>Windows: removes the exe left behind by the last update.</summary>
    public static void CleanupAfterUpdate()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe)
        {
            return;
        }

        string dir = Path.GetDirectoryName(exe) ?? ".";
        foreach (string old in Directory.EnumerateFiles(dir, Path.GetFileName(exe) + "*.old").ToArray())
        {
            try
            {
                File.Delete(old);
            }
            catch
            {
                // still running elsewhere
            }
        }
    }

    private static string ExecutableFor(string target) =>
        OperatingSystem.IsMacOS() && target.EndsWith(".app", StringComparison.Ordinal)
            ? Path.Combine(target, "Contents", "MacOS", "wraithcairn")
            : target;

    /// <summary>Runs `exe --version`; null if it could not be read (e.g. Windows GUI exe).</summary>
    private static async Task<string?> RunVersionAsync(string exe)
    {
        try
        {
            ProcessStartInfo psi = new(exe, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process? p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
            string output = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            string[] words = output.Trim().Split(' ');
            return words.Length >= 2 && words[0] == "wraithcairn" ? words[1] : null;
        }
        catch
        {
            if (OperatingSystem.IsWindows())
            {
                return null;
            }

            throw new InvalidOperationException($"the installed binary did not run: {exe} --version");
        }
    }

    private static async Task RunAsync(string file, string[] args, CancellationToken ct)
    {
        ProcessStartInfo psi = new(file) { UseShellExecute = false, RedirectStandardError = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"could not run {file}");
        string err = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} failed: {err.Trim()}");
        }
    }

    /// <summary>Starts the (updated) app again with the same arguments.</summary>
    public static bool StartNewInstance()
    {
        string? exe = InstallTarget() is { } target ? ExecutableFor(target) : Environment.ProcessPath;
        if (exe is null)
        {
            return false;
        }

        try
        {
            ProcessStartInfo psi = new(exe) { UseShellExecute = false };
            // One-shot startup actions must not repeat in the new process.
            string[] oneShot = ["--check-updates", "--configure-pane", "--selftest", "--dump-panes"];
            foreach (string arg in Environment.GetCommandLineArgs().Skip(1).Where(a => !oneShot.Contains(a)))
            {
                psi.ArgumentList.Add(arg);
            }

            return Process.Start(psi) is not null;
        }
        catch
        {
            return false;
        }
    }
}
