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

namespace SpikeRoyalTerminal;

/// <summary>
/// Spike: a cross-platform tabbed + split terminal on RoyalTerminal
/// (libghostty-vt engine) that saves its layout (tabs, splits, ratios,
/// per-pane cwd) to workspace.json and restores it on next start.
///
/// Keys:
///   Ctrl+Shift+T        new tab (or click +)
///   Ctrl+Shift+W        close pane (last pane closes the tab)
///   Ctrl+Shift+O        split right          Ctrl+Shift+E  split down
///   Alt+Arrows          move focus between panes
///   Ctrl+Alt+Arrows     resize split          Ctrl+Shift+Z  zoom pane
///   Ctrl+Shift+=        equalize splits
///   Ctrl+Shift+C / V    copy selection / paste
///   Ctrl+Shift+F        find (Enter next, Shift+Enter prev, Esc close)
///   Ctrl+Shift+K        clear scrollback
///   Ctrl+Shift+N        new window
///   Ctrl+PgUp/PgDn      previous / next tab
///   Ctrl+Shift+PgUp/PgDn move tab left / right
///   Ctrl+= / Ctrl+-     font size
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly string WorkspacePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "spike-royalterminal",
        "workspace.json");

    private readonly ITerminalWorkspaceStore _workspaceStore =
        TerminalWorkspaceStoreFactory.CreateDefault(WorkspacePath);

    private readonly TabControl _tabStrip;
    private readonly TextBlock _status;
    private readonly TextBox _searchBox;
    private readonly Border _searchBar;
    private readonly ShellIntegration.LaunchSpec _shell = ShellIntegration.Resolve();
    private double _fontSize = 11;
    private static int _paneCounter;

    private sealed class Tab
    {
        public required TabItem Item { get; init; }
        public required TextBlock Header { get; init; }
        public required Border Host { get; init; }
        public required TerminalPaneNode Root { get; set; }
        public TerminalControl? Active { get; set; }

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
    private readonly TabItem _plusTab;
    private readonly DispatcherTimer _autosave;
    private System.Runtime.InteropServices.PosixSignalRegistration? _sigterm;
    private bool _layoutDirty;
    private bool _restoring;

    // ----- multi-window registry -------------------------------------------

    private static readonly List<MainWindow> Windows = [];
    private static MainWindow? _lastFocused;
    private static bool _saving;
    private static bool _shuttingDown;
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
        Windows.Add(this);
        Activated += (_, _) => _lastFocused = this;
        Title = "spike-royalterminal";
        Width = 1200;
        Height = 800;
        Background = new SolidColorBrush(Color.Parse("#1e1e2e"));

        _tabStrip = new TabControl
        {
            Padding = new Thickness(0),
        };

        // Trailing pseudo-tab that acts as a "new tab" button.
        _plusTab = new TabItem
        {
            Header = new TextBlock { Text = "+", FontSize = 14, Margin = new Thickness(4, 0) },
            Content = new Border(),
        };
        _tabStrip.Items.Add(_plusTab);
        _tabStrip.SelectionChanged += (_, _) =>
        {
            if (!_restoring && ReferenceEquals(_tabStrip.SelectedItem, _plusTab) && _tabs.Count > 0)
            {
                _ = NewTabAsync(null);
            }
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
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(_searchBar, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(_searchBar);
        root.Children.Add(_tabStrip);
        Content = root;

        // Intercept our shortcuts before the terminal control eats the keys.
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);

        Opened += async (_, _) =>
        {
            try
            {
                await RestoreWorkspaceAsync();
                if (_isPrimary && Environment.GetCommandLineArgs().Contains("--selftest"))
                {
                    await RunSelfTestAsync();
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
            await NewTabAsync("/tmp");
            _tabStrip.SelectedIndex = 0;
        }

        Tab? tab = ActiveTab();
        tab?.Active?.SendInput("echo spike-selftest-$((6*7))\n");
        await Task.Delay(2500);

        string phase = restored ? "restored" : "initial";
        Console.WriteLine($"SELFTEST phase={phase}");
        Console.WriteLine($"SELFTEST ghostty={RoyalTerminal.Terminal.GhosttyVtProcessor.IsAvailable()}");
        Console.WriteLine($"SELFTEST gpu_gl={IsGpuCompositing()}");
        Console.WriteLine($"SELFTEST shell={_shell.ShellName} integrated={_shell.Integrated}");
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
        bool sawEcho = screenText.Contains("spike-selftest-42");
        Console.WriteLine($"SELFTEST echo_output_visible={sawEcho}");

        if (tab?.Active is { } searchControl)
        {
            searchControl.StartSearch("spike-selftest");
            Console.WriteLine($"SELFTEST search_matches={searchControl.SearchTotal}");
            searchControl.EndSearch();
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

        SnapshotWindow($"/tmp/spike-selftest-{phase}.png");
        Console.WriteLine($"SELFTEST snapshot=/tmp/spike-selftest-{phase}.png");

        SaveWorkspace();
        Console.WriteLine($"SELFTEST workspace={WorkspacePath} exists={File.Exists(WorkspacePath)}");
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

        terminal.GotFocus += (_, _) => OnPaneFocused(terminal);

        terminal.TitleChanged += (_, title) => Dispatcher.UIThread.Post(() =>
        {
            Tab? tab = FindTab(terminal);
            if (tab is not null && ReferenceEquals(tab.Active, terminal)
                && !string.IsNullOrWhiteSpace(title))
            {
                tab.TitleFromOsc = true;
                tab.Header.Text = Shorten(title, 32);
                UpdateWindowTitle(tab);
                MarkLayoutDirty();
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
                                tab.Header.Text = CwdBasename(node.WorkingDirectory);
                                UpdateWindowTitle(tab);
                            }

                            UpdateStatus(tab);
                        }
                    }
                });
            }
        };

        terminal.ProcessExited += (_, _) => Dispatcher.UIThread.Post(() => ClosePane(terminal));
        return terminal;
    }

    private async Task StartSessionAsync(TerminalControl terminal, string? workingDirectory)
    {
        Dictionary<string, string> environment = new()
        {
            ["TERM_PROGRAM"] = "spike-royalterminal",
        };
        if (_shell.Environment is not null)
        {
            foreach ((string key, string value) in _shell.Environment)
            {
                environment[key] = value;
            }
        }

        PtyTransportOptions options = new(
            Command: _shell.Command,
            WorkingDirectory: Directory.Exists(workingDirectory)
                ? workingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment: environment,
            Dimensions: new TerminalSessionDimensions(120, 30, 1200, 700));

        await terminal.StartSessionAsync(options);
    }

    private TerminalPaneNode CreateLeaf(TerminalControl terminal, string? cwd)
    {
        TerminalPaneNode node = new(
            id: $"pane-{++_paneCounter}",
            workingDirectory: cwd,
            transportId: TerminalTransportIds.Pty);
        Border paneBorder = new()
        {
            Child = TerminalPaneLayout.CreatePaneScrollViewer(terminal),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
        };
        node.SetLeaf(terminal, paneBorder);
        return node;
    }

    // ----- tabs ------------------------------------------------------------

    private async Task NewTabAsync(string? cwd)
    {
        TerminalControl terminal = CreateTerminal();
        TerminalPaneNode leaf = CreateLeaf(terminal, cwd);

        Tab tab = MakeTab(leaf, "shell");
        tab.Nodes[terminal] = leaf;
        tab.Active = terminal;

        _tabStrip.SelectedItem = tab.Item;
        await StartSessionAsync(terminal, cwd);
        terminal.Focus();
        UpdateStatus(tab);
        MarkLayoutDirty();
    }

    private Tab MakeTab(TerminalPaneNode root, string title)
    {
        TextBlock header = new()
        {
            Text = title,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Button closeButton = new()
        {
            Content = "\u00d7",
            FontSize = 12,
            Padding = new Thickness(4, 0),
            Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        StackPanel headerPanel = new()
        {
            Orientation = Orientation.Horizontal,
        };
        headerPanel.Children.Add(header);
        headerPanel.Children.Add(closeButton);

        Border host = new() { Child = root.Visual };
        TabItem item = new() { Header = headerPanel, Content = host };

        Tab tab = new() { Item = item, Header = header, Host = host, Root = root };
        closeButton.Click += (_, _) => CloseTab(tab);
        item.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(item).Properties.IsMiddleButtonPressed)
            {
                CloseTab(tab);
                e.Handled = true;
            }
        };

        _tabs.Add(tab);
        // Keep the "+" pseudo-tab last.
        _tabStrip.Items.Insert(_tabStrip.Items.Count - 1, item);
        return tab;
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
        _tabStrip.Items.Remove(tab.Item);
        _tabStrip.Items.Insert(to, tab.Item);
        _tabStrip.SelectedItem = tab.Item;
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
        _tabStrip.SelectedItem = _tabs[index].Item;
        _tabs[index].Active?.Focus();
    }

    private void CloseTab(Tab tab)
    {
        foreach (TerminalControl control in tab.Nodes.Keys.ToArray())
        {
            StopQuietly(control);
        }

        _tabs.Remove(tab);
        _tabStrip.Items.Remove(tab.Item);
        MarkLayoutDirty();

        if (_tabs.Count == 0)
        {
            Close();
        }
        else
        {
            // Select explicitly so Avalonia doesn't land on the "+" pseudo-tab.
            Tab next = _tabs[^1];
            _tabStrip.SelectedItem = next.Item;
            next.Active?.Focus();
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

            _workspaceStore.SaveAsync(document).AsTask().GetAwaiter().GetResult();
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
            Title = tab.Header.Text,
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

        if (window is null)
        {
            TerminalWorkspaceDocument document;
            try
            {
                document = await _workspaceStore.LoadAsync();
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

            Tab tab = MakeTab(root, savedTab.Title ?? "shell");
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
            _tabStrip.SelectedItem = selected.Item;
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
            TerminalPaneNode leaf = CreateLeaf(terminal, saved.WorkingDirectory);
            nodes[terminal] = leaf;
            sessions.Add(StartSessionAsync(terminal, saved.WorkingDirectory));
            return leaf;
        }

        TerminalPaneNode first = BuildPaneNode(split.FirstPane, nodes, sessions);
        TerminalPaneNode second = BuildPaneNode(split.SecondPane, nodes, sessions);

        TerminalPaneSplitOrientation orientation =
            split.Orientation == TerminalWorkspacePaneSplitOrientations.Vertical
                ? TerminalPaneSplitOrientation.Vertical
                : TerminalPaneSplitOrientation.Horizontal;

        Grid grid = TerminalPaneLayout.CreateSplitGrid(orientation, split.Ratio, first.Visual, second.Visual);
        TerminalPaneNode node = new($"pane-{++_paneCounter}");
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
        _tabStrip.SelectedItem is TabItem item ? _tabs.FirstOrDefault(t => t.Item == item) : null;

    private Tab? FindTab(TerminalControl control) =>
        _tabs.FirstOrDefault(t => t.Nodes.ContainsKey(control));

    private void UpdateWindowTitle(Tab tab) =>
        Title = $"{tab.Header.Text} — spike-royalterminal";

    private void UpdateStatus(Tab tab)
    {
        string engine = tab.Active?.VtProcessorPreference.ToString() ?? "?";
        string ghostty = GhosttyVtProcessor.IsAvailable() ? "libghostty-vt" : "managed VT";
        string cwd = tab.Active is { } c && tab.Nodes.TryGetValue(c, out TerminalPaneNode? node)
            ? node.WorkingDirectory ?? "~"
            : "~";
        int panes = tab.Nodes.Count;
        string integrated = _shell.Integrated ? $"{_shell.ShellName}+osc7" : _shell.ShellName;
        _status.Text = $"{ghostty}  |  {integrated}  |  {panes} pane(s)  |  {cwd}  |  layout -> {WorkspacePath}";
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
