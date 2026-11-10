using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Wraithcairn;

/// <summary>
/// Borderless splash shown while the saved workspace is read and its panes
/// start: logo, version, a status line and a progress bar. The main window
/// drives it through <see cref="Report"/> and closes it via
/// <see cref="CloseWhenReadyAsync"/> once restore is done.
/// </summary>
public sealed class SplashWindow : Window
{
    /// <summary>Keep the splash up at least this long so it doesn't just flash.</summary>
    private static readonly TimeSpan MinimumVisible = TimeSpan.FromMilliseconds(700);

    private static readonly Color Background0 = Color.Parse("#14191f");
    private static readonly Color Accent = Color.Parse("#5FD9BD");
    private static readonly Color Muted = Color.Parse("#8A9BAC");

    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly DateTime _shownAt = DateTime.UtcNow;
    private bool _closing;

    /// <summary>Status lines reported so far (for the selftest).</summary>
    public List<string> History { get; } = [];

    public SplashWindow()
    {
        Title = "wraithcairn";
        Icon = AppInfo.WindowIcon;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 560;
        Height = 230;
        Background = new SolidColorBrush(Background0);

        StackPanel body = new()
        {
            Margin = new Thickness(28, 24),
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // logo.png has an opaque white background, so compose the mark from the
        // (dark-tile) app icon plus a text wordmark in the logo's colours.
        StackPanel brand = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        if (LoadBitmap("icon-256.png") is { } icon)
        {
            // The PNG has no alpha; clip its white corners to the rounded tile.
            brand.Children.Add(new Border
            {
                Width = 92,
                Height = 92,
                CornerRadius = new CornerRadius(19),
                ClipToBounds = true,
                Child = new Image { Source = icon, Width = 92, Height = 92, Stretch = Stretch.UniformToFill },
            });
        }

        FontFamily mono = new("JetBrains Mono, JetBrainsMono NFM, Cascadia Mono, DejaVu Sans Mono, monospace");
        TextBlock word = new() { FontFamily = mono, FontSize = 40, FontWeight = FontWeight.Bold };
        word.Inlines!.Add(new Avalonia.Controls.Documents.Run("wraith") { Foreground = new SolidColorBrush(Color.Parse("#C9D6E1")) });
        word.Inlines.Add(new Avalonia.Controls.Documents.Run("cairn") { Foreground = new SolidColorBrush(Accent) });
        StackPanel words = new() { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        words.Children.Add(word);
        words.Children.Add(new TextBlock
        {
            Text = "YOUR SHELLS, RISEN",
            FontFamily = mono,
            FontSize = 13,
            LetterSpacing = 4,
            Foreground = new SolidColorBrush(Color.Parse("#5E7082")),
        });
        brand.Children.Add(words);
        body.Children.Add(brand);

        _progress = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 4,
            MinHeight = 4,
            Foreground = new SolidColorBrush(Accent),
            Background = new SolidColorBrush(Color.Parse("#26303a")),
            Margin = new Thickness(8, 4, 8, 0),
        };
        body.Children.Add(_progress);

        _status = new TextBlock
        {
            Text = "starting…",
            FontSize = 12,
            Foreground = new SolidColorBrush(Muted),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        Grid footer = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 0) };
        footer.Children.Add(_status);
        TextBlock version = new()
        {
            Text = $"v{AppInfo.Version}",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#56677a")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(version, 1);
        footer.Children.Add(version);
        body.Children.Add(footer);

        Content = new Border
        {
            Child = body,
            BorderBrush = new SolidColorBrush(Color.Parse("#2b3640")),
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>Updates the status line; with a total, shows determinate progress.</summary>
    public void Report(string status, int done = 0, int total = 0)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Report(status, done, total));
            return;
        }

        History.Add(status);
        _status.Text = status;
        if (total > 0)
        {
            _progress.IsIndeterminate = false;
            _progress.Maximum = total;
            _progress.Value = Math.Clamp(done, 0, total);
        }
        else
        {
            _progress.IsIndeterminate = true;
        }
    }

    /// <summary>Closes after the minimum visible time; safe to call more than once.</summary>
    public async Task CloseWhenReadyAsync()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        TimeSpan shown = DateTime.UtcNow - _shownAt;
        if (shown < MinimumVisible)
        {
            await Task.Delay(MinimumVisible - shown);
        }

        Close();
    }

    private static Bitmap? LoadBitmap(string file)
    {
        try
        {
            using Stream stream = AssetLoader.Open(new Uri($"avares://wraithcairn/assets/{file}"));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }
}
