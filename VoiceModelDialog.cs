using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Wraithcairn;

/// <summary>
/// First-use download of the speech model for voice input: says what will be
/// downloaded (size, license, destination), then shows a progress bar with
/// speed and time left. Transient errors retry on their own (shown inline);
/// a hard failure offers Try Again, which resumes the partial files. Cancel
/// stops at any point and keeps what was downloaded for next time.
/// Closes with true once the model is installed.
/// </summary>
public sealed class VoiceModelDialog : Window
{
    private readonly VoiceModel _model;
    private readonly string _modelsDir;
    private readonly TextBlock _headline;
    private readonly TextBlock _detail;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _primary;
    private readonly Button _close;
    private CancellationTokenSource? _cts;
    private bool _done;

    /// <summary>Headline text (selftest).</summary>
    public string Headline => _headline.Text ?? string.Empty;

    /// <summary>Status line under the bar (selftest).</summary>
    public string StatusText => _status.Text ?? string.Empty;

    public string PrimaryText => _primary.IsVisible ? _primary.Content as string ?? string.Empty : string.Empty;

    public double ProgressValue => _progress.Value;

    /// <summary>Every status line shown, in order (selftest).</summary>
    public List<string> StatusHistory { get; } = [];

    /// <summary>Completes when a download attempt finishes (success or failure).</summary>
    public Task<bool> Attempt => _attempt.Task;

    private TaskCompletionSource<bool> _attempt = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public VoiceModelDialog(VoiceModel model, string modelsDir, IBrush? background, bool autoStart = false)
    {
        _model = model;
        _modelsDir = modelsDir;
        Title = "Voice Input";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppInfo.WindowIcon;
        if (background is not null)
        {
            Background = background;
        }

        _headline = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Text = "Download the speech model?",
        };
        _detail = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Text = "Voice input runs entirely on this computer. It needs a one-time download of "
                + $"the {model.DisplayName} ({VoiceModel.FormatBytes(model.TotalBytes)}, {model.License}). "
                + "After that, nothing you say leaves this machine.\n\n"
                + $"Saved to: {model.DirIn(modelsDir)}\n"
                + "(change with \"voiceModelsDir\" in config.json)",
        };
        _progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Height = 6,
            MinWidth = 460,
            IsVisible = false,
        };
        _status = new TextBlock
        {
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            IsVisible = false,
        };

        _primary = new Button { Content = "Download", IsDefault = true, MinWidth = 110 };
        _close = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        _primary.Click += async (_, _) => await StartAsync();
        _close.Click += (_, _) => Close(_done);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_primary);
        buttons.Children.Add(_close);

        StackPanel body = new() { Margin = new Thickness(20), Spacing = 10, MinWidth = 460 };
        body.Children.Add(_headline);
        body.Children.Add(_detail);
        body.Children.Add(_progress);
        body.Children.Add(_status);
        body.Children.Add(buttons);
        Content = body;

        Opened += async (_, _) =>
        {
            if (autoStart)
            {
                await StartAsync();
            }
            else
            {
                _primary.Focus();
            }
        };
        Closing += (_, _) => _cts?.Cancel();
    }

    /// <summary>Starts (or resumes) the download. Public for the selftest.</summary>
    public async Task StartAsync()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        if (_attempt.Task.IsCompleted)
        {
            _attempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _headline.Text = "Downloading the speech model\u2026";
        _primary.IsVisible = false;
        _close.Content = "Cancel";
        _progress.IsVisible = true;
        _progress.IsIndeterminate = false;
        _status.IsVisible = true;
        SetStatus("Connecting\u2026");

        Progress<VoiceModel.Progress> progress = new(p =>
        {
            _progress.Value = p.Total > 0 ? (double)p.Done / p.Total : 0;
            if (p.Note is { } note)
            {
                SetStatus(note);
                return;
            }

            string line = $"{VoiceModel.FormatBytes(p.Done)} of {VoiceModel.FormatBytes(p.Total)}";
            if (p.BytesPerSecond > 1)
            {
                double left = (p.Total - p.Done) / p.BytesPerSecond;
                line += $"  \u00b7  {VoiceModel.FormatBytes((long)p.BytesPerSecond)}/s  \u00b7  {FormatEta(left)} left";
            }

            SetStatus(line);
        });

        bool ok = false;
        try
        {
            await Task.Run(() => _model.DownloadAsync(_modelsDir, progress, _cts.Token));
            ok = true;
            _done = true;
            _progress.Value = 1;
            _headline.Text = "Speech model ready";
            SetStatus($"Installed in {_model.DirIn(_modelsDir)}");
            _close.Content = "Close";
        }
        catch (OperationCanceledException)
        {
            // window closed / cancelled
        }
        catch (Exception ex)
        {
            _headline.Text = "Download failed";
            SetStatus($"{ex.Message}\nPartial files are kept; Try Again resumes where it stopped.");
            _primary.Content = "Try Again";
            _primary.IsVisible = true;
            _close.Content = "Close";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _attempt.TrySetResult(ok);
        }

        if (ok)
        {
            // Brief "ready" moment, then hand straight back to dictation.
            await Task.Delay(600);
            Close(true);
        }
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        if (StatusHistory.Count == 0 || StatusHistory[^1] != text)
        {
            StatusHistory.Add(text);
        }
    }

    private static string FormatEta(double seconds) => seconds switch
    {
        < 1 => "<1s",
        < 90 => $"{seconds:0}s",
        < 3600 => $"{seconds / 60:0}m {seconds % 60:00}s",
        _ => $"{seconds / 3600:0}h {seconds % 3600 / 60:00}m",
    };
}
