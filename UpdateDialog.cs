using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Wraithcairn;

/// <summary>
/// View > Check for Updates...: looks up the latest GitHub release, shows
/// current vs latest, and (when this is an installed release build) can
/// download, verify, and install it, then restart into it. Same engine as
/// `wraithcairn update`.
/// </summary>
public sealed class UpdateDialog : Window
{
    private readonly TextBlock _headline;
    private readonly TextBlock _detail;
    private readonly ProgressBar _progress;
    private readonly Button _primary;
    private readonly Button _notes;
    private readonly Button _close;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<bool> _restart;

    private Updater.UpdateCheck? _check;
    private bool _installed;

    /// <summary>Completes after the first check (selftest).</summary>
    public Task Checked => _checked.Task;

    private readonly TaskCompletionSource _checked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Updater.UpdateCheck? Result => _check;

    public string Headline => _headline.Text ?? string.Empty;

    public string PrimaryAction => _primary.IsVisible ? _primary.Content as string ?? string.Empty : string.Empty;

    /// <param name="restart">Saves state and relaunches; returns false if the relaunch failed.</param>
    public UpdateDialog(IBrush? background, Func<bool> restart)
    {
        _restart = restart;
        Title = "Check for Updates";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppInfo.WindowIcon;
        if (background is not null)
        {
            Background = background;
        }

        _headline = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, Text = "Checking for updates\u2026" };
        _detail = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 440,
            Foreground = Brushes.Gray,
            Text = $"Current version: {AppInfo.Version} ({Updater.Platform().Rid})",
        };
        _progress = new ProgressBar { IsIndeterminate = true, Height = 4, MinWidth = 440 };

        _primary = new Button { Content = "Update Now", IsDefault = true, MinWidth = 110, IsVisible = false };
        _notes = new Button { Content = "Release Notes", MinWidth = 110, Margin = new Thickness(8, 0, 0, 0), IsVisible = false };
        _close = new Button { Content = "Close", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        _primary.Click += async (_, _) => await PrimaryAsync();
        _notes.Click += (_, _) =>
        {
            if (_check?.Latest is { } latest)
            {
                _ = Launcher.LaunchUriAsync(new Uri(Updater.ReleaseNotesUrl(latest.Version)));
            }
        };
        _close.Click += (_, _) => Close();

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_primary);
        buttons.Children.Add(_notes);
        buttons.Children.Add(_close);

        StackPanel body = new() { Margin = new Thickness(20), Spacing = 10, MinWidth = 440 };
        body.Children.Add(_headline);
        body.Children.Add(_detail);
        body.Children.Add(_progress);
        body.Children.Add(buttons);
        Content = body;

        Opened += async (_, _) => await CheckAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    private async Task CheckAsync()
    {
        try
        {
            _check = await Updater.CheckAsync(_cts.Token);
            Show(_check);
        }
        catch (OperationCanceledException)
        {
            // closed
        }
        finally
        {
            _progress.IsVisible = false;
            _checked.TrySetResult();
        }
    }

    private void Show(Updater.UpdateCheck check)
    {
        string current = $"Current version: {check.Current} ({Updater.Platform().Rid})";
        if (check.Latest is not { } latest)
        {
            _headline.Text = "Couldn't check for updates";
            _detail.Text = $"{current}\n{check.Error}";
            _primary.Content = "Try Again";
            _primary.IsVisible = true;
            return;
        }

        _notes.IsVisible = true;
        string? blocked = Updater.CannotSelfUpdateReason();
        if (!check.IsNewer)
        {
            _headline.Text = "wraithcairn is up to date";
            _detail.Text = $"{current}\nLatest release: {latest.Version}";
            return;
        }

        _headline.Text = check.IsDevBuild
            ? $"Latest release: {latest.Version}"
            : $"Update available: {latest.Version}";
        _detail.Text = check.IsDevBuild
            ? $"{current}\nThis is a development build."
            : $"{current}\nLatest release: {latest.Version}";
        if (blocked is null)
        {
            _primary.Content = "Update Now";
            _primary.IsVisible = true;
        }
        else
        {
            _detail.Text += $"\n\nCan't update in place: {blocked}.\n"
                + (OperatingSystem.IsWindows()
                    ? "irm https://ryandanthony.github.io/wraithcairn/install.ps1 | iex"
                    : "curl -fsSL https://ryandanthony.github.io/wraithcairn/install | bash");
        }
    }

    private async Task PrimaryAsync()
    {
        if (_installed)
        {
            _primary.IsEnabled = false;
            if (!_restart())
            {
                _primary.IsEnabled = true;
                _detail.Text += "\nCould not start the new version; start wraithcairn again yourself.";
            }

            return;
        }

        if (_check?.Latest is not { } latest)
        {
            _primary.IsVisible = false;
            _headline.Text = "Checking for updates\u2026";
            _progress.IsVisible = true;
            await CheckAsync();
            return;
        }

        _primary.IsEnabled = false;
        _close.IsEnabled = false;
        _progress.IsVisible = true;
        _progress.IsIndeterminate = true;
        try
        {
            Progress<string> progress = new(text => _detail.Text = text);
            await Updater.InstallAsync(latest, progress, _cts.Token);
            _installed = true;
            _headline.Text = $"Updated to {latest.Version}";
            _detail.Text = "Restart wraithcairn to use it. Your layouts are saved and come back after the restart.";
            _primary.Content = "Restart Now";
            _close.Content = "Later";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _headline.Text = "Update failed";
            _detail.Text = ex.Message;
            _primary.Content = "Try Again";
        }
        finally
        {
            _progress.IsVisible = false;
            _primary.IsEnabled = true;
            _close.IsEnabled = true;
        }
    }
}
