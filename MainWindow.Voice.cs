using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RoyalTerminal.Avalonia.Controls;

namespace Wraithcairn;

/// <summary>
/// Voice input (Ctrl+Shift+I, Pane > Voice Input, pane context menu):
/// dictate into one pane, fully offline.
///
/// The first use downloads the speech model (VoiceModelDialog: progress,
/// automatic retries, Try Again that resumes). Then the microphone opens,
/// the pane header shows a red mic, and words are typed into that pane as
/// they are recognized (voiceLiveTyping): when the recognizer revises the
/// phrase in progress, the changed tail is backspaced and retyped, and a
/// short pause finishes the phrase. Never with Enter, so nothing runs until
/// you press it. With voiceLiveTyping off, each phrase is typed only once it
/// is finished. The same
/// shortcut stops; it also stops on its own after voiceIdleStopSeconds of
/// silence, or when the pane closes. One pane listens at a time (there is
/// one microphone): starting it elsewhere moves it.
/// </summary>
public sealed partial class MainWindow
{
    private sealed class VoiceState
    {
        public required MainWindow Owner { get; init; }
        public required TerminalControl Target { get; init; }
        public required DictationSession Session { get; init; }
        public bool TypedAny { get; set; }

        /// <summary>Text typed for the phrase still in progress (live typing), separator included.</summary>
        public string Shown { get; set; } = string.Empty;

        public bool Live { get; init; }
        public string? StopReason { get; set; }
    }

    private static VoiceState? s_voice;
    private static bool s_voiceStarting;

    /// <summary>Everything typed by voice input (selftest).</summary>
    private static readonly List<string> s_voiceTyped = [];

    /// <summary>Every key sequence voice input sent, edits included (selftest).</summary>
    private static readonly System.Text.StringBuilder s_voiceKeys = new();

    /// <summary>Words reached the pane before their phrase was finished (selftest).</summary>
    private static bool s_voiceLiveSeen;

    /// <summary>Last reason a dictation ended (selftest / status).</summary>
    private static string? s_voiceLastEnd;

    private bool IsDictatingInto(TerminalControl terminal) => ReferenceEquals(s_voice?.Target, terminal);

    /// <summary>Toggles voice input for a pane (default: the active one).</summary>
    private async Task ToggleVoiceInputAsync(TerminalControl? target = null)
    {
        target ??= ActiveTab()?.Active;
        s_voiceToggles++;
        VoiceTrace($"toggle target={(target is null ? "null" : target.GetHashCode())} starting={s_voiceStarting} "
            + $"current={(s_voice is null ? "none" : s_voice.Target.GetHashCode())}");
        if (target is null || s_voiceStarting)
        {
            return;
        }

        if (s_voice is { } current)
        {
            bool samePane = ReferenceEquals(current.Target, target);
            current.Owner.StopVoiceInput("stopped");
            if (samePane)
            {
                return;
            }
        }

        s_voiceStarting = true;
        try
        {
            await StartVoiceInputAsync(target);
        }
        finally
        {
            s_voiceStarting = false;
        }
    }

    private async Task StartVoiceInputAsync(TerminalControl target)
    {
        VoiceModel model = VoiceModel.Default;
        string modelsDir = Config.ResolvedVoiceModelsDir;
        if (!model.IsInstalled(modelsDir))
        {
            VoiceModelDialog dialog = new(model, modelsDir, ChromeBrush());
            bool installed = await dialog.ShowDialog<bool>(this);
            target.Focus();
            if (!installed || !model.IsInstalled(modelsDir) || !_panes.ContainsKey(target))
            {
                return;
            }
        }

        IAudioSource source;
        try
        {
            source = OpenVoiceSource();
        }
        catch (Exception ex)
        {
            await ShowVoiceErrorAsync("Can't use the microphone", MicrophoneHelp(ex));
            target.Focus();
            return;
        }

        if (!_panes.TryGetValue(target, out PaneInfo? info))
        {
            source.Dispose();
            return;
        }

        Task<SherpaOnnx.OnlineRecognizer> recognizer = VoiceEngine.GetAsync(model, model.DirIn(modelsDir));
        TimeSpan idle = Config.VoiceIdleStopSeconds > 0 ? TimeSpan.FromSeconds(Config.VoiceIdleStopSeconds) : TimeSpan.Zero;
        DictationSession session = new(source, recognizer, idle);
        VoiceState state = new() { Owner = this, Target = target, Session = session, Live = Config.VoiceLiveTyping };
        s_voice = state;

        info.Header.SetVoice(recognizer.IsCompletedSuccessfully ? "listening\u2026" : "loading speech model\u2026");
        _ = recognizer.ContinueWith(
            t => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(s_voice, state) && t.IsCompletedSuccessfully)
                {
                    info.Header.SetVoice("listening\u2026");
                }
            }),
            TaskScheduler.Default);

        session.Partial += text => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(s_voice, state) && session.Ready)
            {
                info.Header.SetVoice(text.Length == 0 ? "listening\u2026" : $"\u201c{Shorten(text, 48)}\u201d");
            }

            if (state.Live)
            {
                s_voiceLiveSeen |= ShowDictated(state, text) && text.Length > 0;
            }
        });
        session.Final += text => Dispatcher.UIThread.Post(() => TypeDictated(state, text));
        session.Failed += ex => Dispatcher.UIThread.Post(async () =>
        {
            state.StopReason = "failed";
            string message = recognizer.IsFaulted
                ? $"The speech model could not be loaded: {ex.Message}\n\nIf the files are damaged, delete {model.DirIn(modelsDir)} and press Ctrl+Shift+I to download it again."
                : ex.Message;
            await ShowVoiceErrorAsync("Voice input stopped", message);
        });
        session.Ended += reason => Dispatcher.UIThread.Post(() =>
        {
            VoiceTrace($"ended reason={reason}");
            s_voiceLastEnd = state.StopReason ?? reason;
            if (ReferenceEquals(s_voice, state))
            {
                s_voice = null;
            }

            if (_panes.TryGetValue(target, out PaneInfo? ended))
            {
                ended.Header.SetVoice(null);
                // Tests (scripts/e2e-voice.sh): the window as the app drew it
                // once dictation ended. X server captures can lag frames.
                if (Environment.GetEnvironmentVariable("WRAITHCAIRN_VOICE_SNAPSHOT") is { Length: > 0 } snap)
                {
                    DispatcherTimer.RunOnce(() => SnapshotWindow(snap), TimeSpan.FromSeconds(1));
                }
            }

            session.Dispose();
            VoiceEngine.Release();
        });

        session.Start();
    }

    /// <summary>Finishes one recognized phrase in the dictation's pane (no Enter).</summary>
    private static void TypeDictated(VoiceState state, string text)
    {
        ShowDictated(state, text);
        if (state.Shown.Length > 0)
        {
            s_voiceTyped.Add(state.Shown);
            state.TypedAny = true;
        }

        state.Shown = string.Empty;
    }

    /// <summary>
    /// Makes the pane show <paramref name="text"/> as the phrase in progress:
    /// keeps what already matches, backspaces the rest and types the new
    /// tail. Empty text takes the phrase back off the line.
    /// </summary>
    private static bool ShowDictated(VoiceState state, string text)
    {
        if (state.Target.Parent is null)
        {
            return false;
        }

        string clean = DictationSession.Sanitize(text);
        // Phrases after the first are separated by a space.
        string wanted = clean.Length == 0 ? string.Empty : (state.TypedAny ? " " : string.Empty) + clean;
        string edit = DictationSession.EditKeys(state.Shown, wanted);
        state.Shown = wanted;
        if (edit.Length == 0)
        {
            return false;
        }

        state.Target.SendInput(edit);
        s_voiceKeys.Append(edit);
        return true;
    }

    private static void VoiceTrace(string message)
    {
        if (Environment.GetEnvironmentVariable("WRAITHCAIRN_TRACE") == "1")
        {
            Console.WriteLine($"TRACE voice {message}");
        }
    }

    /// <summary>Stops the current dictation (any pane, any window). Null reason = quiet.</summary>
    private void StopVoiceInput(string? reason)
    {
        if (s_voice is not { } state)
        {
            return;
        }

        state.StopReason ??= reason;
        VoiceTrace($"stop reason={reason}");
        if (state.Owner._panes.TryGetValue(state.Target, out PaneInfo? info))
        {
            info.Header.SetVoice("finishing\u2026");
        }

        state.Session.Stop();
    }

    private void StopVoiceInputFor(TerminalControl terminal)
    {
        if (IsDictatingInto(terminal))
        {
            StopVoiceInput("pane closed");
        }
    }

    /// <summary>
    /// The microphone, or for tests a WAV file played in real time
    /// (WRAITHCAIRN_VOICE_TEST_WAV).
    /// </summary>
    private static IAudioSource OpenVoiceSource()
    {
        if (Environment.GetEnvironmentVariable("WRAITHCAIRN_VOICE_TEST_WAV") is { Length: > 0 } wav)
        {
            return new WavFileSource(wav, realTime: true, endless: true);
        }

        return new OpenAlCapture(Config.VoiceInputDevice, VoiceEngine.SampleRate);
    }

    private static string MicrophoneHelp(Exception ex)
    {
        string message = ex is DllNotFoundException or EntryPointNotFoundException
            ? "The audio library (OpenAL Soft) could not be loaded."
            : ex.Message;
        string hint = OperatingSystem.IsMacOS()
            ? "Check System Settings > Privacy & Security > Microphone and allow wraithcairn."
            : OperatingSystem.IsWindows()
                ? "Check Settings > Privacy & security > Microphone, and that a microphone is connected."
                : "Check that a microphone is connected and not muted (pavucontrol / wpctl status).";
        return $"{message}\n\n{hint}\n\nSet \"voiceInputDevice\" in config.json to pick a specific microphone "
            + "(run `wraithcairn voice --devices` to list them).";
    }

    private async Task ShowVoiceErrorAsync(string title, string message)
    {
        if (Environment.GetCommandLineArgs().Contains("--selftest"))
        {
            Console.WriteLine($"VOICE error: {title}: {message.Split('\n')[0]}");
            return;
        }

        Button ok = new() { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right };
        StackPanel body = new() { Margin = new Thickness(20), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
        body.Children.Add(ok);
        Window dialog = new()
        {
            Title = "Voice Input",
            Content = body,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ChromeBrush(),
            Icon = AppInfo.WindowIcon,
        };
        ok.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        ActiveTab()?.Active?.Focus();
    }
}
