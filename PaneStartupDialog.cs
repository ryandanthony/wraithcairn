using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Wraithcairn;

/// <summary>
/// Modal editor for one pane's startup: directory, program, arguments, and
/// whether to run it inside the shell or as the pane's own process.
/// Returns the edited config, or null when cancelled.
/// </summary>
public sealed class PaneStartupDialog : Window
{
    private readonly TextBox _directory;
    private readonly TextBox _command;
    private readonly TextBox _args;
    private readonly RadioButton _inShell;
    private readonly RadioButton _exec;
    private readonly CheckBox _applyNow;
    private readonly TextBlock _preview;
    private readonly string _shellName;

    private PaneStartup? _result;

    public bool ApplyNow => _applyNow.IsChecked == true;

    public PaneStartupDialog(PaneStartup current, string currentCwd, string shellName, IBrush? background)
    {
        _shellName = shellName;
        Title = "Configure Pane";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (background is not null)
        {
            Background = background;
        }

        _directory = new TextBox
        {
            Text = current.Directory ?? string.Empty,
            Watermark = $"blank = keep last folder ({currentCwd})",
            Width = 380,
        };
        Button browse = new() { Content = "Browse\u2026", Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += async (_, _) =>
        {
            IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Pane start folder", AllowMultiple = false });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            {
                _directory.Text = path;
            }
        };

        _command = new TextBox
        {
            Text = current.Command ?? string.Empty,
            Watermark = "blank = just a shell (e.g. htop, npm, docker, ssh)",
            Width = 470,
        };
        _args = new TextBox
        {
            Text = PaneStartup.JoinArgs(current.Args),
            Watermark = "arguments, quoted like a shell: run dev --port \"8080\"",
            Width = 470,
        };

        _inShell = new RadioButton
        {
            GroupName = "mode",
            Content = "Run inside the shell (back to a prompt when it exits)",
            IsChecked = current.RunInShell,
        };
        _exec = new RadioButton
        {
            GroupName = "mode",
            Content = "Run directly as the pane's program (no shell; Ctrl+Shift+R restarts)",
            IsChecked = !current.RunInShell,
        };

        _applyNow = new CheckBox { Content = "Restart this pane now with the new settings", IsChecked = true };

        _preview = new TextBlock
        {
            FontFamily = new FontFamily("monospace"),
            FontSize = 11,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 470,
        };

        foreach (TextBox box in new[] { _directory, _command, _args })
        {
            box.TextChanged += (_, _) => UpdatePreview();
        }

        _inShell.IsCheckedChanged += (_, _) => UpdatePreview();

        Button ok = new() { Content = "Save", IsDefault = true, MinWidth = 80 };
        Button clear = new() { Content = "Clear", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        Button cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) =>
        {
            _result = Build();
            Close();
        };
        clear.Click += (_, _) =>
        {
            _directory.Text = string.Empty;
            _command.Text = string.Empty;
            _args.Text = string.Empty;
            _inShell.IsChecked = true;
        };
        cancel.Click += (_, _) => Close();

        StackPanel dirRow = Row(_directory, browse);
        StackPanel buttons = Row(ok, clear, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;

        StackPanel body = new() { Margin = new Thickness(16), Spacing = 6 };
        body.Children.Add(Label("Start in folder"));
        body.Children.Add(dirRow);
        body.Children.Add(Label("Program to run"));
        body.Children.Add(_command);
        body.Children.Add(Label("Arguments"));
        body.Children.Add(_args);
        body.Children.Add(_inShell);
        body.Children.Add(_exec);
        body.Children.Add(Label("Will run"));
        body.Children.Add(_preview);
        body.Children.Add(_applyNow);
        body.Children.Add(buttons);
        Content = body;

        Opened += (_, _) => _command.Focus();
        UpdatePreview();
    }

    public new async Task<PaneStartup?> ShowDialog(Window owner)
    {
        await base.ShowDialog(owner);
        return _result;
    }

    private PaneStartup Build() => new()
    {
        Directory = string.IsNullOrWhiteSpace(_directory.Text) ? null : _directory.Text.Trim(),
        Command = string.IsNullOrWhiteSpace(_command.Text) ? null : _command.Text.Trim(),
        Args = PaneStartup.SplitArgs(_args.Text),
        RunInShell = _inShell.IsChecked == true,
    };

    private void UpdatePreview()
    {
        PaneStartup draft = Build();
        string where = draft.Directory is null
            ? "(last folder)"
            : draft.ResolveDirectory() ?? $"{draft.Directory}  \u26a0 folder not found, will fall back";

        string what = !draft.HasCommand
            ? $"{_shellName} (interactive shell)"
            : draft.RunInShell
                ? $"{_shellName} \u2192 {draft.BuildShellLine(_shellName)}"
                : $"exec {draft.Command} " + string.Join(' ', draft.Args.Select(a => $"[{a}]"));

        _preview.Text = $"cd {where}\n{what}";
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Margin = new Thickness(0, 6, 0, 0),
    };

    private static StackPanel Row(params Control[] children)
    {
        StackPanel row = new() { Orientation = Orientation.Horizontal };
        foreach (Control child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }
}
