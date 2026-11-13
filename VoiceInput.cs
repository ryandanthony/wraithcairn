using SherpaOnnx;

namespace Wraithcairn;

/// <summary>
/// Local streaming speech recognition (sherpa-onnx). One recognizer is
/// loaded on first use and kept for a few minutes after the last dictation
/// so toggling voice input again is instant; then it is freed (~190 MB).
/// </summary>
public static class VoiceEngine
{
    private static readonly object Gate = new();
    private static Task<OnlineRecognizer>? s_loading;
    private static string? s_loadedFrom;
    private static Timer? s_release;
    private static int s_users;

    /// <summary>How long an idle recognizer stays loaded.</summary>
    public static TimeSpan KeepLoaded { get; set; } = TimeSpan.FromMinutes(5);

    public static int SampleRate => 16000;

    /// <summary>
    /// Loads (or reuses) the recognizer for a model folder, off the UI thread.
    /// Every call must be paired with one <see cref="Release"/>.
    /// </summary>
    public static Task<OnlineRecognizer> GetAsync(VoiceModel model, string modelDir)
    {
        lock (Gate)
        {
            s_users++;
            s_release?.Dispose();
            s_release = null;
            if (s_loading is not null && s_loadedFrom == modelDir && !s_loading.IsFaulted)
            {
                return s_loading;
            }

            // A different folder (config changed) while another session still
            // uses the old recognizer: leak-free only once nobody uses it.
            if (s_users <= 1)
            {
                DisposeLoaded();
            }

            s_loadedFrom = modelDir;
            s_loading = Task.Run(() => Create(model, modelDir));
            return s_loading;
        }
    }

    /// <summary>
    /// One user is done. When nobody uses the recognizer any more, it is
    /// freed after <see cref="KeepLoaded"/> (unless someone needs it again).
    /// </summary>
    public static void Release()
    {
        lock (Gate)
        {
            s_users = Math.Max(0, s_users - 1);
            if (s_users > 0)
            {
                return;
            }

            s_release?.Dispose();
            s_release = new Timer(_ =>
            {
                lock (Gate)
                {
                    if (s_users == 0)
                    {
                        DisposeLoaded();
                    }
                }
            }, null, KeepLoaded, Timeout.InfiniteTimeSpan);
        }
    }

    private static void DisposeLoaded()
    {
        if (s_loading is { IsCompletedSuccessfully: true } done)
        {
            done.Result.Dispose();
        }

        s_loading = null;
        s_loadedFrom = null;
    }

    private static OnlineRecognizer Create(VoiceModel model, string dir)
    {
        foreach (VoiceModel.ModelFile file in model.Files)
        {
            if (!File.Exists(Path.Combine(dir, file.Name)))
            {
                throw new FileNotFoundException($"Speech model file missing: {Path.Combine(dir, file.Name)}");
            }
        }

        OnlineRecognizerConfig config = new();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Path.Combine(dir, model.Encoder);
        config.ModelConfig.Transducer.Decoder = Path.Combine(dir, model.Decoder);
        config.ModelConfig.Transducer.Joiner = Path.Combine(dir, model.Joiner);
        config.ModelConfig.Tokens = Path.Combine(dir, model.Tokens);
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 2);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        // Endpointing splits dictation into phrases, each typed as it ends:
        //   rule 1: nothing said yet, 2.4 s of silence -> reset
        //   rule 2: after speech, 0.8 s of silence -> phrase done
        //   rule 3: force a break after 20 s of continuous speech
        config.EnableEndpoint = 1;
        config.Rule1MinTrailingSilence = 2.4f;
        config.Rule2MinTrailingSilence = 0.8f;
        config.Rule3MinUtteranceLength = 20f;
        return new OnlineRecognizer(config);
    }
}

/// <summary>
/// One dictation run: pulls audio from a source, streams it through the
/// recognizer, reports the in-progress phrase (<see cref="Partial"/>) and
/// each finished phrase (<see cref="Final"/>). Events fire on a background
/// thread. Audio captured while the model is still loading is buffered, so
/// speech right after the key press is not lost.
/// </summary>
public sealed class DictationSession : IDisposable
{
    private readonly IAudioSource _source;
    private readonly Task<OnlineRecognizer> _recognizer;
    private readonly TimeSpan _idleStop;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <summary>In-progress (not yet final) text of the current phrase; empty between phrases.</summary>
    public event Action<string>? Partial;

    /// <summary>A finished phrase, ready to type.</summary>
    public event Action<string>? Final;

    /// <summary>The model failed to load or audio capture broke.</summary>
    public event Action<Exception>? Failed;

    /// <summary>The session is over (stopped, input ended, idle timeout or failure). Arg: reason.</summary>
    public event Action<string>? Ended;

    /// <summary>True once the recognizer is loaded and decoding.</summary>
    public bool Ready => _recognizer.IsCompletedSuccessfully;

    public string DeviceName => _source.Name;

    /// <param name="idleStop">Stop after this long without speech (Zero = never).</param>
    public DictationSession(IAudioSource source, Task<OnlineRecognizer> recognizer, TimeSpan idleStop)
    {
        _source = source;
        _recognizer = recognizer;
        _idleStop = idleStop;
    }

    public void Start()
    {
        _source.Start();
        _loop = Task.Factory.StartNew(Run, TaskCreationOptions.LongRunning);
    }

    /// <summary>Stops listening; the last phrase is finished and reported before <see cref="Ended"/>.</summary>
    public void Stop() => _stop.Cancel();

    /// <summary>Waits for the session to wind down (tests).</summary>
    public Task Completion => _loop ?? Task.CompletedTask;

    private void Run()
    {
        string reason = "stopped";
        List<float[]> pending = [];
        long pendingSamples = 0;
        OnlineStream? stream = null;
        OnlineRecognizer? recognizer = null;
        string lastPartial = string.Empty;
        DateTime lastSpeech = DateTime.UtcNow;
        float[] buffer = new float[_source.SampleRate];
        int rate = _source.SampleRate;

        void Decode()
        {
            while (recognizer!.IsReady(stream!))
            {
                recognizer.Decode(stream!);
            }

            string text = recognizer.GetResult(stream!).Text.Trim();
            if (text != lastPartial)
            {
                lastPartial = text;
                if (text.Length > 0)
                {
                    lastSpeech = DateTime.UtcNow;
                }

                Partial?.Invoke(text);
            }

            if (recognizer.IsEndpoint(stream!))
            {
                if (text.Length > 0)
                {
                    Final?.Invoke(text);
                }

                recognizer.Reset(stream!);
                if (lastPartial.Length > 0)
                {
                    lastPartial = string.Empty;
                    Partial?.Invoke(string.Empty);
                }
            }
        }

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int n = _source.Read(buffer);
                if (n < 0)
                {
                    reason = "end of input";
                    break;
                }

                if (n > 0)
                {
                    float[] chunk = buffer[..n];
                    if (stream is null)
                    {
                        pending.Add(chunk);
                        pendingSamples += n;
                        // Cap the pre-load buffer at 30 s.
                        while (pendingSamples > rate * 30L && pending.Count > 1)
                        {
                            pendingSamples -= pending[0].Length;
                            pending.RemoveAt(0);
                        }
                    }
                    else
                    {
                        stream.AcceptWaveform(rate, chunk);
                    }
                }

                if (stream is null && _recognizer.IsCompleted)
                {
                    recognizer = _recognizer.GetAwaiter().GetResult(); // throws if loading failed
                    stream = recognizer.CreateStream();
                    foreach (float[] early in pending)
                    {
                        stream.AcceptWaveform(rate, early);
                    }

                    pending.Clear();
                    lastSpeech = DateTime.UtcNow;
                }

                if (stream is not null)
                {
                    Decode();
                    if (_idleStop > TimeSpan.Zero && lastPartial.Length == 0 && DateTime.UtcNow - lastSpeech > _idleStop)
                    {
                        reason = $"stopped after {_idleStop.TotalSeconds:0}s of silence";
                        break;
                    }
                }

                if (n == 0)
                {
                    Thread.Sleep(40);
                }
            }

            // Finish the phrase in progress, including audio captured during load.
            _source.Stop();
            recognizer ??= _recognizer.GetAwaiter().GetResult();
            if (stream is null)
            {
                stream = recognizer.CreateStream();
                foreach (float[] early in pending)
                {
                    stream.AcceptWaveform(rate, early);
                }
            }

            stream.AcceptWaveform(rate, new float[rate / 2]);
            stream.InputFinished();
            while (recognizer.IsReady(stream))
            {
                recognizer.Decode(stream);
            }

            string tail = recognizer.GetResult(stream).Text.Trim();
            if (tail.Length > 0)
            {
                Final?.Invoke(tail);
            }

            Partial?.Invoke(string.Empty);
        }
        catch (Exception ex)
        {
            reason = "failed";
            Failed?.Invoke(ex);
        }
        finally
        {
            stream?.Dispose();
            try
            {
                _source.Stop();
            }
            catch
            {
                // already broken
            }

            Ended?.Invoke(reason);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // reported via Failed
        }

        _source.Dispose();
        _stop.Dispose();
    }

    /// <summary>
    /// Keys that turn <paramref name="shown"/> (already typed at the cursor)
    /// into <paramref name="wanted"/>: one DEL (what Backspace sends) per
    /// character after the common prefix, then the new tail. Counts
    /// characters as code points, the way a shell's line editor deletes.
    /// </summary>
    public static string EditKeys(string shown, string wanted)
    {
        int common = 0;
        while (common < shown.Length && common < wanted.Length && shown[common] == wanted[common])
        {
            common++;
        }

        // Never split a surrogate pair.
        if (common > 0 && char.IsHighSurrogate(shown[common - 1]))
        {
            common--;
        }

        int erase = 0;
        for (int i = common; i < shown.Length; i++)
        {
            if (!char.IsLowSurrogate(shown[i]))
            {
                erase++;
            }
        }

        return new string('\u007f', erase) + wanted[common..];
    }

    /// <summary>
    /// Makes recognized text safe to type into a terminal: no control
    /// characters (so nothing can press Enter or send escape sequences),
    /// whitespace collapsed.
    /// </summary>
    public static string Sanitize(string text)
    {
        System.Text.StringBuilder sb = new(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
