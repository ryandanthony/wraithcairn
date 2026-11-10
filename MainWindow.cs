using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Services;

namespace Wraithcairn;

/// <summary>
/// Wraithcairn: a cross-platform split-pane terminal on RoyalTerminal
/// (libghostty-vt engine) that saves its layout (tabs, splits, ratios,
/// per-pane cwd) to workspace.json and restores it on next start.
///
/// Keys:
///   Ctrl+Shift+T        new layout
///   Ctrl+Shift+W        close pane (last pane closes the layout)
///   Ctrl+Shift+O        split right          Ctrl+Shift+E  split down
///   Alt+Arrows          move focus between panes
///   Ctrl+Alt+Arrows     resize split          Ctrl+Shift+Z  zoom pane
///   Ctrl+Shift+=        equalize splits
///   Ctrl+Shift+C / V    copy selection / paste
///   Ctrl+Shift+F        find (Enter next, Shift+Enter prev, Esc close)
///   Ctrl+Shift+K        clear scrollback
///   Ctrl+Shift+N        new window
///   Ctrl+Shift+P        configure pane (start folder, program, args)
///   Ctrl+Shift+R        restart pane
///   Ctrl+PgUp/PgDn      previous / next layout
///   Alt+1..9            switch to layout N
///   Ctrl+Shift+PgUp/PgDn move layout up / down in the list
///   Ctrl+= / Ctrl+-     font size
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly string WorkspacePath = NamedLayouts.ActivePath;

    /// <summary>Per-pane startup config keyed by pane id (shared by all windows).</summary>
    private static readonly Dictionary<string, PaneStartup> Startups = new(StringComparer.Ordinal);

    private readonly ContentControl _layoutHost;
    private readonly TextBlock _status;
    private readonly TextBox _searchBox;
    private readonly Border _searchBar;
    private Menu? _menu;
    private readonly ShellIntegration.LaunchSpec _shell;
    private static readonly AppConfig Config = AppConfig.Load();
    private double _fontSize = Config.FontSize;
    private static int _paneCounter;

    /// <summary>
    /// A layout: one named pane tree. Several can be open in a window; only
    /// the active one is shown, the others keep running in the background.
    /// Switched from the Layouts menu (no tab strip).
    /// </summary>
    private sealed class Tab
    {
        public required string Name { get; set; }
        public required Border Host { get; init; }
        public required TerminalPaneNode Root { get; set; }
        public TerminalControl? Active { get; set; }

        /// <summary>Program-set (OSC) or cwd-derived title, shown in the window title.</summary>
        public string? DisplayTitle { get; set; }

        /// <summary>True once the program set a title via OSC; stops cwd-based titles.</summary>
        public bool TitleFromOsc { get; set; }

        /// <summary>Set while one pane is zoomed to fill the tab.</summary>
        public ZoomState? Zoom { get; set; }

        public Dictionary<TerminalControl, TerminalPaneNode> Nodes { get; } = [];
    }

    private sealed class ZoomState
    {
        public required TerminalPaneNode Leaf { get; init; }
        public required Grid ParentGrid { get; init; }
        public required int ChildIndex { get; init; }
        public required int Row { get; init; }
        public required int Column { get; init; }
    }

    private readonly List<Tab> _tabs = [];
    private Tab? _activeTab;
    private readonly DispatcherTimer _autosave;
    private System.Runtime.InteropServices.PosixSignalRegistration? _sigterm;
    private bool _layoutDirty;
    private bool _restoring;

    // ----- multi-window registry -------------------------------------------

    private static readonly List<MainWindow> Windows = [];
    private static MainWindow? _lastFocused;
    private static bool _saving;
    private static bool _shuttingDown;
    private static int _scrollbackRestored;
    private readonly TerminalWorkspaceWindow? _pendingRestore;
    private readonly bool _isPrimary;

    public MainWindow()
        : this(null, isPrimary: true)
    {
    }

    private MainWindow(TerminalWorkspaceWindow? restore, bool isPrimary)
    {
        _pendingRestore = restore;
        _isPrimary = isPrimary;
        _shell = ShellIntegration.Resolve(Config.Shell);
        Windows.Add(this);
        Activated += (_, _) => _lastFocused = this;
        Title = NamedLayouts.ActiveName is { } layoutName
            ? $"wraithcairn [{layoutName}]"
            : "wraithcairn";
        Width = 1200;
        Height = 800;
        Background = Config.ResolvedTheme is { } chromeTheme
            ? new SolidColorBrush(Color.FromUInt32(chromeTheme.DefaultBackground))
            : new SolidColorBrush(Color.Parse("#1e1e2e"));

        _layoutHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };

        _status = new TextBlock
        {
            Margin = new Thickness(8, 3),
            FontSize = 12,
            Foreground = Brushes.Gray,
        };

        _searchBox = new TextBox
        {
            Watermark = "find…",
            Width = 280,
            FontSize = 12,
        };
        _searchBar = new Border
        {
            Child = _searchBox,
            Padding = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsVisible = false,
        };
        _searchBox.KeyDown += OnSearchKey;
        _searchBox.TextChanged += (_, _) => RunSearch(select: false);

        DockPanel root = new();
        Menu menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(_searchBar, Dock.Top);
        root.Children.Add(menu);
        root.Children.Add(_status);
        root.Children.Add(_searchBar);
        root.Children.Add(_layoutHost);
        Content = root;

        // Intercept our shortcuts before the terminal control eats the keys.
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);

        Opened += async (_, _) =>
        {
            try
            {
                await RestoreWorkspaceAsync();
                if (_isPrimary && Environment.GetCommandLineArgs().Contains("--dump-panes"))
                {
                    await DumpPanesAsync();
                }
                else if (_isPrimary && Environment.GetCommandLineArgs().Contains("--selftest"))
                {
                    await RunSelfTestAsync();
                }
                else if (_isPrimary && Environment.GetCommandLineArgs().Contains("--configure-pane"))
                {
                    await ConfigurePaneAsync();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"startup failed: {ex}");
            }
        };
        Closing += (_, _) =>
        {
            if (_shuttingDown)
            {
                return; // final save already happened
            }

            // A deliberately closed window should not come back: drop it from
            // the registry first, then persist whatever windows remain (or
            // this one alone when it is the last).
            Windows.Remove(this);
            if (Windows.Count == 0)
            {
                Windows.Add(this);
                SaveWorkspace();
                Windows.Remove(this);
            }
            else
            {
                SaveWorkspace();
            }
        };

        // Debounced autosave: marks dirty on layout changes, writes at most
        // every 5 s, so a crash or kill -9 loses almost nothing.
        _autosave = new DispatcherTimer(
            TimeSpan.FromSeconds(5),
            DispatcherPriority.Background,
            (_, _) =>
            {
                if (_layoutDirty && !_restoring)
                {
                    _layoutDirty = false;
                    SaveWorkspace();
                }
            });
        _autosave.Start();

        // Window-size changes count as layout changes.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty || e.Property == WindowStateProperty)
            {
                MarkLayoutDirty();
            }
        };

        // Save on SIGTERM / SIGINT (systemd stop, kill, Ctrl+C from launcher)
        // and on normal process exit. SaveWorkspace marshals itself onto the
        // UI thread when called from a POSIX signal thread. Only the primary
        // window registers process-level hooks.
        if (_isPrimary)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => SaveWorkspaceFromAnyThread();
            Console.CancelKeyPress += (_, args) =>
            {
                SaveWorkspaceFromAnyThread();
                args.Cancel = false;
            };
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                _sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
                    System.Runtime.InteropServices.PosixSignal.SIGTERM,
                    context =>
                    {
                        context.Cancel = true;
                        SaveWorkspaceFromAnyThread();
                        Dispatcher.UIThread.Post(() =>
                        {
                            _shuttingDown = true;
                            foreach (MainWindow window in Windows.ToArray())
                            {
                                window.Close();
                            }
                        });
                    });
            }
        }
    }

    private void MarkLayoutDirty() => _layoutDirty = true;

    private void SaveWorkspaceFromAnyThread()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            SaveWorkspace();
            return;
        }

        try
        {
            Dispatcher.UIThread.Invoke(SaveWorkspace, DispatcherPriority.Send, default,
                TimeSpan.FromSeconds(3));
        }
        catch
        {
            // UI thread gone or wedged; nothing more we can do safely.
        }
    }

    // ----- menu ------------------------------------------------------------

    /// <summary>
    /// Menu bar mirroring the keyboard shortcuts. Gestures are display-only
    /// (the window's tunnel key handler does the real work), so nothing fires
    /// twice. Headers have no access-key underscores, so Alt+letter still
    /// reaches the shell (readline word movement etc.).
    /// </summary>
    private Menu BuildMenu()
    {
        static MenuItem Item(string header, KeyGesture? gesture, Action action)
        {
            MenuItem item = new() { Header = header, InputGesture = gesture };
            item.Click += (_, _) => action();
            return item;
        }

        static KeyGesture Gesture(Key key, KeyModifiers modifiers = KeyModifiers.Control | KeyModifiers.Shift) =>
            new(key, modifiers);

        MenuItem shell = new() { Header = "Shell" };
        shell.Items.Add(Item("New Layout", Gesture(Key.T), () => _ = NewTabAsync(null)));
        shell.Items.Add(Item("New Window", Gesture(Key.N), () => new MainWindow(null, isPrimary: false).Show()));
        shell.Items.Add(new Separator());
        shell.Items.Add(Item("Close Pane", Gesture(Key.W), () => ClosePane()));

        MenuItem pane = new() { Header = "Pane" };
        pane.Items.Add(Item("Split Right", Gesture(Key.O),
            () => _ = SplitAsync(TerminalPaneSplitOrientation.Horizontal)));
        pane.Items.Add(Item("Split Down", Gesture(Key.E),
            () => _ = SplitAsync(TerminalPaneSplitOrientation.Vertical)));
        pane.Items.Add(new Separator());
        pane.Items.Add(Item("Zoom Pane", Gesture(Key.Z), ToggleZoom));
        pane.Items.Add(Item("Equalize Splits", Gesture(Key.OemPlus), EqualizeSplits));
        pane.Items.Add(new Separator());
        pane.Items.Add(Item("Configure Pane…", Gesture(Key.P), () => _ = ConfigurePaneAsync()));
        pane.Items.Add(Item("Restart Pane", Gesture(Key.R), () =>
        {
            if (ActiveTab()?.Active is { } control)
            {
                _ = RestartPaneAsync(control);
            }
        }));

        MenuItem edit = new() { Header = "Edit" };
        edit.Items.Add(Item("Copy", Gesture(Key.C), () =>
        {
            if (ActiveTab()?.Active is { HasSelection: true } control)
            {
                _ = control.CopySelectionAsync();
            }
        }));
        edit.Items.Add(Item("Paste", Gesture(Key.V), () =>
        {
            if (ActiveTab()?.Active is { } control)
            {
                _ = control.PasteAsync();
            }
        }));
        edit.Items.Add(new Separator());
        edit.Items.Add(Item("Find", Gesture(Key.F), OpenSearch));
        edit.Items.Add(Item("Clear Scrollback", Gesture(Key.K), () => ActiveTab()?.Active?.ClearScrollback()));

        MenuItem view = new() { Header = "View" };
        view.Items.Add(Item("Bigger Text", Gesture(Key.OemPlus, KeyModifiers.Control),
            () => SetFontSize(_fontSize + 1)));
        view.Items.Add(Item("Smaller Text", Gesture(Key.OemMinus, KeyModifiers.Control),
            () => SetFontSize(_fontSize - 1)));

        MenuItem layouts = new() { Header = "Layouts" };
        layouts.SubmenuOpened += (_, _) => RebuildLayoutsMenu(layouts);
        RebuildLayoutsMenu(layouts);

        Menu menu = new() { FontSize = 12 };
        menu.Items.Add(shell);
        menu.Items.Add(pane);
        menu.Items.Add(edit);
        menu.Items.Add(view);
        menu.Items.Add(layouts);

        // Clicking the menu takes keyboard focus; hand it back to the active
        // pane afterwards (unless the action opened the find bar).
        menu.Closed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!_searchBar.IsVisible)
            {
                ActiveTab()?.Active?.Focus();
            }
        }, DispatcherPriority.Background);

        _menu = menu;
        return menu;
    }

    // ----- layouts menu ---------------------------------------------------

    /// <summary>
    /// Layouts menu, rebuilt each time it opens:
    ///   [switch]  one entry per open layout (current one checked)
    ///   New Layout / Rename / Close
    ///   Save Layout As / Open Saved Layout / Delete Saved
    /// </summary>
    private void RebuildLayoutsMenu(MenuItem layouts)
    {
        layouts.Items.Clear();

        static KeyGesture Gesture(Key key, KeyModifiers modifiers = KeyModifiers.Control | KeyModifiers.Shift) =>
            new(key, modifiers);

        // Switch between open layouts.
        for (int i = 0; i < _tabs.Count; i++)
        {
            Tab tab = _tabs[i];
            bool current = ReferenceEquals(tab, _activeTab);
            int panes = tab.Nodes.Count;
            MenuItem item = new()
            {
                Header = $"{tab.Name}   ({panes} pane{(panes == 1 ? "" : "s")})",
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = current,
                InputGesture = i < 9 ? new KeyGesture(Key.D1 + i, KeyModifiers.Alt) : null,
            };
            item.Click += (_, _) => SwitchToLayout(tab);
            layouts.Items.Add(item);
        }

        layouts.Items.Add(new Separator());

        MenuItem next = new() { Header = "Next Layout", InputGesture = new KeyGesture(Key.PageDown, KeyModifiers.Control) };
        next.Click += (_, _) => CycleTab(1);
        next.IsEnabled = _tabs.Count > 1;
        MenuItem prev = new() { Header = "Previous Layout", InputGesture = new KeyGesture(Key.PageUp, KeyModifiers.Control) };
        prev.Click += (_, _) => CycleTab(-1);
        prev.IsEnabled = _tabs.Count > 1;
        layouts.Items.Add(next);
        layouts.Items.Add(prev);
        layouts.Items.Add(new Separator());

        MenuItem newLayout = new() { Header = "New Layout…", InputGesture = Gesture(Key.T) };
        newLayout.Click += async (_, _) =>
        {
            string? name = await PromptForNameAsync("New Layout", UniqueLayoutName("layout"), "Create");
            if (name is not null)
            {
                await NewTabAsync(null, UniqueLayoutName(name));
            }
        };
        layouts.Items.Add(newLayout);

        MenuItem rename = new() { Header = "Rename Layout…", IsEnabled = _activeTab is not null };
        rename.Click += async (_, _) => await RenameActiveLayoutAsync();
        layouts.Items.Add(rename);

        MenuItem close = new() { Header = "Close Layout", IsEnabled = _activeTab is not null };
        close.Click += (_, _) =>
        {
            if (_activeTab is { } t)
            {
                CloseTab(t);
            }
        };
        layouts.Items.Add(close);

        layouts.Items.Add(new Separator());

        // Saved (named) layouts on disk.
        MenuItem saveAs = new() { Header = "Save Layout As…" };
        saveAs.Click += async (_, _) => await SaveActiveLayoutAsAsync();
        layouts.Items.Add(saveAs);

        IReadOnlyList<string> names = NamedLayouts.List();
        MenuItem open = new() { Header = "Open Saved Layout", IsEnabled = names.Count > 0 };
        MenuItem delete = new() { Header = "Delete Saved Layout", IsEnabled = names.Count > 0 };
        foreach (string name in names)
        {
            MenuItem o = new() { Header = name };
            o.Click += async (_, _) => await OpenSavedLayoutAsync(name);
            open.Items.Add(o);

            MenuItem d = new() { Header = name };
            d.Click += (_, _) => NamedLayouts.Delete(name);
            delete.Items.Add(d);
        }

        layouts.Items.Add(open);
        layouts.Items.Add(delete);
    }

    private async Task RenameActiveLayoutAsync()
    {
        if (_activeTab is not { } tab)
        {
            return;
        }

        string? name = await PromptForNameAsync("Rename Layout", tab.Name, "Rename");
        if (name is not null && name != tab.Name)
        {
            tab.Name = UniqueLayoutName(name);
            UpdateWindowTitle(tab);
            MarkLayoutDirty();
        }

        tab.Active?.Focus();
    }

    /// <summary>
    /// Saves only the active layout (its panes, splits and per-pane startup)
    /// to layouts/NAME.json so it can be opened again later.
    /// </summary>
    private async Task SaveActiveLayoutAsAsync()
    {
        if (_activeTab is not { } tab)
        {
            return;
        }

        string? name = await PromptForNameAsync("Save Layout As", tab.Name, "Save");
        if (name is null)
        {
            tab.Active?.Focus();
            return;
        }

        TerminalWorkspaceDocument single = new()
        {
            SelectedWindowId = "window-1",
            Windows =
            [
                new TerminalWorkspaceWindow
                {
                    Id = "window-1",
                    WidthPixels = (int)Width,
                    HeightPixels = (int)Height,
                    SelectedTabId = "tab-1",
                    Tabs =
                    [
                        new TerminalWorkspaceTab
                        {
                            Id = "tab-1",
                            Title = name,
                            WorkingDirectory = tab.Active is { } c && tab.Nodes.TryGetValue(c, out TerminalPaneNode? n)
                                ? n.WorkingDirectory
                                : null,
                            RootPane = ToWorkspacePane(tab.Root),
                        },
                    ],
                },
            ],
        };

        string path = NamedLayouts.PathFor(name);
        LayoutFile.Save(path, single, LiveStartups());
        if (tab.Name != name && !_tabs.Any(t => t != tab && t.Name == name))
        {
            tab.Name = name;
            UpdateWindowTitle(tab);
            MarkLayoutDirty();
        }

        _status.Text = $"saved layout \"{name}\" -> {path}";
        tab.Active?.Focus();
    }

    /// <summary>
    /// Opens a saved layout as a new layout in this window (switch to it from
    /// the Layouts menu). Pane ids are remapped so it can be opened next to
    /// layouts that came from the same file.
    /// </summary>
    private async Task OpenSavedLayoutAsync(string name)
    {
        (TerminalWorkspaceDocument doc, Dictionary<string, PaneStartup> startups) =
            LayoutFile.Load(NamedLayouts.PathFor(name));

        List<Task> sessions = [];
        Tab? first = null;
        foreach (TerminalWorkspaceTab savedTab in doc.Windows.SelectMany(w => w.Tabs))
        {
            Dictionary<string, string> idMap = new(StringComparer.Ordinal);
            TerminalWorkspacePane remapped = RemapPaneIds(savedTab.RootPane, idMap);
            foreach ((string oldId, string newId) in idMap)
            {
                if (startups.TryGetValue(oldId, out PaneStartup? s))
                {
                    Startups[newId] = s.Clone();
                }
            }

            (TerminalPaneNode root, Dictionary<TerminalControl, TerminalPaneNode> nodes) =
                BuildPaneTree(remapped, sessions);
            string layoutName = UniqueLayoutName(
                doc.Windows.SelectMany(w => w.Tabs).Count() > 1 && savedTab.Title is { Length: > 0 } t
                    ? t
                    : name);
            Tab tab = MakeTab(root, layoutName);
            foreach ((TerminalControl control, TerminalPaneNode node) in nodes)
            {
                tab.Nodes[control] = node;
            }

            tab.Active = TerminalPaneLayout.CollectLeafControls(root).FirstOrDefault();
            first ??= tab;
        }

        if (first is not null)
        {
            SwitchToLayout(first);
        }

        await Task.WhenAll(sessions);
        MarkLayoutDirty();
    }

    private static TerminalWorkspacePane RemapPaneIds(TerminalWorkspacePane pane, Dictionary<string, string> map)
    {
        string newId = $"pane-{++_paneCounter}";
        if (pane.Id is { Length: > 0 } oldId)
        {
            map[oldId] = newId;
        }

        return pane with
        {
            Id = newId,
            Split = pane.Split is { } split
                ? split with
                {
                    FirstPane = RemapPaneIds(split.FirstPane, map),
                    SecondPane = RemapPaneIds(split.SecondPane, map),
                }
                : null,
        };
    }

    private async Task<string?> PromptForNameAsync(string title, string initial, string okText)
    {
        TextBox nameBox = new() { Text = initial, Width = 260 };
        Button ok = new() { Content = okText, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        Button cancel = new() { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        StackPanel row = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(12) };
        row.Children.Add(nameBox);
        row.Children.Add(ok);
        row.Children.Add(cancel);

        Window dialog = new()
        {
            Title = title,
            Content = row,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
        };

        string? result = null;
        ok.Click += (_, _) =>
        {
            result = nameBox.Text?.Trim();
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) =>
        {
            nameBox.Focus();
            nameBox.SelectAll();
        };

        await dialog.ShowDialog(this);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    // ----- self test --------------------------------------------------------

    /// <summary>
    /// Headless-ish smoke test: first run builds tabs + a split, runs a
    /// command, snapshots the window to PNG, saves the workspace and exits.
    /// A second run restores the saved layout, snapshots again, prints a
    /// layout summary and exits. Screenshots land in /tmp.
    /// </summary>
    private async Task RunSelfTestAsync()
    {
        bool restored = Windows.Count > 1
            || _tabs.Count > 1
            || (_tabs.Count == 1 && _tabs[0].Nodes.Count > 1);

        if (!restored)
        {
            await SplitAsync(TerminalPaneSplitOrientation.Horizontal);
            await SplitAsync(TerminalPaneSplitOrientation.Vertical);
            await NewTabAsync("/tmp", "tmp");
            SwitchToLayout(_tabs[0]);
        }

        Tab? tab = ActiveTab();
        tab?.Active?.SendInput("echo wraithcairn-selftest-$((6*7))\n");
        await Task.Delay(2500);

        string phase = restored ? "restored" : "initial";
        Console.WriteLine($"SELFTEST phase={phase}");
        Console.WriteLine($"SELFTEST ghostty={RoyalTerminal.Terminal.GhosttyVtProcessor.IsAvailable()}");
        Console.WriteLine($"SELFTEST gpu_gl={IsGpuCompositing()}");
        Console.WriteLine($"SELFTEST shell={_shell.ShellName} integrated={_shell.Integrated}");
        Console.WriteLine($"SELFTEST config font={Config.FontFamily ?? "(default)"} size={Config.FontSize} "
            + $"theme={Config.Theme ?? "(none)"} copyOnSelect={Config.CopyOnSelect}");
        Console.WriteLine($"SELFTEST config_sources={string.Join(" | ", Config.Sources)}");
        Console.WriteLine($"SELFTEST scrollback enabled={Config.RestoreScrollback} restored_panes={_scrollbackRestored}");
        if (tab?.Active is { } themed)
        {
            Console.WriteLine($"SELFTEST pane_font={themed.FontFamilyName} size={themed.TerminalFontSize}");
        }
        Console.WriteLine($"SELFTEST tabs={_tabs.Count}");
        for (int i = 0; i < _tabs.Count; i++)
        {
            Tab t = _tabs[i];
            string cwds = string.Join(",", t.Nodes.Values.Select(n => n.WorkingDirectory ?? "~"));
            Console.WriteLine($"SELFTEST tab[{i}] panes={t.Nodes.Count} cwds={cwds}");
        }

        string screenText = tab?.Active is { } active && TryReadScreenText(active, out string text)
            ? text
            : string.Empty;
        bool sawEcho = screenText.Contains("wraithcairn-selftest-42");
        Console.WriteLine($"SELFTEST echo_output_visible={sawEcho}");

        if (restored && Config.RestoreScrollback)
        {
            bool sawMarker = screenText.Contains("restored scrollback");
            Console.WriteLine($"SELFTEST scrollback_marker_visible={sawMarker}");
        }

        if (tab?.Active is { } searchControl)
        {
            searchControl.StartSearch("wraithcairn-selftest");
            Console.WriteLine($"SELFTEST search_matches={searchControl.SearchTotal}");
            searchControl.EndSearch();
        }

        // Layout switching: no tab strip; the shown layout changes, the
        // others keep their sessions alive in the background.
        if (_tabs.Count > 1)
        {
            Tab first = _tabs[0];
            Tab second = _tabs[1];
            SwitchToLayout(second);
            bool showsSecond = ReferenceEquals(_layoutHost.Content, second.Host);
            bool firstAlive = first.Nodes.Keys.All(c => c.HasActiveSession);
            SwitchToLayout(first);
            bool showsFirst = ReferenceEquals(_layoutHost.Content, first.Host);
            bool secondAlive = second.Nodes.Keys.All(c => c.HasActiveSession);
            Console.WriteLine($"SELFTEST layout_switch={showsSecond && showsFirst} background_alive={firstAlive && secondAlive}");
            Console.WriteLine($"SELFTEST layout_names={string.Join(",", _tabs.Select(t => t.Name))}");
        }

        // Exercise split UX: resize, equalize, zoom toggle round-trip.
        if (tab is not null && tab.Nodes.Count > 1)
        {
            TerminalPaneNode? firstSplit = tab.Root.First is not null ? tab.Root : null;
            double ratioBefore = firstSplit?.Ratio ?? 0.5;
            ResizeSplit(TerminalPaneDirection.Right);
            double ratioAfter = firstSplit?.Ratio ?? 0.5;
            Console.WriteLine($"SELFTEST resize_changed_ratio={Math.Abs(ratioAfter - ratioBefore) > 0.001}");

            EqualizeSplits();
            Console.WriteLine($"SELFTEST equalized={Math.Abs((firstSplit?.Ratio ?? 0.5) - 0.5) < 0.001}");

            ToggleZoom();
            bool zoomedIn = tab.Zoom is not null;
            ToggleZoom();
            bool zoomedOut = tab.Zoom is null;
            Console.WriteLine($"SELFTEST zoom_roundtrip={zoomedIn && zoomedOut}");
        }

        // Multi-window: first phase opens a second window with one tab;
        // restored phase just reports how many windows came back.
        if (!restored)
        {
            // Drive "Pane > Split Right" through the real menu item, on the
            // /tmp tab so the main tab's layout stays as built above.
            Tab? tmpTab = _tabs.Count > 1 ? _tabs[1] : null;
            MenuItem? splitRight = _menu?.Items.OfType<MenuItem>()
                .FirstOrDefault(m => (string?)m.Header == "Pane")?.Items.OfType<MenuItem>()
                .FirstOrDefault(m => (string?)m.Header == "Split Right");
            if (tmpTab is not null && splitRight is not null)
            {
                SwitchToLayout(tmpTab);
                int before = tmpTab.Nodes.Count;
                splitRight.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Task.Delay(1500);
                Console.WriteLine($"SELFTEST menu_split_right={tmpTab.Nodes.Count == before + 1}");
                SwitchToLayout(_tabs[0]);
            }
            else
            {
                Console.WriteLine("SELFTEST menu_split_right=False (menu item missing)");
            }
        }

        if (!restored)
        {
            MainWindow second = new(
                new TerminalWorkspaceWindow
                {
                    Id = "selftest-2",
                    Tabs = [new TerminalWorkspaceTab { Id = "t1", WorkingDirectory = "/tmp" }],
                },
                isPrimary: false);
            second.Show();
            await Task.Delay(1500);
        }

        Console.WriteLine($"SELFTEST windows={Windows.Count}");

        // Keep the primary window as the one restore selects next launch.
        _lastFocused = this;

        SnapshotWindow($"/tmp/wraithcairn-selftest-{phase}.png");
        Console.WriteLine($"SELFTEST snapshot=/tmp/wraithcairn-selftest-{phase}.png");

        SaveWorkspace();
        Console.WriteLine($"SELFTEST workspace={WorkspacePath} exists={File.Exists(WorkspacePath)}");

        if (!restored && NamedLayouts.ActiveName is null && _tabs.Count > 0)
        {
            // Save the active layout to a named file (with a per-pane startup),
            // then open it back as an extra layout in this window.
            const string probe = "selftest-probe";
            Tab src = _tabs[0];
            string firstPaneId = src.Nodes.Values.First().Id;
            Startups[firstPaneId] = new PaneStartup { Directory = "/tmp", Command = "true" };
            TerminalWorkspaceDocument single = new()
            {
                SelectedWindowId = "window-1",
                Windows =
                [
                    new TerminalWorkspaceWindow
                    {
                        Id = "window-1",
                        SelectedTabId = "tab-1",
                        Tabs = [new TerminalWorkspaceTab { Id = "tab-1", Title = probe, RootPane = ToWorkspacePane(src.Root) }],
                    },
                ],
            };
            LayoutFile.Save(NamedLayouts.PathFor(probe), single, LiveStartups());
            bool listed = NamedLayouts.List().Contains(probe);

            int before = _tabs.Count;
            await OpenSavedLayoutAsync(probe);
            Tab? opened = _tabs.Count == before + 1 ? _tabs[^1] : null;
            bool samePaneCount = opened is not null && opened.Nodes.Count == src.Nodes.Count;
            bool startupCarried = opened is not null
                && opened.Nodes.Values.Any(n => StartupFor(n) is { Command: "true" });
            bool idsUnique = opened is not null
                && !opened.Nodes.Values.Select(n => n.Id).Intersect(src.Nodes.Values.Select(n => n.Id)).Any();
            Console.WriteLine($"SELFTEST layout_saved={listed} opened={opened is not null} name={opened?.Name} "
                + $"panes_match={samePaneCount} startup_carried={startupCarried} ids_unique={idsUnique}");

            if (opened is not null)
            {
                CloseTab(opened);
            }

            Startups.Remove(firstPaneId);
            NamedLayouts.Delete(probe);
            SaveWorkspace();
        }
        _shuttingDown = true;
        foreach (MainWindow window in Windows.ToArray())
        {
            if (!ReferenceEquals(window, this))
            {
                window.Close();
            }
        }

        Close();
    }

    /// <summary>
    /// Diagnostic: restores the layout as normal, waits for programs to run,
    /// prints each pane's id, startup, cwd and last screen lines, then exits
    /// WITHOUT saving (so the layout file under test is left untouched).
    /// </summary>
    private async Task DumpPanesAsync()
    {
        await Task.Delay(3000);
        foreach (MainWindow window in Windows.ToArray())
        {
            foreach (Tab tab in window._tabs)
            {
                foreach ((TerminalControl control, TerminalPaneNode node) in tab.Nodes)
                {
                    string startup = StartupFor(node) is { } s
                        ? $"dir={s.Directory ?? "-"} cmd={s.Command ?? "-"} args=[{string.Join("|", s.Args)}] inShell={s.RunInShell}"
                        : "(none)";
                    string text = TryReadScreenText(control, out string screen) ? screen : string.Empty;
                    string tail = string.Join(" / ", text
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .TakeLast(4));
                    Console.WriteLine($"PANE {node.Id} startup: {startup}");
                    Console.WriteLine($"PANE {node.Id} cwd={node.WorkingDirectory ?? "?"} alive={control.HasActiveSession}");
                    Console.WriteLine($"PANE {node.Id} screen: {tail}");
                }
            }
        }

        _shuttingDown = true;
        foreach (MainWindow window in Windows.ToArray())
        {
            window.Close();
        }
    }

    private static bool TryReadScreenText(TerminalControl control, out string text)
    {
        text = string.Empty;
        try
        {
            if (control.TryExportSnapshot(
                    TerminalSnapshotExportFormat.PlainText,
                    new TerminalSnapshotExportOptions(TrimTrailingWhitespace: true),
                    out string snapshot))
            {
                text = snapshot;
                return true;
            }
        }
        catch
        {
            // diagnostics only
        }

        return false;
    }

    /// <summary>
    /// True when Avalonia's compositor exposes the OpenGL texture-sharing
    /// feature, i.e. GPU (GLX/EGL/ANGLE) rendering rather than software.
    /// </summary>
    private bool IsGpuCompositing()
    {
        try
        {
            Avalonia.Rendering.Composition.Compositor? compositor =
                Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(this)?.Compositor;
            if (compositor is null)
            {
                return false;
            }

            object? feature = compositor
                .TryGetRenderInterfaceFeature(
                    typeof(Avalonia.OpenGL.IOpenGlTextureSharingRenderInterfaceContextFeature))
                .GetAwaiter().GetResult();
            return feature is not null;
        }
        catch
        {
            return false;
        }
    }

    private void SnapshotWindow(string path)
    {
        try
        {
            PixelSize size = new((int)Bounds.Width, (int)Bounds.Height);
            if (size.Width <= 0 || size.Height <= 0)
            {
                return;
            }

            using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = new(size, new Vector(96, 96));
            bitmap.Render(this);
            bitmap.Save(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"snapshot failed: {ex.Message}");
        }
    }

    // ----- shortcuts -------------------------------------------------------

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        bool ctrlShift = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (ctrlShift && e.Key == Key.T) { _ = NewTabAsync(null); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.W) { ClosePane(); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.O) { _ = SplitAsync(TerminalPaneSplitOrientation.Horizontal); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.E) { _ = SplitAsync(TerminalPaneSplitOrientation.Vertical); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.Z) { ToggleZoom(); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.P) { _ = ConfigurePaneAsync(); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.R && ActiveTab()?.Active is { } restartTarget) { _ = RestartPaneAsync(restartTarget); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.C && ActiveTab()?.Active is { HasSelection: true } sel) { _ = sel.CopySelectionAsync(); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.V && ActiveTab()?.Active is { } pasteTarget) { _ = pasteTarget.PasteAsync(); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.F) { OpenSearch(); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.K && ActiveTab()?.Active is { } clearTarget) { clearTarget.ClearScrollback(); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.N) { new MainWindow(null, isPrimary: false).Show(); e.Handled = true; }
        else if (ctrlShift && e.Key is Key.OemPlus or Key.Add) { EqualizeSplits(); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.PageDown) { MoveTab(1); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.PageUp) { MoveTab(-1); e.Handled = true; }
        else if (ctrl && e.Key == Key.PageDown) { CycleTab(1); e.Handled = true; }
        else if (ctrl && e.Key == Key.PageUp) { CycleTab(-1); e.Handled = true; }
        else if (ctrl && e.Key is Key.OemPlus or Key.Add) { SetFontSize(_fontSize + 1); e.Handled = true; }
        else if (ctrl && e.Key is Key.OemMinus or Key.Subtract) { SetFontSize(_fontSize - 1); e.Handled = true; }
        else if (ctrl && alt && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            ResizeSplit(e.Key switch
            {
                Key.Left => TerminalPaneDirection.Left,
                Key.Right => TerminalPaneDirection.Right,
                Key.Up => TerminalPaneDirection.Up,
                _ => TerminalPaneDirection.Down,
            });
            e.Handled = true;
        }
        else if (alt && !ctrl && e.Key >= Key.D1 && e.Key <= Key.D9 && (e.Key - Key.D1) < _tabs.Count)
        {
            SwitchToLayout(_tabs[e.Key - Key.D1]);
            e.Handled = true;
        }
        else if (alt && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            MoveFocus(e.Key switch
            {
                Key.Left => TerminalPaneDirection.Left,
                Key.Right => TerminalPaneDirection.Right,
                Key.Up => TerminalPaneDirection.Up,
                _ => TerminalPaneDirection.Down,
            });
            e.Handled = true;
        }
    }

    // ----- terminal / pane creation ---------------------------------------

    private TerminalControl CreateTerminal()
    {
        TerminalControl terminal = new(
            new TerminalSessionService(),
            new DefaultTerminalInputAdapter(),
            new DefaultTerminalSelectionService(),
            new DefaultTerminalScrollService(),
            new DefaultVtProcessorFactory([new GhosttyVtProcessorProvider()]),
            new DefaultPtyFactory())
        {
            VtProcessorPreference = VtProcessorPreference.Auto,
            Columns = 120,
            Rows = 30,
            ScrollbackLimit = 10_000,
            TerminalFontSize = _fontSize,
        };

        if (!string.IsNullOrWhiteSpace(Config.FontFamily))
        {
            terminal.FontFamilyName = Config.FontFamily;
        }

        if (Config.ResolvedTheme is { } theme)
        {
            terminal.ApplyTheme(theme);
        }

        if (Config.CopyOnSelect)
        {
            terminal.SelectionFinalized += (_, _) =>
            {
                if (terminal.HasSelection)
                {
                    _ = terminal.CopySelectionAsync();
                }
            };
        }

        terminal.GotFocus += (_, _) => OnPaneFocused(terminal);

        terminal.TitleChanged += (_, title) => Dispatcher.UIThread.Post(() =>
        {
            Tab? tab = FindTab(terminal);
            if (tab is not null && ReferenceEquals(tab.Active, terminal)
                && !string.IsNullOrWhiteSpace(title))
            {
                tab.TitleFromOsc = true;
                tab.DisplayTitle = Shorten(title, 48);
                if (ReferenceEquals(tab, _activeTab))
                {
                    UpdateWindowTitle(tab);
                }
            }
        });

        terminal.ShellIntegrationEventReceived += (_, args) =>
        {
            if (args.Value.Kind == TerminalShellIntegrationEventKind.WorkingDirectoryChanged
                && args.Value.WorkingDirectory is { Length: > 0 } cwd)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    Tab? tab = FindTab(terminal);
                    if (tab is not null && tab.Nodes.TryGetValue(terminal, out TerminalPaneNode? node))
                    {
                        node.SetWorkingDirectory(NormalizeCwd(cwd));
                        MarkLayoutDirty();
                        if (ReferenceEquals(tab.Active, terminal))
                        {
                            if (!tab.TitleFromOsc)
                            {
                                tab.DisplayTitle = CwdBasename(node.WorkingDirectory);
                                if (ReferenceEquals(tab, _activeTab))
                                {
                                    UpdateWindowTitle(tab);
                                }
                            }

                            UpdateStatus(tab);
                        }
                    }
                });
            }
        };

        terminal.ProcessExited += (_, exitCode) => Dispatcher.UIThread.Post(() => OnPaneProcessExited(terminal, exitCode));
        return terminal;
    }

    /// <summary>
    /// Shell panes close when the shell exits (as before). Exec-mode panes
    /// (a program run directly, no shell) stay open with a notice so the
    /// output can be read; Ctrl+Shift+R restarts the program.
    /// </summary>
    private void OnPaneProcessExited(TerminalControl terminal, int exitCode)
    {
        if (FindTab(terminal) is { } tab
            && tab.Nodes.TryGetValue(terminal, out TerminalPaneNode? node)
            && Startups.TryGetValue(node.Id, out PaneStartup? startup)
            && startup.IsExec)
        {
            terminal.WriteOutput(System.Text.Encoding.UTF8.GetBytes(
                $"\r\n\x1b[0m\x1b[2m[{startup.Command} exited with code {exitCode}. "
                + "Ctrl+Shift+R to restart, Ctrl+Shift+W to close]\x1b[0m\r\n"));
            return;
        }

        ClosePane(terminal);
    }

    private async Task StartSessionAsync(
        TerminalControl terminal,
        string? workingDirectory,
        bool preserveScrollback = false,
        PaneStartup? startup = null)
    {
        Dictionary<string, string> environment = new()
        {
            ["TERM_PROGRAM"] = "wraithcairn",
        };
        if (_shell.Environment is not null)
        {
            foreach ((string key, string value) in _shell.Environment)
            {
                environment[key] = value;
            }
        }

        // Configured directory wins over the last-seen cwd.
        string? directory = startup?.ResolveDirectory()
            ?? (Directory.Exists(workingDirectory) ? workingDirectory : null)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        TerminalCommandSpec? command = startup is { IsExec: true }
            ? new TerminalCommandSpec(startup.Command!.Trim(), startup.Args)
            : _shell.Command;

        PtyTransportOptions options = new(
            Command: command,
            WorkingDirectory: directory,
            Environment: environment,
            Dimensions: new TerminalSessionDimensions(120, 30, 1200, 700));

        await terminal.StartSessionAsync(options, preserveScrollback);

        // Shell mode: type the configured program into the fresh shell.
        if (startup is { HasCommand: true, RunInShell: true })
        {
            terminal.SendInput(startup.BuildShellLine(_shell.ShellName) + "\r");
        }
    }

    private PaneStartup? StartupFor(TerminalPaneNode node) =>
        Startups.TryGetValue(node.Id, out PaneStartup? startup) && !startup.IsEmpty ? startup : null;

    /// <summary>Stops the pane's process and starts it again from its startup config.</summary>
    private async Task RestartPaneAsync(TerminalControl terminal)
    {
        if (FindTab(terminal) is not { } tab || !tab.Nodes.TryGetValue(terminal, out TerminalPaneNode? node))
        {
            return;
        }

        if (terminal.HasActiveSession)
        {
            terminal.StopPty();
        }

        await StartSessionAsync(terminal, node.WorkingDirectory, preserveScrollback: true, StartupFor(node));
        terminal.Focus();
    }

    private TerminalPaneNode CreateLeaf(TerminalControl terminal, string? cwd, string? id = null)
    {
        // Reuse saved ids so per-pane scrollback files line up across runs;
        // keep the counter ahead of any restored numeric id.
        if (id is not null && id.StartsWith("pane-", StringComparison.Ordinal)
            && int.TryParse(id.AsSpan(5), out int n) && n > _paneCounter)
        {
            _paneCounter = n;
        }

        TerminalPaneNode node = new(
            id: id ?? $"pane-{++_paneCounter}",
            workingDirectory: cwd,
            transportId: TerminalTransportIds.Pty);
        Border paneBorder = new()
        {
            Child = TerminalPaneLayout.CreatePaneScrollViewer(terminal),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
        };
        paneBorder.ContextMenu = BuildPaneContextMenu(terminal);
        node.SetLeaf(terminal, paneBorder);
        return node;
    }

    private ContextMenu BuildPaneContextMenu(TerminalControl terminal)
    {
        MenuItem configure = new() { Header = "Configure Pane…" };
        configure.Click += (_, _) => { terminal.Focus(); _ = ConfigurePaneAsync(); };
        MenuItem restart = new() { Header = "Restart Pane" };
        restart.Click += (_, _) => _ = RestartPaneAsync(terminal);
        MenuItem right = new() { Header = "Split Right" };
        right.Click += (_, _) => { terminal.Focus(); _ = SplitAsync(TerminalPaneSplitOrientation.Horizontal); };
        MenuItem down = new() { Header = "Split Down" };
        down.Click += (_, _) => { terminal.Focus(); _ = SplitAsync(TerminalPaneSplitOrientation.Vertical); };
        MenuItem close = new() { Header = "Close Pane" };
        close.Click += (_, _) => ClosePane(terminal);

        ContextMenu menu = new();
        menu.Items.Add(configure);
        menu.Items.Add(restart);
        menu.Items.Add(new Separator());
        menu.Items.Add(right);
        menu.Items.Add(down);
        menu.Items.Add(new Separator());
        menu.Items.Add(close);
        return menu;
    }

    /// <summary>Opens the startup editor for the active pane and applies the result.</summary>
    private async Task ConfigurePaneAsync()
    {
        if (ActiveTab() is not { Active: TerminalControl terminal } tab
            || !tab.Nodes.TryGetValue(terminal, out TerminalPaneNode? node))
        {
            return;
        }

        PaneStartup current = Startups.TryGetValue(node.Id, out PaneStartup? existing)
            ? existing.Clone()
            : new PaneStartup();

        PaneStartupDialog dialog = new(current, node.WorkingDirectory ?? "~", _shell.ShellName, Background);
        PaneStartup? edited = await dialog.ShowDialog(this);
        if (edited is null)
        {
            terminal.Focus();
            return;
        }

        if (edited.IsEmpty)
        {
            Startups.Remove(node.Id);
        }
        else
        {
            Startups[node.Id] = edited;
        }

        MarkLayoutDirty();
        SaveWorkspace();
        UpdateStatus(tab);

        if (dialog.ApplyNow)
        {
            await RestartPaneAsync(terminal);
        }
        else
        {
            terminal.Focus();
        }
    }

    // ----- layouts (formerly tabs) -------------------------------------------

    /// <summary>Creates a new one-pane layout and switches to it.</summary>
    private async Task NewTabAsync(string? cwd, string? name = null)
    {
        TerminalControl terminal = CreateTerminal();
        TerminalPaneNode leaf = CreateLeaf(terminal, cwd);

        Tab tab = MakeTab(leaf, name ?? UniqueLayoutName("layout"));
        tab.Nodes[terminal] = leaf;
        tab.Active = terminal;

        SwitchToLayout(tab);
        await StartSessionAsync(terminal, cwd);
        terminal.Focus();
        UpdateStatus(tab);
        MarkLayoutDirty();
    }

    private Tab MakeTab(TerminalPaneNode root, string name)
    {
        Border host = new() { Child = root.Visual };
        Tab tab = new() { Name = name, Host = host, Root = root };
        _tabs.Add(tab);
        return tab;
    }

    /// <summary>Shows one layout; the others stay alive off-screen.</summary>
    private void SwitchToLayout(Tab tab)
    {
        _activeTab = tab;
        _layoutHost.Content = tab.Host;
        UpdateWindowTitle(tab);
        UpdateStatus(tab);
        UpdateFocusBorders(tab);
        Dispatcher.UIThread.Post(() => tab.Active?.Focus(), DispatcherPriority.Background);
        MarkLayoutDirty();
    }

    private string UniqueLayoutName(string stem)
    {
        HashSet<string> used = new(_tabs.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(stem))
        {
            return stem;
        }

        for (int i = 2; ; i++)
        {
            string candidate = $"{stem} {i}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private void MoveTab(int delta)
    {
        Tab? tab = ActiveTab();
        if (tab is null)
        {
            return;
        }

        int from = _tabs.IndexOf(tab);
        int to = from + delta;
        if (to < 0 || to >= _tabs.Count)
        {
            return;
        }

        _tabs.RemoveAt(from);
        _tabs.Insert(to, tab);
        MarkLayoutDirty();
    }

    private void CycleTab(int delta)
    {
        if (_tabs.Count == 0)
        {
            return;
        }

        int index = ActiveTab() is { } current ? _tabs.IndexOf(current) : 0;
        index = (index + delta + _tabs.Count) % _tabs.Count;
        SwitchToLayout(_tabs[index]);
    }

    private void CloseTab(Tab tab)
    {
        foreach (TerminalControl control in tab.Nodes.Keys.ToArray())
        {
            StopQuietly(control);
        }

        int index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        if (ReferenceEquals(_layoutHost.Content, tab.Host))
        {
            _layoutHost.Content = null;
        }

        MarkLayoutDirty();

        if (_tabs.Count == 0)
        {
            Close();
        }
        else if (ReferenceEquals(_activeTab, tab))
        {
            SwitchToLayout(_tabs[Math.Clamp(index - 1, 0, _tabs.Count - 1)]);
        }
    }

    // ----- splits ----------------------------------------------------------

    private async Task SplitAsync(TerminalPaneSplitOrientation orientation)
    {
        Tab? tab = ActiveTab();
        if (tab?.Active is not TerminalControl activeControl
            || !tab.Nodes.TryGetValue(activeControl, out TerminalPaneNode? activeNode))
        {
            return;
        }

        string? cwd = activeNode.WorkingDirectory;

        TerminalControl newControl = CreateTerminal();
        TerminalPaneNode newLeaf = CreateLeaf(newControl, cwd);
        tab.Nodes[newControl] = newLeaf;

        // Leave zoom before changing the tree.
        if (tab.Zoom is not null)
        {
            ToggleZoom();
        }

        TerminalPaneNode parent = activeNode.Parent is { } p ? p : null!;
        TerminalPaneNode splitNode = new($"pane-{++_paneCounter}");

        // Where did the active pane's visual live?
        Control oldVisual = activeNode.Visual;
        Grid? parentGrid = activeNode.Parent?.SplitGrid;
        int childIndex = -1, row = 0, column = 0;
        if (parentGrid is not null)
        {
            childIndex = parentGrid.Children.IndexOf(oldVisual);
            row = Grid.GetRow(oldVisual);
            column = Grid.GetColumn(oldVisual);
            parentGrid.Children.Remove(oldVisual);
        }
        else
        {
            // Active pane is the tab root: its visual lives in the host Border.
            DetachFromParent(oldVisual);
        }

        Grid grid = TerminalPaneLayout.CreateSplitGrid(
            orientation, 0.5, activeNode.Visual, newLeaf.Visual);
        splitNode.SetSplit(orientation, 0.5, activeNode, newLeaf, grid);

        if (activeNode.Parent is null || parentGrid is null)
        {
            // active pane was the root of the tab
            tab.Root = splitNode;
            splitNode.Parent = null;
            tab.Host.Child = splitNode.Visual;
        }
        else
        {
            TerminalPaneLayout.ReplaceChildPane(parent, activeNode, splitNode, childIndex, row, column);
            activeNode.Parent = splitNode;
        }

        await StartSessionAsync(newControl, cwd);
        newControl.Focus();
        UpdateStatus(tab);
        MarkLayoutDirty();
    }

    private void ClosePane(TerminalControl? target = null)
    {
        Tab? tab = target is null ? ActiveTab() : FindTab(target);
        if (tab is null)
        {
            return;
        }

        TerminalControl? control = target ?? tab.Active;
        if (control is null || !tab.Nodes.TryGetValue(control, out TerminalPaneNode? node))
        {
            return;
        }

        // Leave zoom before changing the tree.
        if (tab.Zoom is not null)
        {
            ToggleZoom();
        }

        if (node.Parent is not TerminalPaneNode parent)
        {
            CloseTab(tab);
            return;
        }

        TerminalPaneNode sibling = ReferenceEquals(parent.First, node) ? parent.Second! : parent.First!;

        StopQuietly(control);
        tab.Nodes.Remove(control);

        // Promote the sibling into the parent's slot.
        TerminalPaneNode? grandparent = parent.Parent;
        if (grandparent is null)
        {
            tab.Root = sibling;
            sibling.Parent = null;
            DetachFromParent(sibling.Visual);
            tab.Host.Child = sibling.Visual;
        }
        else
        {
            Grid grandGrid = grandparent.SplitGrid!;
            int childIndex = grandGrid.Children.IndexOf(parent.Visual);
            int row = Grid.GetRow(parent.Visual);
            int column = Grid.GetColumn(parent.Visual);
            grandGrid.Children.Remove(parent.Visual);
            DetachFromParent(sibling.Visual);
            TerminalPaneLayout.ReplaceChildPane(grandparent, parent, sibling, childIndex, row, column);
        }

        tab.Active = TerminalPaneLayout.CollectLeafControls(tab.Root).FirstOrDefault();
        tab.Active?.Focus();
        UpdateStatus(tab);
        MarkLayoutDirty();
    }

    private static void DetachFromParent(Control visual)
    {
        if (visual.Parent is Panel panel)
        {
            panel.Children.Remove(visual);
        }
        else if (visual.Parent is Border border)
        {
            border.Child = null;
        }
    }

    private void MoveFocus(TerminalPaneDirection direction)
    {
        Tab? tab = ActiveTab();
        if (tab?.Active is not TerminalControl active)
        {
            return;
        }

        IReadOnlyList<TerminalControl> leaves = TerminalPaneLayout.CollectLeafControls(tab.Root);
        TerminalControl? next =
            TerminalPaneLayout.FindDirectionalPane(leaves, tab.Nodes, tab.Root.Visual, active, direction)
            ?? TerminalPaneLayout.FindSequentialPane(
                leaves,
                active,
                forward: direction is TerminalPaneDirection.Right or TerminalPaneDirection.Down);
        next?.Focus();
    }

    private void OnPaneFocused(TerminalControl terminal)
    {
        Tab? tab = FindTab(terminal);
        if (tab is null)
        {
            return;
        }

        tab.Active = terminal;
        UpdateFocusBorders(tab);
        UpdateStatus(tab);
        UpdateWindowTitle(tab);
    }

    private static void UpdateFocusBorders(Tab tab)
    {
        bool multiplePanes = tab.Nodes.Count > 1;
        foreach ((TerminalControl control, TerminalPaneNode node) in tab.Nodes)
        {
            if (node.LeafContainer is Border border)
            {
                border.BorderBrush = multiplePanes && ReferenceEquals(control, tab.Active)
                    ? new SolidColorBrush(Color.Parse("#89b4fa"))
                    : Brushes.Transparent;
            }
        }
    }

    // ----- search ----------------------------------------------------------

    private void OpenSearch()
    {
        _searchBar.IsVisible = true;
        _searchBox.SelectAll();
        _searchBox.Focus();
    }

    private void CloseSearch()
    {
        _searchBar.IsVisible = false;
        if (ActiveTab()?.Active is { } control)
        {
            control.EndSearch();
            control.Focus();
        }
    }

    private void RunSearch(bool select)
    {
        if (!_searchBar.IsVisible || ActiveTab()?.Active is not TerminalControl control)
        {
            return;
        }

        string needle = _searchBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(needle))
        {
            control.EndSearch();
            return;
        }

        control.StartSearch(needle);
        if (select)
        {
            control.SelectNextSearchMatch();
        }
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ActiveTab()?.Active is { } control)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                control.SelectPreviousSearchMatch();
            }
            else if (!control.SelectNextSearchMatch())
            {
                RunSearch(select: true);
            }

            e.Handled = true;
        }
    }

    // ----- zoom / resize ----------------------------------------------------

    /// <summary>Temporarily maximize the active pane inside its tab.</summary>
    private void ToggleZoom()
    {
        Tab? tab = ActiveTab();
        if (tab is null)
        {
            return;
        }

        if (tab.Zoom is ZoomState zoom)
        {
            // Restore: put the leaf's visual back into its parent grid slot.
            DetachFromParent(zoom.Leaf.Visual);
            Grid.SetRow(zoom.Leaf.Visual, zoom.Row);
            Grid.SetColumn(zoom.Leaf.Visual, zoom.Column);
            zoom.ParentGrid.Children.Insert(
                Math.Min(zoom.ChildIndex, zoom.ParentGrid.Children.Count), zoom.Leaf.Visual);
            tab.Host.Child = tab.Root.Visual;
            tab.Zoom = null;
            tab.Active?.Focus();
            return;
        }

        if (tab.Active is not TerminalControl active
            || !tab.Nodes.TryGetValue(active, out TerminalPaneNode? leaf)
            || leaf.Parent?.SplitGrid is not Grid parentGrid)
        {
            return; // single pane, nothing to zoom
        }

        tab.Zoom = new ZoomState
        {
            Leaf = leaf,
            ParentGrid = parentGrid,
            ChildIndex = parentGrid.Children.IndexOf(leaf.Visual),
            Row = Grid.GetRow(leaf.Visual),
            Column = Grid.GetColumn(leaf.Visual),
        };
        parentGrid.Children.Remove(leaf.Visual);
        tab.Host.Child = leaf.Visual;
        active.Focus();
    }

    private void ResizeSplit(TerminalPaneDirection direction)
    {
        Tab? tab = ActiveTab();
        if (tab?.Active is not TerminalControl active
            || !tab.Nodes.TryGetValue(active, out TerminalPaneNode? node))
        {
            return;
        }

        bool wantsHorizontal = direction is TerminalPaneDirection.Left or TerminalPaneDirection.Right;
        TerminalPaneNode? split = TerminalPaneLayout.FindResizeSplit(node, wantsHorizontal);
        if (split is null)
        {
            return;
        }

        double delta = TerminalPaneLayout.GetFocusedPaneResizeDelta(split, node, direction);
        TerminalPaneLayout.ApplySplitRatio(split, split.Ratio + delta);
        MarkLayoutDirty();
    }

    private void EqualizeSplits()
    {
        Tab? tab = ActiveTab();
        if (tab is null)
        {
            return;
        }

        Equalize(tab.Root);
        MarkLayoutDirty();

        static void Equalize(TerminalPaneNode node)
        {
            if (node.First is null || node.Second is null)
            {
                return;
            }

            TerminalPaneLayout.ApplySplitRatio(node, 0.5);
            Equalize(node.First);
            Equalize(node.Second);
        }
    }

    // ----- workspace save / restore ----------------------------------------

    private void SaveWorkspace()
    {
        if (_saving)
        {
            return;
        }

        _saving = true;
        try
        {
            MainWindow focused = _lastFocused is { } lf && Windows.Contains(lf) ? lf : this;
            TerminalWorkspaceDocument document = new()
            {
                SelectedWindowId = focused.WindowId(),
                Windows = Windows.Select(w => w.ToWorkspaceWindow()).ToList(),
            };

            LayoutFile.Save(WorkspacePath, document, LiveStartups());

            if (Config.RestoreScrollback)
            {
                ScrollbackStore.SaveAll(Windows
                    .SelectMany(w => w._tabs)
                    .SelectMany(t => t.Nodes)
                    .Select(kv => (kv.Value.Id, kv.Key)));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"workspace save failed: {ex.Message}");
        }
        finally
        {
            _saving = false;
        }
    }

    private string WindowId() => $"window-{Windows.IndexOf(this) + 1}";

    /// <summary>Startup entries for panes that currently exist (drops closed panes).</summary>
    private static Dictionary<string, PaneStartup> LiveStartups()
    {
        Dictionary<string, PaneStartup> live = new(StringComparer.Ordinal);
        foreach (TerminalPaneNode node in Windows.SelectMany(w => w._tabs).SelectMany(t => t.Nodes.Values))
        {
            if (Startups.TryGetValue(node.Id, out PaneStartup? startup) && !startup.IsEmpty)
            {
                live[node.Id] = startup;
            }
        }

        return live;
    }

    private TerminalWorkspaceWindow ToWorkspaceWindow() => new()
    {
        Id = WindowId(),
        Title = Title,
        WidthPixels = (int)Width,
        HeightPixels = (int)Height,
        IsMaximized = WindowState == WindowState.Maximized,
        SelectedTabId = ActiveTab() is { } active ? TabId(active) : null,
        Tabs = _tabs.Select(tab => new TerminalWorkspaceTab
        {
            Id = TabId(tab),
            Title = tab.Name,
            WorkingDirectory = tab.Active is { } control
                && tab.Nodes.TryGetValue(control, out TerminalPaneNode? node)
                ? node.WorkingDirectory
                : null,
            RootPane = ToWorkspacePane(tab.Root),
        }).ToList(),
    };

    private string TabId(Tab tab) => $"{WindowId()}-tab-{_tabs.IndexOf(tab) + 1}";

    private static TerminalWorkspacePane ToWorkspacePane(TerminalPaneNode node)
    {
        if (node.Orientation is not TerminalPaneSplitOrientation orientation
            || node.First is null || node.Second is null)
        {
            return new TerminalWorkspacePane
            {
                Id = node.Id,
                WorkingDirectory = node.WorkingDirectory,
                TransportId = node.TransportId,
            };
        }

        return new TerminalWorkspacePane
        {
            Id = node.Id,
            Split = new TerminalWorkspacePaneSplit
            {
                Orientation = orientation == TerminalPaneSplitOrientation.Horizontal
                    ? TerminalWorkspacePaneSplitOrientations.Horizontal
                    : TerminalWorkspacePaneSplitOrientations.Vertical,
                Ratio = CurrentRatio(node),
                FirstPane = ToWorkspacePane(node.First),
                SecondPane = ToWorkspacePane(node.Second),
            },
        };
    }

    /// <summary>Reads the live splitter ratio back from the grid star sizes.</summary>
    private static double CurrentRatio(TerminalPaneNode split)
    {
        Grid? grid = split.SplitGrid;
        if (grid is null)
        {
            return split.Ratio;
        }

        double first, second;
        if (split.Orientation == TerminalPaneSplitOrientation.Vertical)
        {
            if (grid.RowDefinitions.Count < 3) return split.Ratio;
            first = grid.RowDefinitions[0].Height.Value;
            second = grid.RowDefinitions[2].Height.Value;
        }
        else
        {
            if (grid.ColumnDefinitions.Count < 3) return split.Ratio;
            first = grid.ColumnDefinitions[0].Width.Value;
            second = grid.ColumnDefinitions[2].Width.Value;
        }

        double total = first + second;
        return total > 0 ? first / total : split.Ratio;
    }

    private async Task RestoreWorkspaceAsync()
    {
        _restoring = true;
        try
        {
            await RestoreWorkspaceCoreAsync();
        }
        finally
        {
            _restoring = false;
            _layoutDirty = false;
        }
    }

    private async Task RestoreWorkspaceCoreAsync()
    {
        TerminalWorkspaceWindow? window = _pendingRestore;
        List<TerminalWorkspaceWindow> siblings = [];

        if (window is null && Config.RestoreLayout)
        {
            TerminalWorkspaceDocument document;
            try
            {
                (document, Dictionary<string, PaneStartup> loaded) = LayoutFile.Load(WorkspacePath);
                foreach ((string id, PaneStartup startup) in loaded)
                {
                    Startups[id] = startup;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"workspace load failed: {ex.Message}");
                document = new TerminalWorkspaceDocument();
            }

            // Primary window takes the selected (or first) saved window and
            // spawns one MainWindow per remaining saved window.
            window = document.Windows.FirstOrDefault(w => w.Id == document.SelectedWindowId)
                ?? document.Windows.FirstOrDefault();
            siblings = document.Windows.Where(w => !ReferenceEquals(w, window)).ToList();
        }

        if (window is null || window.Tabs.Count == 0)
        {
            await NewTabAsync(null);
            return;
        }

        Width = Math.Max(400, window.WidthPixels);
        Height = Math.Max(300, window.HeightPixels);
        if (window.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        List<Task> sessions = [];
        Tab? selected = null;

        foreach (TerminalWorkspaceTab savedTab in window.Tabs)
        {
            (TerminalPaneNode root, Dictionary<TerminalControl, TerminalPaneNode> nodes) =
                BuildPaneTree(savedTab.RootPane, sessions);

            Tab tab = MakeTab(root, savedTab.Title is { Length: > 0 } savedName
                ? UniqueLayoutName(savedName)
                : UniqueLayoutName("layout"));
            foreach ((TerminalControl control, TerminalPaneNode node) in nodes)
            {
                tab.Nodes[control] = node;
            }

            tab.Active = TerminalPaneLayout.CollectLeafControls(root).FirstOrDefault();

            if (savedTab.Id == window.SelectedTabId)
            {
                selected = tab;
            }
        }

        selected ??= _tabs.FirstOrDefault();
        if (selected is not null)
        {
            SwitchToLayout(selected);
        }

        await Task.WhenAll(sessions);
        selected?.Active?.Focus();
        if (selected is not null)
        {
            UpdateStatus(selected);
        }

        // Open the other saved windows (primary window only).
        foreach (TerminalWorkspaceWindow sibling in siblings)
        {
            if (sibling.Tabs.Count > 0)
            {
                new MainWindow(sibling, isPrimary: false).Show();
            }
        }
    }

    private (TerminalPaneNode Root, Dictionary<TerminalControl, TerminalPaneNode> Nodes) BuildPaneTree(
        TerminalWorkspacePane saved,
        List<Task> sessions)
    {
        Dictionary<TerminalControl, TerminalPaneNode> nodes = [];
        TerminalPaneNode root = BuildPaneNode(saved, nodes, sessions);
        return (root, nodes);
    }

    private TerminalPaneNode BuildPaneNode(
        TerminalWorkspacePane saved,
        Dictionary<TerminalControl, TerminalPaneNode> nodes,
        List<Task> sessions)
    {
        if (saved.Split is not TerminalWorkspacePaneSplit split)
        {
            TerminalControl terminal = CreateTerminal();
            TerminalPaneNode leaf = CreateLeaf(terminal, saved.WorkingDirectory, saved.Id);
            nodes[terminal] = leaf;
            bool replayed = Config.RestoreScrollback && ScrollbackStore.TryRestore(saved.Id, terminal);
            if (replayed)
            {
                _scrollbackRestored++;
            }

            sessions.Add(StartSessionAsync(
                terminal,
                saved.WorkingDirectory,
                preserveScrollback: replayed,
                startup: StartupFor(leaf)));
            return leaf;
        }

        TerminalPaneNode first = BuildPaneNode(split.FirstPane, nodes, sessions);
        TerminalPaneNode second = BuildPaneNode(split.SecondPane, nodes, sessions);

        TerminalPaneSplitOrientation orientation =
            split.Orientation == TerminalWorkspacePaneSplitOrientations.Vertical
                ? TerminalPaneSplitOrientation.Vertical
                : TerminalPaneSplitOrientation.Horizontal;

        Grid grid = TerminalPaneLayout.CreateSplitGrid(orientation, split.Ratio, first.Visual, second.Visual);
        TerminalPaneNode node = new(saved.Id is { Length: > 0 } splitId ? splitId : $"pane-{++_paneCounter}");
        if (saved.Id is not null && saved.Id.StartsWith("pane-", StringComparison.Ordinal)
            && int.TryParse(saved.Id.AsSpan(5), out int splitN) && splitN > _paneCounter)
        {
            _paneCounter = splitN;
        }

        node.SetSplit(orientation, split.Ratio, first, second, grid);
        return node;
    }

    // ----- misc -------------------------------------------------------------

    private void SetFontSize(double size)
    {
        _fontSize = Math.Clamp(size, 8, 32);
        foreach (Tab tab in _tabs)
        {
            foreach (TerminalControl control in tab.Nodes.Keys)
            {
                control.TerminalFontSize = _fontSize;
            }
        }
    }

    private static void StopQuietly(TerminalControl control)
    {
        try
        {
            control.StopPty();
        }
        catch
        {
            // process may already be gone
        }
    }

    private Tab? ActiveTab() =>
        _activeTab is not null && _tabs.Contains(_activeTab) ? _activeTab : null;

    private Tab? FindTab(TerminalControl control) =>
        _tabs.FirstOrDefault(t => t.Nodes.ContainsKey(control));

    private void UpdateWindowTitle(Tab tab)
    {
        string detail = tab.DisplayTitle is { Length: > 0 } d && !string.Equals(d, tab.Name, StringComparison.Ordinal)
            ? $" — {d}"
            : string.Empty;
        string count = _tabs.Count > 1 ? $" ({_tabs.IndexOf(tab) + 1}/{_tabs.Count})" : string.Empty;
        Title = $"{tab.Name}{count}{detail} — wraithcairn";
    }

    private void UpdateStatus(Tab tab)
    {
        string engine = tab.Active?.VtProcessorPreference.ToString() ?? "?";
        string ghostty = GhosttyVtProcessor.IsAvailable() ? "libghostty-vt" : "managed VT";
        string cwd = tab.Active is { } c && tab.Nodes.TryGetValue(c, out TerminalPaneNode? node)
            ? node.WorkingDirectory ?? "~"
            : "~";
        int panes = tab.Nodes.Count;
        string integrated = _shell.Integrated ? $"{_shell.ShellName}+osc7" : _shell.ShellName;
        string program = tab.Active is { } ac && tab.Nodes.TryGetValue(ac, out TerminalPaneNode? an)
            && StartupFor(an) is { HasCommand: true } su
                ? $"  |  runs: {su.Command} {string.Join(' ', su.Args)}".TrimEnd()
                : string.Empty;
        _status.Text = $"{ghostty}  |  {integrated}  |  {panes} pane(s)  |  {cwd}{program}  |  layout -> {WorkspacePath}";
    }

    private static string NormalizeCwd(string cwd)
    {
        if (cwd.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(cwd, UriKind.Absolute, out Uri? uri))
        {
            cwd = uri.LocalPath;
        }

        // pwsh's OSC 7 can yield a doubled leading slash via Uri.LocalPath.
        if (!OperatingSystem.IsWindows())
        {
            while (cwd.StartsWith("//", StringComparison.Ordinal))
            {
                cwd = cwd[1..];
            }
        }

        return cwd;
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string CwdBasename(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return "shell";
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.Equals(Path.TrimEndingDirectorySeparator(cwd), home, StringComparison.Ordinal))
        {
            return "~";
        }

        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(cwd));
        return string.IsNullOrEmpty(name) ? cwd : name;
    }
}
