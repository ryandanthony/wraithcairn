using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Wraithcairn;

/// <summary>
/// The offline speech model used by voice input (Ctrl+Shift+I), and its
/// one-time download.
///
/// Files land in {voiceModelsDir}/{model id}/ (default: "models" next to
/// config.json). Each file is pinned by size and SHA-256 to one Hugging Face
/// revision, downloaded to a ".part" file that resumes with an HTTP Range
/// request, retried a few times on network errors, verified, then renamed.
/// After that nothing touches the network: recognition runs locally.
///
/// WRAITHCAIRN_VOICE_MODEL_URL replaces the download base (http(s) URL or a
/// local folder holding the files), for tests and mirrors.
/// </summary>
public sealed class VoiceModel
{
    public sealed record ModelFile(string Name, long Size, string Sha256);

    /// <summary>Folder name under the models dir.</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string License { get; init; }

    /// <summary>Base URL the file names are appended to.</summary>
    public required string BaseUrl { get; init; }

    public required IReadOnlyList<ModelFile> Files { get; init; }

    public string Encoder { get; init; } = "encoder.onnx";

    public string Decoder { get; init; } = "decoder.onnx";

    public string Joiner { get; init; } = "joiner.onnx";

    public string Tokens { get; init; } = "tokens.txt";

    public long TotalBytes => Files.Sum(f => f.Size);

    /// <summary>
    /// Kroko streaming Zipformer (English): 68 MB, punctuation and casing,
    /// about 40x faster than real time on one laptop core. CC-BY-SA 4.0.
    /// </summary>
    public static VoiceModel Default { get; } = new()
    {
        Id = "sherpa-onnx-streaming-zipformer-en-kroko-2025-08-06",
        DisplayName = "Kroko English streaming model",
        License = "CC-BY-SA 4.0 (Banafo / Kroko ASR)",
        BaseUrl = "https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-en-kroko-2025-08-06/resolve/572aaf4e2e0c603c3fc2a574d096e755a178faa1",
        Files =
        [
            new("encoder.onnx", 70092599, "d4881c57449d581e0770fd53fa66c2fdc6cd167d92ece7c715e603defc96d9d4"),
            new("decoder.onnx", 617488, "455ba38466fce8d5a57e7db68a323b684079ca4d9e1dd93a740d9b2429aae3b1"),
            new("joiner.onnx", 336817, "d406f616736350e2a7df3e39398b78eb2fc1a2ca6973a19d3853fa3227e25b52"),
            new("tokens.txt", 6310, "396dbeb5f4858875690716084f54e90d339679d0ba3e6b5b584f3d7589254d2d"),
        ],
    };

    /// <summary>Where models go: config "voiceModelsDir", else "models" next to config.json.</summary>
    public static string ResolveModelsDir(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(AppConfig.ConfigDir, "models");
        }

        string path = configured.Trim();
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : string.Empty);
        }

        path = ExpandVariables(path);
        // Relative paths are relative to the config folder, like the default.
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(AppConfig.ConfigDir, path));
    }

    /// <summary>
    /// Expands %VAR% (all OSes, .NET's rule) plus $VAR and ${VAR} (shell
    /// style). Unknown variables are left as written.
    /// </summary>
    internal static string ExpandVariables(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        return System.Text.RegularExpressions.Regex.Replace(
            path,
            @"\$\{(?<n>[A-Za-z_][A-Za-z0-9_]*)\}|\$(?<n>[A-Za-z_][A-Za-z0-9_]*)",
            m => Environment.GetEnvironmentVariable(m.Groups["n"].Value) ?? m.Value);
    }

    public string DirIn(string modelsDir) => Path.Combine(modelsDir, Id);

    /// <summary>All files present with the expected size (hashes are checked at download time).</summary>
    public bool IsInstalled(string modelsDir) =>
        Files.All(f => new FileInfo(Path.Combine(DirIn(modelsDir), f.Name)) is { Exists: true } info && info.Length == f.Size);

    // ----- download -----------------------------------------------------------

    public sealed record Progress(long Done, long Total, string File, double BytesPerSecond, string? Note);

    /// <summary>Attempts per file before giving up (the dialog's Retry starts over, resuming).</summary>
    public const int MaxAttempts = 4;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // No overall timeout (68 MB on a slow link); a stalled read is caught
        // by the per-read idle timeout below instead.
        HttpClient client = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"wraithcairn/{AppInfo.Version}");
        return client;
    }

    /// <summary>Read idle timeout: no bytes for this long counts as a dropped connection.</summary>
    internal static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Delay before retry n (1-based). Overridable for tests.</summary>
    internal static Func<int, TimeSpan> RetryDelay { get; set; } = attempt => TimeSpan.FromSeconds(Math.Min(8, 1 << (attempt - 1)));

    private string SourceBase()
    {
        string? env = Environment.GetEnvironmentVariable("WRAITHCAIRN_VOICE_MODEL_URL");
        return (string.IsNullOrWhiteSpace(env) ? BaseUrl : env).TrimEnd('/', '\\');
    }

    /// <summary>
    /// Downloads every missing file into {modelsDir}/{Id}. Resumes ".part"
    /// files, retries transient failures, verifies SHA-256. Throws on
    /// cancellation or after <see cref="MaxAttempts"/> failed attempts.
    /// </summary>
    public async Task DownloadAsync(string modelsDir, IProgress<Progress>? progress, CancellationToken ct)
    {
        string dir = DirIn(modelsDir);
        Directory.CreateDirectory(dir);
        long total = TotalBytes;
        long doneBefore = 0;
        Stopwatch clock = Stopwatch.StartNew();
        long bytesThisRun = 0;

        foreach (ModelFile file in Files)
        {
            string target = Path.Combine(dir, file.Name);
            if (new FileInfo(target) is { Exists: true } existing && existing.Length == file.Size)
            {
                doneBefore += file.Size;
                progress?.Report(new Progress(doneBefore, total, file.Name, 0, null));
                continue;
            }

            string part = target + ".part";
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long fileStart = doneBefore;
                    await DownloadFileAsync(file, part, have =>
                    {
                        double seconds = Math.Max(0.25, clock.Elapsed.TotalSeconds);
                        progress?.Report(new Progress(fileStart + have, total, file.Name, bytesThisRun / seconds, null));
                    }, n => bytesThisRun += n, ct);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    if (ex is InvalidDataException)
                    {
                        // Bad bytes: resuming would keep them. Start this file over.
                        TryDelete(part);
                    }

                    if (attempt >= MaxAttempts)
                    {
                        throw new IOException($"{file.Name}: {Describe(ex)} (gave up after {attempt} attempts)", ex);
                    }

                    TimeSpan wait = RetryDelay(attempt);
                    long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                    progress?.Report(new Progress(doneBefore + have, total, file.Name, 0,
                        $"{Describe(ex)}. Retrying in {wait.TotalSeconds:0}s (attempt {attempt + 1} of {MaxAttempts})"));
                    await Task.Delay(wait, ct);
                }
            }

            File.Move(part, target, overwrite: true);
            doneBefore += file.Size;
            progress?.Report(new Progress(doneBefore, total, file.Name, bytesThisRun / Math.Max(0.25, clock.Elapsed.TotalSeconds), null));
        }
    }

    private async Task DownloadFileAsync(ModelFile file, string part, Action<long> onProgress, Action<long> onBytes, CancellationToken ct)
    {
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > file.Size)
        {
            TryDelete(part);
            have = 0;
        }

        string source = SourceBase();
        if (have < file.Size)
        {
            if (IsLocal(source))
            {
                await CopyLocalAsync(Path.Combine(LocalPath(source), file.Name), part, have, onProgress, onBytes, ct);
            }
            else
            {
                await DownloadHttpAsync($"{source}/{file.Name}", part, have, onProgress, onBytes, ct);
            }
        }

        long size = new FileInfo(part).Length;
        if (size != file.Size)
        {
            throw new IOException($"incomplete download ({size} of {file.Size} bytes)");
        }

        string hash = await HashAsync(part, ct);
        if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("checksum mismatch");
        }
    }

    private static async Task DownloadHttpAsync(string url, string part, long have, Action<long> onProgress, Action<long> onBytes, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        if (have > 0)
        {
            request.Headers.Range = new RangeHeaderValue(have, null);
        }

        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Our .part is not a prefix the server agrees with; start over.
            TryDelete(part);
            throw new IOException("server refused to resume");
        }

        response.EnsureSuccessStatusCode();
        bool append = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
        {
            have = 0; // server ignored the Range header: full body follows
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(ct);
        await using FileStream output = new(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await PumpAsync(body, output, have, onProgress, onBytes, ct);
    }

    private static async Task CopyLocalAsync(string source, string part, long have, Action<long> onProgress, Action<long> onBytes, CancellationToken ct)
    {
        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        input.Seek(have, SeekOrigin.Begin);
        await using FileStream output = new(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await PumpAsync(input, output, have, onProgress, onBytes, ct);
    }

    private static async Task PumpAsync(Stream input, Stream output, long have, Action<long> onProgress, Action<long> onBytes, CancellationToken ct)
    {
        byte[] buffer = new byte[1 << 16];
        long lastReport = 0;
        while (true)
        {
            using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleTimeout);
            int read;
            try
            {
                read = await input.ReadAsync(buffer, idle.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"no data for {IdleTimeout.TotalSeconds:0}s");
            }

            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            have += read;
            onBytes(read);
            if (have - lastReport >= 256 * 1024)
            {
                lastReport = have;
                onProgress(have);
            }
        }

        await output.FlushAsync(ct);
        onProgress(have);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    internal static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code} {code}",
        HttpRequestException http => http.InnerException?.Message ?? http.Message,
        TimeoutException timeout => timeout.Message,
        _ => ex.Message,
    };

    private static bool IsLocal(string url) =>
        !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string LocalPath(string url) =>
        url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(url).LocalPath : url;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    public static string FormatBytes(long bytes) => bytes >= 1_000_000
        ? $"{bytes / 1_000_000.0:0.0} MB"
        : $"{bytes / 1000.0:0} KB";
}
