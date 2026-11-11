using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Wraithcairn;

/// <summary>
/// View > Themes...: browse the Ghostty theme catalog from the docs site,
/// preview a theme live on every pane, download one or all of them, and
/// apply. Applying writes "theme" to config.json and repaints immediately,
/// no restart. Closing without applying puts the previous theme back.
/// </summary>
public sealed class ThemeDialog : Window
{
    /// <summary>One picker at a time (also pauses config-watcher repaints while previewing).</summary>
    public static bool IsOpen { get; private set; }

    private readonly TextBox _search;
    private readonly ComboBox _filter;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _apply;
    private readonly Button _downloadAll;
    private readonly Button _useGhostty;
    private readonly CheckBox _livePreview;
    private readonly Border _preview;
    private readonly TextBlock _previewText;
    private readonly WrapPanel _swatches;
    private readonly TextBlock _previewTitle;

    private List<ThemeEntry> _all = [];
    private bool _busy;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Last catalog load, for the selftest.</summary>
    public ThemeCatalog.CatalogResult? Catalog { get; private set; }

    /// <summary>Completes once the catalog is listed.</summary>
    public Task CatalogLoaded => _loaded.Task;

    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int VisibleCount => (_list.ItemsSource as List<ThemeEntry>)?.Count ?? 0;

    public ThemeDialog(IBrush? background)
    {
        IsOpen = true;
        Title = "Themes";
        Width = 920;
        Height = 620;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppInfo.WindowIcon;
        if (background is not null)
        {
            Background = background;
        }

        _search = new TextBox { PlaceholderText = "search themes\u2026", Width = 260 };
        _filter = new ComboBox
        {
            ItemsSource = new[] { "All themes", "Dark", "Light", "Downloaded" },
            SelectedIndex = 0,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 130,
        };
        _downloadAll = new Button { Content = "Download All", Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
        ToolTip.SetTip(_downloadAll, $"Download every theme into {AppConfig.UserThemesDir}");
        _progress = new ProgressBar
        {
            Width = 140,
            Minimum = 0,
            Maximum = 1,
            Margin = new Thickness(8, 0, 0, 0),
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };

        StackPanel top = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 12, 12, 6) };
        top.Children.Add(_search);
        top.Children.Add(_filter);
        top.Children.Add(_downloadAll);
        top.Children.Add(_progress);

        _list = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<ThemeEntry?>((entry, _) => entry is null ? new TextBlock() : BuildRow(entry), supportsRecycling: false),
            Width = 340,
            Margin = new Thickness(12, 0, 6, 0),
            SelectionMode = SelectionMode.Single,
        };

        _previewTitle = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        _previewText = new TextBlock
        {
            FontFamily = new FontFamily("JetBrainsMono NFM, JetBrains Mono, DejaVu Sans Mono, Consolas, Menlo, monospace"),
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
        };
        _swatches = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        StackPanel previewBody = new();
        previewBody.Children.Add(_previewText);
        previewBody.Children.Add(_swatches);
        _preview = new Border
        {
            Child = previewBody,
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
        };

        _livePreview = new CheckBox
        {
            Content = "Preview on open panes",
            IsChecked = true,
            Margin = new Thickness(0, 10, 0, 0),
        };

        StackPanel right = new() { Margin = new Thickness(6, 0, 12, 0) };
        right.Children.Add(_previewTitle);
        right.Children.Add(_preview);
        right.Children.Add(_livePreview);
        right.Children.Add(new TextBlock
        {
            Text = "Double-click or Apply to use a theme. It is saved as \"theme\" in config.json and takes effect "
                + "right away. Themes are downloaded on first use and then work offline.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
        });

        _status = new TextBlock
        {
            Foreground = Brushes.Gray,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = "Loading themes\u2026",
        };
        _useGhostty = new Button { Content = "Use Ghostty Config Theme", Margin = new Thickness(8, 0, 0, 0) };
        ToolTip.SetTip(_useGhostty, "Remove \"theme\" from config.json and inherit the theme from ~/.config/ghostty/config");
        _apply = new Button { Content = "Apply", IsDefault = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
        Button close = new() { Content = "Close", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };

        DockPanel bottom = new() { Margin = new Thickness(12, 8, 12, 12) };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_useGhostty);
        buttons.Children.Add(_apply);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        DockPanel root = new();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(_list, Dock.Left);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        root.Children.Add(new ScrollViewer { Content = right });
        Content = root;

        _search.TextChanged += (_, _) => Refilter();
        _filter.SelectionChanged += (_, _) => Refilter();
        _list.SelectionChanged += (_, _) => OnSelected();
        _list.DoubleTapped += (_, _) => _ = ApplySelectedAsync();
        _apply.Click += (_, _) => _ = ApplySelectedAsync();
        _downloadAll.Click += (_, _) => _ = DownloadAllAsync();
        _useGhostty.Click += (_, _) => UseGhosttyTheme();
        close.Click += (_, _) => Close();
        _livePreview.IsCheckedChanged += (_, _) =>
        {
            if (_livePreview.IsChecked != true)
            {
                MainWindow.EndThemePreview();
            }
            else
            {
                OnSelected();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && _search.IsFocused && _list.ItemCount > 0)
            {
                _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, _list.ItemCount - 1);
                _list.Focus();
                e.Handled = true;
            }
        };

        Opened += async (_, _) =>
        {
            _search.Focus();
            await LoadCatalogAsync();
        };
        Closed += (_, _) =>
        {
            _cts.Cancel();
            MainWindow.EndThemePreview();
            IsOpen = false;
        };
    }

    private async Task LoadCatalogAsync()
    {
        try
        {
            Catalog = await ThemeCatalog.LoadAsync(_cts.Token);
            _all = Catalog.Themes;
            _downloadAll.IsEnabled = Catalog.Online;
            Refilter();
            string? current = MainWindow.CurrentThemeName;
            ThemeEntry? selected = _all.FirstOrDefault(t => t.Name == current);
            if (selected is not null)
            {
                _list.SelectedItem = selected;
                _list.ScrollIntoView(selected);
            }
            else if (_list.ItemCount > 0)
            {
                ShowPreview(_all[0]);
            }

            UpdateStatus();
        }
        catch (OperationCanceledException)
        {
            // dialog closed
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not load themes: {ex.Message}";
        }
        finally
        {
            _loaded.TrySetResult();
        }
    }

    private void UpdateStatus(string? extra = null)
    {
        if (Catalog is null)
        {
            return;
        }

        int installed = _all.Count(t => t.Installed);
        string where = Catalog.Online
            ? $"{_all.Count} themes"
            : Catalog.Themes.Any(t => t.InCatalog)
                ? $"offline (cached list): {_all.Count} themes"
                : $"offline: {_all.Count} local themes ({Catalog.Error})";
        string current = MainWindow.CurrentThemeName is { } name
            ? $"current: {name}{(MainWindow.ThemeFromAppConfig ? string.Empty : " (from Ghostty config)")}"
            : "current: built-in default";
        _status.Text = $"{where}, {installed} downloaded  |  {current}{(extra is null ? string.Empty : "  |  " + extra)}";
        _useGhostty.IsEnabled = MainWindow.ThemeFromAppConfig && !_busy;
    }

    private void Refilter()
    {
        string query = _search.Text?.Trim() ?? string.Empty;
        ThemeEntry? keep = _list.SelectedItem as ThemeEntry;
        IEnumerable<ThemeEntry> items = _all;
        if (query.Length > 0)
        {
            items = items.Where(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        items = _filter.SelectedIndex switch
        {
            1 => items.Where(t => t.Dark),
            2 => items.Where(t => !t.Dark),
            3 => items.Where(t => t.Installed),
            _ => items,
        };

        List<ThemeEntry> list = [.. items];
        _list.ItemsSource = list;
        if (keep is not null && list.Contains(keep))
        {
            _list.SelectedItem = keep;
        }
    }

    private static Control BuildRow(ThemeEntry entry)
    {
        StackPanel strip = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        strip.Children.Add(Swatch(entry.Background, 14, 10, entry.Foreground));
        foreach (string color in entry.Palette.Take(16))
        {
            strip.Children.Add(Swatch(color, 10, 10));
        }

        TextBlock name = new() { FontSize = 13 };
        name.Inlines!.Add(new Run(entry.Name));
        if (entry.Installed)
        {
            name.Inlines.Add(new Run("  \u2713") { Foreground = Brushes.MediumSeaGreen });
        }

        if (entry.Name == MainWindow.CurrentThemeName)
        {
            name.Inlines.Add(new Run("  (current)") { Foreground = Brushes.Gray, FontSize = 11 });
        }

        StackPanel row = new() { Margin = new Thickness(2) };
        row.Children.Add(name);
        row.Children.Add(strip);
        return row;
    }

    private static Border Swatch(string hex, double width, double height, string? border = null) => new()
    {
        Width = width,
        Height = height,
        Background = new SolidColorBrush(ParseOr(hex, Colors.Black)),
        BorderBrush = border is null ? null : new SolidColorBrush(ParseOr(border, Colors.Gray)),
        BorderThickness = border is null ? default : new Thickness(1),
        Margin = new Thickness(0, 0, 1, 0),
    };

    private static Color ParseOr(string hex, Color fallback) => Color.TryParse(hex, out Color c) ? c : fallback;

    private void OnSelected()
    {
        if (_list.SelectedItem is not ThemeEntry entry)
        {
            _apply.IsEnabled = false;
            return;
        }

        _apply.IsEnabled = !_busy;
        _apply.Content = entry.Installed || !entry.InCatalog ? "Apply" : "Download & Apply";
        ShowPreview(entry);
        if (_livePreview.IsChecked == true)
        {
            MainWindow.PreviewTheme(entry.ToTerminalTheme());
        }
    }

    private void ShowPreview(ThemeEntry entry)
    {
        _previewTitle.Text = entry.Name;
        Color bg = ParseOr(entry.Background, Colors.Black);
        Color fg = ParseOr(entry.Foreground, Colors.White);
        IBrush P(int i) => new SolidColorBrush(ParseOr(entry.Palette.ElementAtOrDefault(i) ?? entry.Foreground, fg));
        IBrush fgBrush = new SolidColorBrush(fg);

        _preview.Background = new SolidColorBrush(bg);
        InlineCollection lines = _previewText.Inlines!;
        lines.Clear();
        void Add(string text, IBrush? brush = null, bool bold = false) =>
            lines.Add(new Run(text) { Foreground = brush ?? fgBrush, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal });

        Add("ryan@host", P(2), bold: true);
        Add(":");
        Add("~/code/wraithcairn", P(4), bold: true);
        Add(" (", P(8));
        Add("main", P(5));
        Add(")", P(8));
        Add(" $ ls --color\n");
        Add("docs/  ", P(4), bold: true);
        Add("scripts/  ", P(4), bold: true);
        Add("install.sh  ", P(2), bold: true);
        Add("README.md  ");
        Add("logo.png\n", P(5));
        Add("$ git status -s\n");
        Add(" M ", P(1));
        Add("MainWindow.cs\n");
        Add("?? ", P(1));
        Add("ThemeCatalog.cs\n");
        Add("$ dotnet build\n");
        Add("  warning", P(3), bold: true);
        Add(" CS8618: Non-nullable field\n");
        Add("  error", P(1), bold: true);
        Add(" CS1002: ; expected\n");
        Add("Build succeeded.", P(2));
        Add("  0 Error(s)\n", P(8));
        Add("$ ");
        if (entry.CursorColor is { } cursor)
        {
            lines.Add(new Run(" ")
            {
                Background = new SolidColorBrush(ParseOr(cursor, fg)),
            });
        }

        _swatches.Children.Clear();
        for (int i = 0; i < 16 && i < entry.Palette.Length; i++)
        {
            Color c = ParseOr(entry.Palette[i], fg);
            _swatches.Children.Add(new Border
            {
                Width = 28,
                Height = 22,
                Margin = new Thickness(0, 0, 3, 3),
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(c),
                Child = new TextBlock
                {
                    Text = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B) > 140 ? Brushes.Black : Brushes.White,
                },
            });
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _apply.IsEnabled = !busy && _list.SelectedItem is ThemeEntry;
        _downloadAll.IsEnabled = !busy && Catalog?.Online == true;
        _useGhostty.IsEnabled = !busy && MainWindow.ThemeFromAppConfig;
        _progress.IsVisible = busy;
    }

    /// <summary>Downloads (if needed), applies live, and saves the selected theme.</summary>
    public async Task<bool> ApplySelectedAsync()
    {
        if (_busy || _list.SelectedItem is not ThemeEntry entry)
        {
            return false;
        }

        SetBusy(true);
        _progress.IsIndeterminate = true;
        try
        {
            if (!ThemeCatalog.IsInstalled(entry.Name))
            {
                _status.Text = $"Downloading {entry.Name}\u2026";
                await ThemeCatalog.DownloadAsync(entry, _cts.Token);
            }

            bool saved = MainWindow.SetThemeLive(entry.Name);
            Refilter();
            UpdateStatus(saved ? $"applied {entry.Name}" : "applied, but config.json could not be saved");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UpdateStatus($"failed: {ex.Message}");
            return false;
        }
        finally
        {
            _progress.IsIndeterminate = false;
            SetBusy(false);
            OnSelected();
        }
    }

    public async Task<int> DownloadAllAsync()
    {
        if (_busy)
        {
            return 0;
        }

        SetBusy(true);
        _progress.IsIndeterminate = true;
        _status.Text = "Downloading all themes\u2026";
        try
        {
            Progress<(int Done, int Total)> progress = new(p =>
            {
                if (p.Total <= 0)
                {
                    return;
                }

                _progress.IsIndeterminate = false;
                _progress.Value = (double)p.Done / p.Total;
                _status.Text = $"Installing themes\u2026 {p.Done}/{p.Total}";
            });
            int written = await ThemeCatalog.DownloadAllAsync(progress, _cts.Token);
            foreach (ThemeEntry entry in _all.Where(t => t.InCatalog))
            {
                entry.Installed = ThemeCatalog.IsInstalled(entry.Name);
            }

            Refilter();
            UpdateStatus($"downloaded {written} themes");
            return written;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UpdateStatus($"download failed: {ex.Message}");
            return 0;
        }
        finally
        {
            _progress.IsIndeterminate = false;
            SetBusy(false);
        }
    }

    private void UseGhosttyTheme()
    {
        bool saved = MainWindow.SetThemeLive(null);
        Refilter();
        if (_all.FirstOrDefault(t => t.Name == MainWindow.CurrentThemeName) is { } ghostty)
        {
            _list.SelectedItem = ghostty;
            _list.ScrollIntoView(ghostty);
        }
        else
        {
            MainWindow.EndThemePreview();
        }

        UpdateStatus(saved ? "using the Ghostty config theme" : "config.json could not be saved");
    }

    /// <summary>Selects a theme by name (selftest).</summary>
    public bool Select(string name)
    {
        _search.Text = string.Empty;
        _filter.SelectedIndex = 0;
        Refilter();
        ThemeEntry? entry = _all.FirstOrDefault(t => t.Name == name);
        if (entry is null)
        {
            return false;
        }

        _list.SelectedItem = entry;
        Dispatcher.UIThread.RunJobs();
        return ReferenceEquals(_list.SelectedItem, entry);
    }

    /// <summary>Applies the current search text, for the selftest.</summary>
    public void Search(string text)
    {
        _search.Text = text;
        Refilter();
    }
}
