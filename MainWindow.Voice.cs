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
/// the pane header shows a red mic with what is being heard, and every
/// finished phrase (a short pause ends one) is typed into that pane as if
/// pasted, never with Enter, so nothing runs until you press it. The same
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
        public string? StopReason { get; set; }
    }

    private static VoiceState? s_voice;
    private static bool s_voiceStarting;

    /// <summary>Everything typed by voice input (selftest).</summary>
    private static readonly List<string> s_voiceTyped = [];

    /// <summary>Last reason a dictation ended (selftest / status).</summary>
    private static string? s_voiceLastEnd;

    private bool IsDictatingInto(TerminalControl terminal) => ReferenceEquals(s_voice?.Target, terminal);

    /// <summary>Toggles voice input for a pane (default: the active one).</summary>
    private async Task ToggleVoiceInputAsync(TerminalControl? target = null)
    {
        target ??= ActiveTab()?.Active;
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
        VoiceState state = new() { Owner = this, Target = target, Session = session };
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
            s_voiceLastEnd = state.StopReason ?? reason;
            if (ReferenceEquals(s_voice, state))
            {
                s_voice = null;
            }

            if (_panes.TryGetValue(target, out PaneInfo? ended))
            {
                ended.Header.SetVoice(null);
            }

            session.Dispose();
            VoiceEngine.Release();
        });

        session.Start();
    }

    /// <summary>Types one recognized phrase into the dictation's pane (no Enter).</summary>
    private static void TypeDictated(VoiceState state, string text)
    {
        string clean = DictationSession.Sanitize(text);
        if (clean.Length == 0 || state.Target.Parent is null)
        {
            return;
        }

        // Phrases after the first are separated by a space.
        string typed = (state.TypedAny ? " " : string.Empty) + clean;
        state.TypedAny = true;
        state.Target.SendInput(typed);
        s_voiceTyped.Add(typed);
    }

    /// <summary>Stops the current dictation (any pane, any window). Null reason = quiet.</summary>
    private void StopVoiceInput(string? reason)
    {
        if (s_voice is not { } state)
        {
            return;
        }

        state.StopReason ??= reason;
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
