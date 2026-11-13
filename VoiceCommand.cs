namespace Wraithcairn;

/// <summary>
/// `wraithcairn voice ...`: headless helpers for voice input (no display).
///   voice --download        download the speech model now (progress on stderr)
///   voice --devices         list microphones OpenAL can open
///   voice --status          show where the model lives and whether it is installed
///   voice --transcribe F    transcribe a 16-bit PCM WAV file with the local model
///   voice --listen [SECS]   transcribe the microphone for a few seconds (default 5)
/// </summary>
internal static class VoiceCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            Console.WriteLine("""
                usage: wraithcairn voice [--status | --download | --devices | --transcribe FILE.wav | --listen [SECONDS]]

                  --status       where the speech model lives and whether it is installed
                  --download     download the speech model now (resumes, verifies SHA-256)
                  --devices      list microphones (use one in config.json "voiceInputDevice")
                  --transcribe   transcribe a 16-bit PCM WAV file, offline
                  --listen       transcribe the microphone for SECONDS (default 5), offline

                Voice input itself is Ctrl+Shift+I in a pane.
                """);
            return 0;
        }

        AppConfig config = AppConfig.Load(writeDefault: false);
        VoiceModel model = VoiceModel.Default;
        string modelsDir = config.ResolvedVoiceModelsDir;

        try
        {
            if (args.Contains("--status"))
            {
                Console.WriteLine($"model:     {model.DisplayName} ({VoiceModel.FormatBytes(model.TotalBytes)}, {model.License})");
                Console.WriteLine($"folder:    {model.DirIn(modelsDir)}");
                Console.WriteLine($"           (voiceModelsDir: {config.VoiceModelsDir ?? "default, next to config.json"})");
                Console.WriteLine($"installed: {(model.IsInstalled(modelsDir) ? "yes" : "no (downloads on first Ctrl+Shift+I, or run `wraithcairn voice --download`)")}");
                Console.WriteLine($"mic:       {config.VoiceInputDevice ?? "system default"}");
                return 0;
            }

            if (args.Contains("--devices"))
            {
                IReadOnlyList<string> devices = OpenAlCapture.Devices();
                string? fallback = OpenAlCapture.DefaultDevice();
                foreach (string device in devices)
                {
                    Console.WriteLine($"{(device == fallback ? "*" : " ")} {device}");
                }

                Console.Error.WriteLine(devices.Count == 0 ? "no capture devices found" : $"{devices.Count} device(s), * = default");
                return devices.Count == 0 ? 1 : 0;
            }

            if (args.Contains("--download"))
            {
                return await DownloadAsync(model, modelsDir) ? 0 : 1;
            }

            int t = Array.IndexOf(args, "--transcribe");
            if (t >= 0)
            {
                if (t + 1 >= args.Length)
                {
                    Console.Error.WriteLine("usage: wraithcairn voice --transcribe FILE.wav");
                    return 2;
                }

                return await TranscribeAsync(model, modelsDir, new WavFileSource(args[t + 1]), TimeSpan.Zero);
            }

            int l = Array.IndexOf(args, "--listen");
            if (l >= 0)
            {
                double seconds = l + 1 < args.Length && double.TryParse(args[l + 1], System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 5;
                OpenAlCapture mic = new(config.VoiceInputDevice, VoiceEngine.SampleRate);
                Console.Error.WriteLine($"listening on \"{mic.Name}\" for {seconds:0.#}s\u2026");
                return await TranscribeAsync(model, modelsDir, mic, TimeSpan.FromSeconds(seconds));
            }

            Console.Error.WriteLine($"wraithcairn voice: unknown option {args[0]} (try --help)");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"wraithcairn voice: {ex.Message}");
            return 1;
        }
    }

    private static async Task<bool> DownloadAsync(VoiceModel model, string modelsDir)
    {
        if (model.IsInstalled(modelsDir))
        {
            Console.WriteLine($"already installed: {model.DirIn(modelsDir)}");
            return true;
        }

        Console.Error.WriteLine($"downloading {model.DisplayName} ({VoiceModel.FormatBytes(model.TotalBytes)}) to {model.DirIn(modelsDir)}");
        int lastPercent = -1;
        Progress<VoiceModel.Progress> progress = new(p =>
        {
            if (p.Note is { } note)
            {
                Console.Error.WriteLine(note);
                return;
            }

            int percent = p.Total > 0 ? (int)(p.Done * 100 / p.Total) : 0;
            if (percent != lastPercent && (percent % 10 == 0 || percent == 100))
            {
                lastPercent = percent;
                Console.Error.WriteLine($"{percent,3}%  {VoiceModel.FormatBytes(p.Done)} of {VoiceModel.FormatBytes(p.Total)}");
            }
        });
        await model.DownloadAsync(modelsDir, progress, CancellationToken.None);
        await Task.Delay(50);
        Console.WriteLine($"installed: {model.DirIn(modelsDir)}");
        return true;
    }

    private static async Task<int> TranscribeAsync(VoiceModel model, string modelsDir, IAudioSource source, TimeSpan duration)
    {
        if (!model.IsInstalled(modelsDir) && !await DownloadAsync(model, modelsDir))
        {
            return 1;
        }

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        Task<SherpaOnnx.OnlineRecognizer> recognizer = VoiceEngine.GetAsync(model, model.DirIn(modelsDir));
        using DictationSession session = new(source, recognizer, TimeSpan.Zero);
        List<string> phrases = [];
        Exception? failure = null;
        session.Final += phrases.Add;
        session.Failed += ex => failure = ex;
        session.Start();
        if (duration > TimeSpan.Zero)
        {
            await Task.Delay(duration);
            session.Stop();
        }

        await session.Completion;
        VoiceEngine.Release();
        if (failure is not null)
        {
            throw failure;
        }

        Console.WriteLine(string.Join(" ", phrases));
        Console.Error.WriteLine($"({clock.Elapsed.TotalSeconds:0.0}s incl. model load)");
        return 0;
    }
}
