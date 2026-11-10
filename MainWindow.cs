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
///   Ctrl+Shift+T        new tab
///   Ctrl+Shift+W        close pane (last pane closes the tab)
///   Ctrl+Shift+O        split right          Ctrl+Shift+E  split down
///   Alt+Arrows          move focus between panes
///   Ctrl+PgUp/PgDn      previous / next tab
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
    private readonly string? _bashRcFile;
    private double _fontSize = 13;
    private int _paneCounter;

    private sealed class Tab
    {
        public required TabItem Item { get; init; }
        public required TextBlock Header { get; init; }
        public required Border Host { get; init; }
        public required TerminalPaneNode Root { get; set; }
        public TerminalControl? Active { get; set; }

        public Dictionary<TerminalControl, TerminalPaneNode> Nodes { get; } = [];
    }

    private readonly List<Tab> _tabs = [];

    public MainWindow()
    {
        Title = "spike-royalterminal";
        Width = 1200;
        Height = 800;
        Background = new SolidColorBrush(Color.Parse("#1e1e2e"));

        _bashRcFile = TryWriteBashIntegrationRcFile();

        _tabStrip = new TabControl
        {
            Padding = new Thickness(0),
        };

        _status = new TextBlock
        {
            Margin = new Thickness(8, 3),
            FontSize = 12,
            Foreground = Brushes.Gray,
        };

        DockPanel root = new();
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(_tabStrip);
        Content = root;

        // Intercept our shortcuts before the terminal control eats the keys.
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);

        Opened += async (_, _) =>
        {
            try
            {
                await RestoreWorkspaceAsync();
                if (Environment.GetCommandLineArgs().Contains("--selftest"))
                {
                    await RunSelfTestAsync();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"startup failed: {ex}");
            }
        };
        Closing += (_, _) => SaveWorkspace();
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
        bool restored = _tabs.Count > 1 || (_tabs.Count == 1 && _tabs[0].Nodes.Count > 1);

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

        SnapshotWindow($"/tmp/spike-selftest-{phase}.png");
        Console.WriteLine($"SELFTEST snapshot=/tmp/spike-selftest-{phase}.png");

        SaveWorkspace();
        Console.WriteLine($"SELFTEST workspace={WorkspacePath} exists={File.Exists(WorkspacePath)}");
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
        else if (ctrl && e.Key == Key.PageDown) { CycleTab(1); e.Handled = true; }
        else if (ctrl && e.Key == Key.PageUp) { CycleTab(-1); e.Handled = true; }
        else if (ctrl && e.Key is Key.OemPlus or Key.Add) { SetFontSize(_fontSize + 1); e.Handled = true; }
        else if (ctrl && e.Key is Key.OemMinus or Key.Subtract) { SetFontSize(_fontSize - 1); e.Handled = true; }
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
            if (tab is not null && ReferenceEquals(tab.Active, terminal))
            {
                tab.Header.Text = Shorten(title, 32);
                UpdateWindowTitle(tab);
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
                        if (ReferenceEquals(tab.Active, terminal))
                        {
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
        TerminalCommandSpec? command = null;
        if (_bashRcFile is not null)
        {
            command = new TerminalCommandSpec("/bin/bash", ["--rcfile", _bashRcFile]);
        }

        PtyTransportOptions options = new(
            Command: command,
            WorkingDirectory: Directory.Exists(workingDirectory)
                ? workingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment: new Dictionary<string, string> { ["TERM_PROGRAM"] = "spike-royalterminal" },
            Dimensions: new TerminalSessionDimensions(120, 30, 1200, 700));

        await terminal.StartSessionAsync(options);
    }

    private TerminalPaneNode CreateLeaf(TerminalControl terminal, string? cwd)
    {
        TerminalPaneNode node = new(
            id: $"pane-{++_paneCounter}",
            workingDirectory: cwd,
            transportId: TerminalTransportIds.Pty);
        node.SetLeaf(terminal, TerminalPaneLayout.CreatePaneScrollViewer(terminal));
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
    }

    private Tab MakeTab(TerminalPaneNode root, string title)
    {
        TextBlock header = new() { Text = title, FontSize = 12 };
        Border host = new() { Child = root.Visual };
        TabItem item = new() { Header = header, Content = host };

        Tab tab = new() { Item = item, Header = header, Host = host, Root = root };
        _tabs.Add(tab);
        _tabStrip.Items.Add(item);
        return tab;
    }

    private void CycleTab(int delta)
    {
        if (_tabs.Count == 0)
        {
            return;
        }

        int index = _tabStrip.SelectedIndex;
        index = (index + delta + _tabs.Count) % _tabs.Count;
        _tabStrip.SelectedIndex = index;
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

        if (_tabs.Count == 0)
        {
            Close();
        }
        else
        {
            _tabs[^1].Active?.Focus();
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
        UpdateStatus(tab);
        UpdateWindowTitle(tab);
    }

    // ----- workspace save / restore ----------------------------------------

    private void SaveWorkspace()
    {
        try
        {
            TerminalWorkspaceDocument document = new()
            {
                SelectedWindowId = "main",
                Windows =
                [
                    new TerminalWorkspaceWindow
                    {
                        Id = "main",
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
                    },
                ],
            };

            _workspaceStore.SaveAsync(document).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"workspace save failed: {ex.Message}");
        }
    }

    private string TabId(Tab tab) => $"tab-{_tabs.IndexOf(tab) + 1}";

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

        TerminalWorkspaceWindow? window = document.Windows.FirstOrDefault();
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
        _status.Text = $"{ghostty}  |  {panes} pane(s)  |  {cwd}  |  layout -> {WorkspacePath}";
    }

    private static string NormalizeCwd(string cwd)
    {
        if (cwd.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(cwd, UriKind.Absolute, out Uri? uri))
        {
            return uri.LocalPath;
        }

        return cwd;
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>
    /// Writes a bash rcfile that sources the user's ~/.bashrc and then the
    /// RoyalTerminal shell-integration snippet (OSC 7 cwd + OSC 133 prompts),
    /// so panes report their working directory for layout save/restore.
    /// </summary>
    private static string? TryWriteBashIntegrationRcFile()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return null;
        }

        if (!File.Exists("/bin/bash"))
        {
            return null;
        }

        try
        {
            string? script = TerminalShellIntegrationBootstrapBuilder.Build(
                new TerminalShellIntegrationBootstrapOptions(TerminalShellIntegrationBootstrapShell.Bash));
            if (script is null)
            {
                return null;
            }

            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "spike-royalterminal",
                "bashrc");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                "[ -f /etc/bash.bashrc ] && source /etc/bash.bashrc\n" +
                "[ -f \"$HOME/.bashrc\" ] && source \"$HOME/.bashrc\"\n\n" +
                script + "\n");
            return path;
        }
        catch
        {
            return null;
        }
    }
}
