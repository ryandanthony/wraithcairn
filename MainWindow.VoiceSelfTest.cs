using System.Net;
using System.Net.Sockets;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RoyalTerminal.Avalonia.Controls;

namespace Wraithcairn;

/// <summary>
/// Selftests for voice input, run by --selftest. No network and no
/// microphone needed:
///   voice_download  a local HTTP server serves the model; the first request
///                   drops mid-file, the dialog shows a retry, resumes with a
///                   Range request and installs (checksums verified).
///   voice_retry     a server that always fails: the dialog ends on
///                   "Download failed" + Try Again; once fixed, Try Again
///                   resumes from the partial file and succeeds.
///   voice_dictate   Ctrl+Shift+I on a pane with a WAV file standing in for
///                   the microphone: header shows the mic, the phrase is
///                   typed at the prompt without Enter, Ctrl+Shift+I stops.
/// The model files come from WRAITHCAIRN_VOICE_SELFTEST_MODEL (a folder with
/// the real files) or an already installed model; otherwise the
/// recognition part is skipped and reported as such.
/// </summary>
public sealed partial class MainWindow
{
    private async Task RunVoiceSelfTestAsync()
    {
        VoiceModel real = VoiceModel.Default;
        string? source = Environment.GetEnvironmentVariable("WRAITHCAIRN_VOICE_SELFTEST_MODEL") is { Length: > 0 } env
            ? env
            : real.IsInstalled(Config.ResolvedVoiceModelsDir) ? real.DirIn(Config.ResolvedVoiceModelsDir) : null;

        Environment.SetEnvironmentVariable("WC_T", "q");
        bool envExpands = VoiceModel.ExpandVariables("$WC_T/a/${WC_T}/%WC_T%/$NOPE_WC") == "q/a/q/q/$NOPE_WC";
        Environment.SetEnvironmentVariable("WC_T", null);
        bool modelsDirDefault = Config.ResolvedVoiceModelsDir == Path.Combine(AppConfig.ConfigDir, "models");
        Console.WriteLine($"SELFTEST voice_models_dir_default={modelsDirDefault || Config.VoiceModelsDir is not null} "
            + $"relative={VoiceModel.ResolveModelsDir("m") == Path.Combine(AppConfig.ConfigDir, "m")} "
            + $"home={VoiceModel.ResolveModelsDir("~/x") == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "x")} "
            + $"env={envExpands}");

        await RunVoiceDownloadSelfTestAsync();
        RunVoiceEditSelfTest();
        await RunShortcutLeakSelfTestAsync();
        await RunSplitFocusSelfTestAsync();
        string? wavFile = Environment.GetEnvironmentVariable("WRAITHCAIRN_VOICE_SELFTEST_WAV") is { Length: > 0 } w ? w
            : source is not null && Directory.Exists(Path.Combine(source, "test_wavs"))
                ? Directory.GetFiles(Path.Combine(source, "test_wavs"), "*.wav").OrderBy(f => f).FirstOrDefault()
                : null;
        if (source is null || wavFile is null || !real.Files.All(f => File.Exists(Path.Combine(source, f.Name))))
        {
            Console.WriteLine("SELFTEST voice_dictate=skipped (set WRAITHCAIRN_VOICE_SELFTEST_MODEL / _WAV)");
            return;
        }

        await RunVoiceDictateSelfTestAsync(real, source, wavFile);
    }

    /// <summary>
    /// Window shortcuts must not also reach the program: TerminalControl
    /// encodes key-downs even when already handled, which used to leave
    /// "5;6u" / "[122;6u" at the prompt after Ctrl+Shift+I / K / Z.
    /// Captures the raw bytes a program receives while shortcuts are pressed.
    /// </summary>
    private async Task RunShortcutLeakSelfTestAsync()
    {
        if (OperatingSystem.IsWindows() || ActiveTab()?.Active is not { } pane)
        {
            Console.WriteLine("SELFTEST shortcut_keys_not_leaked=skipped");
            return;
        }

        string outFile = Path.Combine(Path.GetTempPath(), $"wraithcairn-leak-{Environment.ProcessId}.txt");
        pane.Focus();
        pane.SendInput($"stty raw -echo; timeout 3 cat > {outFile}; stty sane\n");
        await Task.Delay(800);
        foreach (Key key in new[] { Key.K, Key.Z, Key.Z, Key.OemPlus })
        {
            pane.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = KeyDownEvent,
                Key = key,
                PhysicalKey = key == Key.OemPlus ? PhysicalKey.Equal : (PhysicalKey)Enum.Parse(typeof(PhysicalKey), key.ToString()),
                KeySymbol = key == Key.OemPlus ? "+" : key.ToString(),
                KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
                Source = pane,
            });
            await Task.Delay(120);
        }

        // A plain key still gets through (sanity: the capture works).
        pane.SendInput("q");
        await Task.Delay(3500);
        string got = File.Exists(outFile) ? File.ReadAllText(outFile) : "(none)";
        File.Delete(outFile);
        Console.WriteLine($"SELFTEST shortcut_keys_not_leaked={got == "q"} got={System.Text.Json.JsonSerializer.Serialize(got)}");
    }

    /// <summary>
    /// After Ctrl+Shift+O / E (and zoom, close) the pane that should be
    /// active must own keyboard focus. Focus() on a freshly reparented pane
    /// used to return false, so typing went to the window menu instead.
    /// </summary>
    private async Task RunSplitFocusSelfTestAsync()
    {
        if (ActiveTab() is not { Active: { } start } tab)
        {
            Console.WriteLine("SELFTEST split_focus=skipped");
            return;
        }

        start.Focus();
        int before = tab.Nodes.Count;
        start.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = KeyDownEvent,
            Key = Key.O,
            PhysicalKey = PhysicalKey.O,
            KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
            Source = start,
        });
        for (int i = 0; i < 40 && tab.Nodes.Count == before; i++)
        {
            await Task.Delay(100);
        }

        await Task.Delay(400);
        object? focused = FocusManager?.GetFocusedElement();
        bool newPaneFocused = focused is TerminalControl t && !ReferenceEquals(t, start) && tab.Nodes.ContainsKey(t);

        // Close it again: focus must land back on a remaining pane.
        if (focused is TerminalControl created)
        {
            ClosePane(created);
        }

        await Task.Delay(400);
        bool afterClose = FocusManager?.GetFocusedElement() is TerminalControl survivor && tab.Nodes.ContainsKey(survivor);
        Console.WriteLine($"SELFTEST split_focus={newPaneFocused && afterClose} new_pane={newPaneFocused} after_close={afterClose}");
    }

    /// <summary>Small fake model (three files with known hashes) served over local HTTP.</summary>
    private async Task RunVoiceDownloadSelfTestAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"wraithcairn-voice-{Environment.ProcessId}");
        string served = Path.Combine(root, "served");
        string dest = Path.Combine(root, "models");
        Directory.CreateDirectory(served);
        Random rng = new(42);
        List<VoiceModel.ModelFile> files = [];
        foreach ((string name, int size) in new[] { ("encoder.onnx", 3_000_000), ("decoder.onnx", 400_000), ("tokens.txt", 5_000) })
        {
            byte[] data = new byte[size];
            rng.NextBytes(data);
            File.WriteAllBytes(Path.Combine(served, name), data);
            files.Add(new VoiceModel.ModelFile(name, size, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data))));
        }

        TimeSpan oldIdle = VoiceModel.IdleTimeout;
        Func<int, TimeSpan> oldDelay = VoiceModel.RetryDelay;
        VoiceModel.RetryDelay = _ => TimeSpan.FromMilliseconds(200);
        VoiceModel.IdleTimeout = TimeSpan.FromSeconds(5);
        FlakyServer server = new(served);
        try
        {
            // 1) First request for encoder.onnx dies half way: auto-retry resumes.
            server.Mode = FlakyServer.Fail.DropOnce;
            VoiceModel fake = new()
            {
                Id = "selftest-model",
                DisplayName = "selftest model",
                License = "test",
                BaseUrl = server.BaseUrl,
                Files = files,
            };
            VoiceModelDialog dialog = new(fake, dest, ChromeBrush(), autoStart: true);
            Task<bool> shown = dialog.ShowDialog<bool>(this);
            bool ok = await dialog.Attempt.WaitAsync(TimeSpan.FromSeconds(30));
            bool installed = await shown.WaitAsync(TimeSpan.FromSeconds(5));
            bool retried = dialog.StatusHistory.Any(s => s.Contains("Retrying", StringComparison.Ordinal));
            bool progressed = dialog.StatusHistory.Any(s => s.Contains(" of ", StringComparison.Ordinal));
            Console.WriteLine($"SELFTEST voice_download={ok && installed && fake.IsInstalled(dest)} retried={retried} "
                + $"resumed={server.RangeRequests > 0} progress={progressed} requests={server.Requests}");

            // 2) Server keeps failing: dialog gives up with Try Again; then it heals.
            Directory.Delete(dest, recursive: true);
            server.Mode = FlakyServer.Fail.DropAlways;
            server.RangeRequests = 0;
            VoiceModelDialog retry = new(fake, dest, ChromeBrush(), autoStart: true);
            Task<bool> retryShown = retry.ShowDialog<bool>(this);
            bool firstOk = await retry.Attempt.WaitAsync(TimeSpan.FromSeconds(30));
            bool offered = !firstOk && retry.PrimaryText == "Try Again" && retry.Headline == "Download failed";
            string partPath = Path.Combine(fake.DirIn(dest), "encoder.onnx.part");
            long partial = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            SnapshotControl(retry, "/tmp/wraithcairn-selftest-voice-failed.png");

            server.Mode = FlakyServer.Fail.None;
            _ = retry.StartAsync(); // replaces Attempt synchronously
            bool secondOk = await retry.Attempt.WaitAsync(TimeSpan.FromSeconds(30));
            bool closed = await retryShown.WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine($"SELFTEST voice_retry={offered && secondOk && closed && fake.IsInstalled(dest)} "
                + $"try_again_offered={offered} kept_partial={partial > 0} resumed={server.RangeRequests > 0}");

            // 3) Cancel mid-download keeps the partial file and reports not installed.
            Directory.Delete(dest, recursive: true);
            server.Mode = FlakyServer.Fail.Slow;
            VoiceModelDialog cancel = new(fake, dest, ChromeBrush(), autoStart: true);
            Task<bool> cancelShown = cancel.ShowDialog<bool>(this);
            for (int i = 0; i < 100 && cancel.ProgressValue <= 0; i++)
            {
                await Task.Delay(50);
            }

            SnapshotControl(cancel, "/tmp/wraithcairn-selftest-voice-download.png");
            cancel.Close(false);
            bool cancelResult = await cancelShown.WaitAsync(TimeSpan.FromSeconds(5));
            await cancel.Attempt.WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine($"SELFTEST voice_cancel={!cancelResult && !fake.IsInstalled(dest)} progress_seen={cancel.ProgressValue > 0}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SELFTEST voice_download=error {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            server.Dispose();
            VoiceModel.IdleTimeout = oldIdle;
            VoiceModel.RetryDelay = oldDelay;
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // temp
            }
        }
    }

    private async Task RunVoiceDictateSelfTestAsync(VoiceModel model, string sourceDir, string sample)
    {
        string? previousUrl = Environment.GetEnvironmentVariable("WRAITHCAIRN_VOICE_MODEL_URL");
        string? previousWav = Environment.GetEnvironmentVariable("WRAITHCAIRN_VOICE_TEST_WAV");
        string wav = Path.Combine(Path.GetTempPath(), $"wraithcairn-voice-{Environment.ProcessId}.wav");
        try
        {
            // Model: real files "downloaded" from the local folder into the configured dir.
            string modelsDir = Config.ResolvedVoiceModelsDir;
            if (!model.IsInstalled(modelsDir))
            {
                Environment.SetEnvironmentVariable("WRAITHCAIRN_VOICE_MODEL_URL", sourceDir);
                await model.DownloadAsync(modelsDir, null, CancellationToken.None);
            }

            // Spoken test phrase, played in real time as the "microphone".
            File.Copy(sample, wav, overwrite: true);
            Environment.SetEnvironmentVariable("WRAITHCAIRN_VOICE_TEST_WAV", wav);

            Tab? tab = ActiveTab();
            if (tab?.Active is not { } pane || !_panes.TryGetValue(pane, out PaneInfo? info))
            {
                Console.WriteLine("SELFTEST voice_dictate=False (no pane)");
                return;
            }

            pane.Focus();
            pane.SendInput("clear\n");
            await Task.Delay(500);
            s_voiceTyped.Clear();
            s_voiceKeys.Clear();
            s_voiceLiveSeen = false;
            bool liveBefore = false;
            static string Squash(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

            KeyEventArgs press = new()
            {
                RoutedEvent = KeyDownEvent,
                Key = Key.I,
                KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
                Source = pane,
            };
            pane.RaiseEvent(press);
            bool indicator = false;
            for (int i = 0; i < 50 && !indicator; i++)
            {
                await Task.Delay(100);
                indicator = info.Header.VoiceText.Length > 0;
            }

            // Wait for the phrase (WAV plays in real time, ~4 s). With live
            // typing, words must reach the prompt before the phrase is done.
            for (int i = 0; i < 150 && s_voiceTyped.Count == 0; i++)
            {
                await Task.Delay(100);
                liveBefore |= s_voiceTyped.Count == 0 && s_voiceLiveSeen
                    && s_voice?.Shown is { Length: > 2 } shown
                    && TryReadScreenText(pane, out string partialScreen)
                    && Squash(partialScreen).Contains(Squash(shown), StringComparison.Ordinal);
            }

            bool heard = info.Header.VoiceText.Contains('\u201c') || s_voiceTyped.Count > 0;
            SnapshotWindow("/tmp/wraithcairn-selftest-voice-dictate.png");

            // Stop with the same shortcut.
            pane.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = KeyDownEvent,
                Key = Key.I,
                KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
                Source = pane,
            });
            for (int i = 0; i < 50 && s_voice is not null; i++)
            {
                await Task.Delay(100);
            }

            await Task.Delay(400);
            string typed = string.Concat(s_voiceTyped);
            string screen = TryReadScreenText(pane, out string text) ? text : string.Empty;
            // Compare without whitespace: in a narrow window (CI's Xvfb) the
            // prompt plus phrase wraps, splitting it across screen rows.
            string typedSquashed = Squash(typed);
            bool onScreen = typedSquashed.Length > 0
                && Squash(screen).Contains(typedSquashed[..Math.Min(30, typedSquashed.Length)], StringComparison.Ordinal);
            bool expected = typed.Length > 10;
            bool notRun = info.RunningCommand is null && info.LastExitCode is null or 0
                && !screen.Contains("command not found", StringComparison.OrdinalIgnoreCase)
                && !screen.Contains("Unknown command", StringComparison.OrdinalIgnoreCase);
            bool stopped = s_voice is null && info.Header.VoiceText.Length == 0;
            // Replaying every key voice input sent (DEL = erase one) must give
            // exactly the finished text: corrections left nothing behind.
            bool editsClean = ApplyEditKeys(s_voiceKeys.ToString()) == typed;
            bool live = !Config.VoiceLiveTyping || liveBefore;
            Console.WriteLine($"SELFTEST voice_dictate={indicator && expected && onScreen && notRun && stopped && editsClean && live} indicator={indicator} "
                + $"heard={heard} on_screen={onScreen} no_enter={notRun} stopped={stopped} live_before_final={liveBefore} edits_clean={editsClean} "
                + $"ended=\"{s_voiceLastEnd}\" typed=\"{typed}\"");

            // Leave the prompt clean for later checks.
            pane.SendInput("\u0015");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SELFTEST voice_dictate=error {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WRAITHCAIRN_VOICE_MODEL_URL", previousUrl);
            Environment.SetEnvironmentVariable("WRAITHCAIRN_VOICE_TEST_WAV", previousWav);
            try
            {
                File.Delete(wav);
            }
            catch
            {
                // temp
            }
        }
    }

    /// <summary>What a line editor shows after these keys (DEL erases one code point).</summary>
    private static string ApplyEditKeys(string keys)
    {
        System.Text.StringBuilder line = new();
        foreach (char c in keys)
        {
            if (c == '\u007f')
            {
                if (line.Length > 0)
                {
                    int cut = line.Length > 1 && char.IsLowSurrogate(line[^1]) ? 2 : 1;
                    line.Length -= cut;
                }
            }
            else
            {
                line.Append(c);
            }
        }

        return line.ToString();
    }

    /// <summary>Live-typing edits: revisions erase only what changed and end at the wanted text.</summary>
    private static void RunVoiceEditSelfTest()
    {
        string[] steps = ["", "and so", "and so my", "and so my fellow", "and so my fellows", "and so my fellow americans", "and so", "", "caf\u00e9 \U0001F600", "caf\u00e9 \U0001F601"];
        bool ok = true;
        string shown = string.Empty;
        string keys = string.Empty;
        foreach (string step in steps)
        {
            keys += DictationSession.EditKeys(shown, step);
            ok &= ApplyEditKeys(keys) == step;
            shown = step;
        }

        bool minimal = DictationSession.EditKeys("and so my fellows", "and so my fellow americans") == "\u007f americans"
            && DictationSession.EditKeys("abc", "abc") == string.Empty
            && DictationSession.EditKeys("\U0001F600", "\U0001F601") == "\u007f\U0001F601";
        Console.WriteLine($"SELFTEST voice_live_edits={ok && minimal} replay={ok} minimal={minimal}");
    }

    /// <summary>
    /// Minimal HTTP/1.1 file server for the download selftest: supports Range
    /// requests and can drop connections half way through a file.
    /// </summary>
    private sealed class FlakyServer : IDisposable
    {
        public enum Fail { None, DropOnce, DropAlways, Slow }

        private readonly TcpListener _listener;
        private readonly string _root;
        private readonly CancellationTokenSource _cts = new();
        private bool _dropped;

        public Fail Mode { get; set; }

        public int Requests { get; private set; }

        public int RangeRequests { get; set; }

        public string BaseUrl { get; }

        public FlakyServer(string root)
        {
            _root = root;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    using StreamReader reader = new(stream, leaveOpen: true);
                    string? requestLine = await reader.ReadLineAsync();
                    long from = 0;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    {
                        if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                        {
                            from = long.Parse(line["Range: bytes=".Length..].TrimEnd('-').Split('-')[0]);
                        }
                    }

                    Requests++;
                    if (from > 0)
                    {
                        RangeRequests++;
                    }

                    string name = requestLine?.Split(' ')[1].TrimStart('/') ?? string.Empty;
                    string path = Path.Combine(_root, Path.GetFileName(name));
                    if (!File.Exists(path))
                    {
                        await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
                        return;
                    }

                    byte[] data = await File.ReadAllBytesAsync(path);
                    int length = data.Length - (int)from;
                    string head = from > 0
                        ? $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {from}-{data.Length - 1}/{data.Length}\r\n"
                        : "HTTP/1.1 200 OK\r\n";
                    head += $"Content-Length: {length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head));

                    bool drop = Mode == Fail.DropAlways || (Mode == Fail.DropOnce && !_dropped && name == "encoder.onnx");
                    int send = drop ? length / 2 : length;
                    int chunk = Mode == Fail.Slow ? 16 * 1024 : 256 * 1024;
                    for (int sent = 0; sent < send; sent += chunk)
                    {
                        await stream.WriteAsync(data.AsMemory((int)from + sent, Math.Min(chunk, send - sent)));
                        if (Mode == Fail.Slow)
                        {
                            await Task.Delay(30);
                        }
                    }

                    if (drop)
                    {
                        _dropped = true;
                        client.Client.LingerState = new LingerOption(true, 0); // RST, like a dropped link
                    }
                }
                catch
                {
                    // client went away
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }
}
