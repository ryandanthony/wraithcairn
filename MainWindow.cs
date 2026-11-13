using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Services;
using RoyalTerminal.Terminal.Theming;

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
///   Ctrl+Alt+Shift+O    split left           Ctrl+Alt+Shift+E  split up
///   Alt+Arrows          move focus between panes
///   Ctrl+Alt+Arrows     resize split          Ctrl+Shift+Z  zoom pane
///   Ctrl+Shift+=        equalize splits
///   Ctrl+Shift+C / V    copy selection / paste
///   Ctrl+Shift+F        find (Enter next, Shift+Enter prev, Esc close)
///   Ctrl+Shift+K        clear scrollback
///   Ctrl+Shift+N        new window
///   Ctrl+Shift+P        configure pane (start folder, program, args)
///   Ctrl+Shift+R        restart pane
///   Ctrl+Shift+I        voice input on/off for the active pane (offline)
///   Ctrl+PgUp/PgDn      previous / next layout
///   Alt+1..9            switch to layout N
///   Ctrl+Shift+PgUp/PgDn move layout up / down in the list
///   Ctrl+= / Ctrl+-     font size
/// </summary>
public sealed partial class MainWindow : Window
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

    /// <summary>
    /// Per-pane chrome and live context shown in the pane's header bar.
    /// </summary>
    private sealed class PaneInfo
    {
        public required PaneHeader Header { get; init; }
        public required Border Frame { get; init; }

        /// <summary>Title the program set via OSC 0/2, if any.</summary>
        public string? OscTitle { get; set; }

        /// <summary>Command currently running (OSC 133 C), null at a prompt.</summary>
        public string? RunningCommand { get; set; }

        /// <summary>Exit code of the last finished command (OSC 133 D).</summary>
        public int? LastExitCode { get; set; }

        /// <summary>Exec-mode program exited; pane kept open.</summary>
        public bool ProcessExited { get; set; }

        /// <summary>The shell has shown at least one prompt.</summary>
        public bool PromptSeen { get; set; }
    }

    private readonly Dictionary<TerminalControl, PaneInfo> _panes = [];

    /// <summary>
    /// Per-process counter for KITTY_WINDOW_ID. Shared by all windows, so it
    /// is unique per pane session (a restarted pane gets a new id, like kitty).
    /// </summary>
    private static int s_kittyWindowId;

    /// <summary>Pane whose header is being dragged, and the pane under the pointer.</summary>
    private TerminalControl? _dragSource;
    private TerminalControl? _dragTarget;
    private static readonly IBrush DropTargetBrush = new SolidColorBrush(Color.Parse("#f9a825"));
    private static readonly IBrush FocusBrush = new SolidColorBrush(Color.Parse("#89b4fa"));
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

    /// <summary>Startup splash (primary window only); null when disabled or already closed.</summary>
    internal static SplashWindow? Splash { get; set; }

    /// <summary>What the splash showed, kept after it closes (for the selftest).</summary>
    private static List<string>? SplashHistory { get; set; }

    /// <summary>Splash on unless config says no or we're doing a headless one-shot.</summary>
    internal static bool SplashEnabled =>
        Config.ShowSplash != false && !Environment.GetCommandLineArgs().Contains("--dump-panes");

    private static void ReportSplash(string status, int done = 0, int total = 0) =>
        Splash?.Report(status, done, total);

    /// <summary>Only the primary window's restore drives the splash.</summary>
    private void ReportProgress(string status, int done = 0, int total = 0)
    {
        if (_isPrimary)
        {
            ReportSplash(status, done, total);
        }
    }

    /// <summary>Gives the splash a chance to repaint between blocking restore steps.</summary>
    private Task YieldForSplashAsync() =>
        _isPrimary && Splash is { } splash ? splash.YieldForPaintAsync() : Task.CompletedTask;

    public MainWindow()
        : this(null, isPrimary: true)
    {
    }

    private MainWindow(TerminalWorkspaceWindow? restore, bool isPrimary)
    {
        _pendingRestore = restore;
        _isPrimary = isPrimary;
        _shell = ShellIntegration.Resolve(Config.Shell);
        LoginEnvironment.Prefetch(ShellIntegration.UserShell(Config.Shell));
        Windows.Add(this);
        Activated += (_, _) => _lastFocused = this;
        Opened += (_, _) => LinuxWindowIdentity.Apply(this);
        Icon = AppInfo.WindowIcon;
        Title = NamedLayouts.ActiveName is { } layoutName
            ? $"wraithcairn [{layoutName}]"
            : "wraithcairn";
        Width = 1200;
        Height = 800;
        Background = ChromeBrush();
        ApplyChromeVariant(s_paintedTheme ?? Config.ResolvedTheme);

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
            PlaceholderText = "find…",
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
        AddHandler(KeyUpEvent, OnControlKeyUp, RoutingStrategies.Tunnel);

        Opened += async (_, _) =>
        {
            if (_isPrimary)
            {
                SplashWindow.Log("main window opened");
            }

            try
            {
                await RestoreWorkspaceAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"startup failed: {ex}");
            }
            finally
            {
                if (_isPrimary && Splash is { } splash)
                {
                    splash.Report("ready", 1, 1);
                    if (Environment.GetCommandLineArgs().Contains("--selftest"))
                    {
                        await Task.Delay(400);
                        SnapshotControl(splash, "/tmp/wraithcairn-selftest-splash.png");
                    }

                    await splash.CloseWhenReadyAsync();
                    SplashHistory = splash.History;
                    Splash = null;
                    Activate();
                    ActiveTab()?.Active?.Focus();
                }
            }

            try
            {
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
                else if (_isPrimary && Environment.GetCommandLineArgs().Contains("--check-updates"))
                {
                    await ShowUpdatesAsync();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"startup failed: {ex}");
            }
        };
        Closing += (_, _) =>
        {
            if (s_voice?.Owner == this)
            {
                StopVoiceInput(null);
            }

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

        if (_isPrimary)
        {
            StartConfigWatcher();
        }

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

        MenuItem pane = new() { Header = "Pane" };
        pane.Items.Add(Item("Split Right", Gesture(Key.O),
            () => _ = SplitAsync(TerminalPaneSplitOrientation.Horizontal)));
        pane.Items.Add(Item("Split Down", Gesture(Key.E),
            () => _ = SplitAsync(TerminalPaneSplitOrientation.Vertical)));
        pane.Items.Add(Item("Split Left", Gesture(Key.O, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift),
            () => _ = SplitAsync(TerminalPaneSplitOrientation.Horizontal, newPaneFirst: true)));
        pane.Items.Add(Item("Split Up", Gesture(Key.E, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift),
            () => _ = SplitAsync(TerminalPaneSplitOrientation.Vertical, newPaneFirst: true)));
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
        pane.Items.Add(new Separator());
        pane.Items.Add(Item("Voice Input", Gesture(Key.I), () => _ = ToggleVoiceInputAsync()));
        pane.Items.Add(new Separator());
        pane.Items.Add(Item("Close Pane", Gesture(Key.W), () => ClosePane()));

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
        view.Items.Add(new Separator());
        view.Items.Add(Item("Themes…", null, () => _ = ShowThemesAsync()));

        MenuItem help = new() { Header = "Help" };
        help.Items.Add(Item("Quick Start", null, () => OpenUrl(QuickStartUrl)));
        help.Items.Add(Item("Guides", null, () => OpenUrl(GuidesUrl)));
        help.Items.Add(new Separator());
        help.Items.Add(Item("Check for Updates…", null, () => _ = ShowUpdatesAsync()));
        MenuItem about = new() { Header = $"About wraithcairn {AppInfo.Version}…" };
        about.Click += async (_, _) => await AppInfo.ShowAboutAsync(this);
        help.Items.Add(about);

        MenuItem layouts = new() { Header = "Layouts" };
        layouts.SubmenuOpened += (_, e) =>
        {
            // SubmenuOpened bubbles: opening "Open Saved Layout" also raises it
            // here. Rebuilding then would replace the submenu the user is
            // pointing at with fresh, detached items, so clicks did nothing.
            if (ReferenceEquals(e.Source, layouts))
            {
                RebuildLayoutsMenu(layouts);
            }
        };
        RebuildLayoutsMenu(layouts);

        Menu menu = new() { FontSize = 12 };
        menu.Items.Add(shell);
        menu.Items.Add(edit);
        menu.Items.Add(layouts);
        menu.Items.Add(view);
        menu.Items.Add(pane);
        menu.Items.Add(help);

        // Clicking the menu takes keyboard focus; hand it back to the active
        // pane afterwards (unless the action opened the find bar).
        menu.Closed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // Not when the action opened the find bar or a dialog (Themes,
            // Configure Pane...): focusing the pane would pull keyboard focus
            // back to this window and typing would land in the shell.
            if (!_searchBar.IsVisible && !OwnedWindows.Any(w => w.IsVisible))
            {
                ActiveTab()?.Active?.Focus();
            }
        }, DispatcherPriority.Background);

        _menu = menu;
        return menu;
    }

    private const string DocsBaseUrl = "https://ryandanthony.github.io/wraithcairn/docs/";
    private const string QuickStartUrl = DocsBaseUrl + "getting-started/quick-start";
    private const string GuidesUrl = DocsBaseUrl + "guides";

    /// <summary>Opens a URL in the user's default browser (xdg-open on Linux).</summary>
    private void OpenUrl(string url)
    {
        try
        {
            _ = Launcher.LaunchUriAsync(new Uri(url));
        }
        catch
        {
            // No browser / launcher available; nothing useful to do.
        }
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
            d.Click += async (_, _) => await DeleteSavedLayoutAsync(name);
            delete.Items.Add(d);
        }

        layouts.Items.Add(open);
        layouts.Items.Add(delete);
    }

    /// <summary>
    /// Selftest helper: opens Layouts, then a saved-layout submenu, the way a
    /// mouse does (IsSubMenuOpen raises the real SubmenuOpened events), then
    /// clicks the entry for <paramref name="name"/>.
    /// </summary>
    private async Task<(bool Opened, bool Deleted)> DriveSavedLayoutMenuAsync(string name)
    {
        MenuItem? Layouts() => _menu?.Items.OfType<MenuItem>().FirstOrDefault(m => (string?)m.Header == "Layouts");

        async Task<MenuItem?> OpenSubmenuEntryAsync(string submenu)
        {
            if (Layouts() is not { } layouts)
            {
                return null;
            }

            layouts.IsSubMenuOpen = true;
            await Task.Delay(150);
            MenuItem? parent = layouts.Items.OfType<MenuItem>().FirstOrDefault(m => (string?)m.Header == submenu);
            if (parent is null)
            {
                return null;
            }

            parent.IsSubMenuOpen = true;
            await Task.Delay(150);

            // The entry must still belong to the menu that is on screen.
            bool parentStillShown = layouts.Items.Contains(parent) && parent.IsSubMenuOpen;
            MenuItem? entry = parent.Items.OfType<MenuItem>().FirstOrDefault(m => (string?)m.Header == name);
            return parentStillShown ? entry : null;
        }

        int before = _tabs.Count;
        bool opened = false;
        if (await OpenSubmenuEntryAsync("Open Saved Layout") is { } openEntry)
        {
            openEntry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Task.Delay(1500);
            opened = _tabs.Count == before + 1;
            if (opened)
            {
                CloseTab(_tabs[^1]);
            }
        }

        Layouts()?.Close();
        bool deleted = false;
        if (await OpenSubmenuEntryAsync("Delete Saved Layout") is { } deleteEntry)
        {
            _skipDeleteConfirm = true;
            deleteEntry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Task.Delay(300);
            _skipDeleteConfirm = false;
            deleted = !NamedLayouts.List().Contains(name);
        }

        Layouts()?.Close();
        return (opened, deleted);
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

    /// <summary>Set by the selftest so menu-driven deletes skip the confirm dialog.</summary>
    private bool _skipDeleteConfirm;

    /// <summary>Deletes layouts/NAME.json after a confirm (no undo).</summary>
    private async Task DeleteSavedLayoutAsync(string name)
    {
        if (!_skipDeleteConfirm && !await ConfirmAsync(
                "Delete Saved Layout",
                $"Delete saved layout \"{name}\"?\nThis removes {NamedLayouts.PathFor(name)} and can't be undone.",
                "Delete"))
        {
            ActiveTab()?.Active?.Focus();
            return;
        }

        try
        {
            NamedLayouts.Delete(name);
            _status.Text = $"deleted saved layout \"{name}\"";
        }
        catch (Exception ex)
        {
            _status.Text = $"couldn't delete \"{name}\": {ex.Message}";
        }

        ActiveTab()?.Active?.Focus();
    }

    private async Task<bool> ConfirmAsync(string title, string message, string okText)
    {
        Button ok = new() { Content = okText, IsDefault = true, MinWidth = 80 };
        Button cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        StackPanel body = new() { Margin = new Thickness(16), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        body.Children.Add(buttons);

        Window dialog = new()
        {
            Title = title,
            Content = body,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
        };

        bool result = false;
        ok.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => cancel.Focus();

        await dialog.ShowDialog(this);
        return result;
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
        if (tab?.Active is { } typed)
        {
            // Wait for the shell's first prompt, as a person would.
            for (int i = 0; i < 50 && !(_panes.TryGetValue(typed, out PaneInfo? ready) && ready.PromptSeen); i++)
            {
                await Task.Delay(100);
            }

            // Unique per run: bash's history dedupe would hide a repeated line.
            typed.SendInput($"sleep 3; echo wraithcairn-selftest-$((6*7)) # {Guid.NewGuid():N}\n");
        }

        await Task.Delay(300);
        PaneInfo? probeInfo = tab?.Active is { } probePane && _panes.TryGetValue(probePane, out PaneInfo? pinfo) ? pinfo : null;
        string? runningSeen = null;
        for (int i = 0; i < 50 && runningSeen is null; i++)
        {
            runningSeen = probeInfo?.RunningCommand;
            await Task.Delay(100);
        }

        string? runningAfter = probeInfo?.RunningCommand;
        for (int i = 0; i < 80 && runningAfter is not null; i++)
        {
            await Task.Delay(100);
            runningAfter = probeInfo?.RunningCommand;
        }

        await Task.Delay(300);

        string phase = restored ? "restored" : "initial";
        Console.WriteLine($"SELFTEST phase={phase}");
        Console.WriteLine($"SELFTEST version={AppInfo.Version}");
        Console.WriteLine($"SELFTEST icon_loaded={AppInfo.WindowIcon is not null && Icon is not null}");
        Console.WriteLine($"SELFTEST splash shown={SplashHistory is not null} closed={Splash is null} "
            + $"steps={SplashHistory?.Count ?? 0} last={SplashHistory?.LastOrDefault()}");
        if (SplashHistory is not null)
        {
            Console.WriteLine($"SELFTEST splash_steps={string.Join(" | ", SplashHistory)}");
        }
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

            // Back to the layout under test (after restore it may not be _tabs[0]).
            if (tab is not null)
            {
                SwitchToLayout(tab);
            }
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

            // Pane header bars: one per pane, showing a title.
            bool headersOk = tab.Nodes.Keys.All(c => _panes.TryGetValue(c, out PaneInfo? pi)
                && pi.Header.IsVisible && pi.Header.TitleText.Length > 0);
            if (tab.Active is { } headed && _panes.TryGetValue(headed, out PaneInfo? hi))
            {
                Console.WriteLine($"SELFTEST header_text={hi.Header.DisplayText}");
            }

            Console.WriteLine($"SELFTEST headers={headersOk} count={tab.Nodes.Count(n => _panes.ContainsKey(n.Key))}");
            Console.WriteLine($"SELFTEST header_running={runningSeen?.StartsWith("sleep", StringComparison.Ordinal) == true} idle_after={runningAfter is null}");

            // Ctrl+C must reach the shell as SIGINT: start a long sleep, send a
            // real KeyDown through the window (our tunnel handler, then the
            // terminal), and check the command ends well before 30 s.
            if (tab.Active is { } intPane && _panes.TryGetValue(intPane, out PaneInfo? intInfo))
            {
                intPane.Focus();
                intPane.SendInput($"sleep 30 # ctrlc-{Guid.NewGuid():N}\n");
                for (int i = 0; i < 30 && intInfo.RunningCommand is null; i++)
                {
                    await Task.Delay(100);
                }

                bool wasRunning = intInfo.RunningCommand is not null;

                // The reported bug: with text selected, Ctrl+C copied instead of
                // interrupting. Select everything first to cover that case.
                intPane.SelectAll();
                bool hadSelection = intPane.HasSelection;
                KeyEventArgs ctrlC = new()
                {
                    RoutedEvent = KeyDownEvent,
                    Key = Key.C,
                    KeyModifiers = KeyModifiers.Control,
                    Source = intPane,
                };
                intPane.RaiseEvent(ctrlC);
                for (int i = 0; i < 30 && intInfo.RunningCommand is not null; i++)
                {
                    await Task.Delay(100);
                }

                Console.WriteLine($"SELFTEST ctrl_c_interrupts={wasRunning && intInfo.RunningCommand is null} "
                    + $"was_running={wasRunning} with_selection={hadSelection} handled={ctrlC.Handled} exit={intInfo.LastExitCode}");

                // macOS delivers Ctrl+C with the control byte itself as the key
                // text (NSEvent.characters = "\u0003") and PhysicalKey set.
                // Replay that shape here so the Mac path is covered on Linux CI.
                intPane.SendInput($"sleep 30 # ctrlc-mac-{Guid.NewGuid():N}\n");
                for (int i = 0; i < 30 && intInfo.RunningCommand is null; i++)
                {
                    await Task.Delay(100);
                }

                bool macWasRunning = intInfo.RunningCommand is not null;
                KeyEventArgs macCtrlC = new()
                {
                    RoutedEvent = KeyDownEvent,
                    Key = Key.C,
                    PhysicalKey = PhysicalKey.C,
                    KeySymbol = "\u0003",
                    KeyModifiers = KeyModifiers.Control,
                    Source = intPane,
                };
                intPane.RaiseEvent(macCtrlC);
                for (int i = 0; i < 30 && intInfo.RunningCommand is not null; i++)
                {
                    await Task.Delay(100);
                }

                bool macInterrupted = macWasRunning && intInfo.RunningCommand is null;
                if (!macInterrupted && intInfo.RunningCommand is not null)
                {
                    intPane.SendInput("\u0003"); // clean up for the next checks
                    await Task.Delay(500);
                }

                Console.WriteLine($"SELFTEST mac_ctrl_c_interrupts={macInterrupted} was_running={macWasRunning} handled={macCtrlC.Handled}");

                // Every Ctrl+letter in the mac shape: feed them to `od` and
                // check the bytes the program actually received.
                string odOut = Path.Combine(Path.GetTempPath(), $"wraithcairn-ctrl-{Environment.ProcessId}.txt");
                intPane.SendInput($"stty raw -echo; head -c 20 | od -An -tx1 > {odOut}; stty sane\n");
                await Task.Delay(800);
                const string letters = "abefghklnoprstuvwxy"; // skip c/d/i/j/m/q/z: signals, EOF, tab, newline, flow control
                foreach (char letter in letters.Take(20))
                {
                    intPane.RaiseEvent(new KeyEventArgs
                    {
                        RoutedEvent = KeyDownEvent,
                        Key = Enum.Parse<Key>(letter.ToString().ToUpperInvariant()),
                        PhysicalKey = Enum.Parse<PhysicalKey>(letter.ToString().ToUpperInvariant()),
                        KeySymbol = ((char)(letter & 0x1F)).ToString(),
                        KeyModifiers = KeyModifiers.Control,
                        Source = intPane,
                    });
                    await Task.Delay(30);
                }

                intPane.SendInput("\u0001"); // pad to 20 bytes if needed
                await Task.Delay(800);
                string expected = string.Join(" ", letters.Select(l => ((int)(l & 0x1F)).ToString("x2")));
                string got = File.Exists(odOut) ? string.Join(" ", File.ReadAllText(odOut).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : "(none)";
                File.Delete(odOut);
                Console.WriteLine($"SELFTEST mac_ctrl_letters={got.StartsWith(expected, StringComparison.Ordinal)} got={got}");

                // Shift+1..0 as Avalonia/X11 delivers them (no key text), with
                // the Kitty keyboard protocol on like jcode/neovim/fish 4 use:
                // the program must get "!@#$%^&*()", not ESC[51;2u for "#".
                if (XkbKeyboardLayout.Instance is not null)
                {
                    const string shiftExpected = "21 40 23 24 25 5e 26 2a 28 29"; // !@#$%^&*()
                    Key[] digits = [Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9, Key.D0];
                    PhysicalKey[] physical =
                    [
                        PhysicalKey.Digit1, PhysicalKey.Digit2, PhysicalKey.Digit3, PhysicalKey.Digit4, PhysicalKey.Digit5,
                        PhysicalKey.Digit6, PhysicalKey.Digit7, PhysicalKey.Digit8, PhysicalKey.Digit9, PhysicalKey.Digit0,
                    ];

                    // Types Shift+1..0 into `od` under the Kitty protocol and
                    // returns the bytes the program received.
                    async Task<string> ShiftDigitsAsync(string? keyText)
                    {
                        string outFile = Path.Combine(Path.GetTempPath(), $"wraithcairn-shift-{Environment.ProcessId}.txt");
                        intPane.SendInput($"stty raw -echo; printf '\\033[>1u'; head -c 10 | od -An -tx1 > {outFile}; printf '\\033[<u'; stty sane\n");
                        await Task.Delay(800);
                        for (int i = 0; i < digits.Length; i++)
                        {
                            intPane.RaiseEvent(new KeyEventArgs
                            {
                                RoutedEvent = KeyDownEvent,
                                Key = digits[i],
                                PhysicalKey = physical[i],
                                KeySymbol = keyText?[i].ToString(),
                                KeyModifiers = KeyModifiers.Shift,
                                Source = intPane,
                            });
                            await Task.Delay(30);
                        }

                        await Task.Delay(800);
                        string bytes = File.Exists(outFile)
                            ? string.Join(" ", File.ReadAllText(outFile).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                            : "(none)";
                        File.Delete(outFile);
                        return bytes;
                    }

                    // X11 shape: no key text (Avalonia can't map exclam etc.).
                    if (XkbKeyboardLayout.HasXkb)
                    {
                        string shiftGot = await ShiftDigitsAsync(null);
                        Console.WriteLine($"SELFTEST shift_digits_kitty={shiftGot == shiftExpected} got={shiftGot}");
                    }

                    // Native Wayland shape (WRAITHCAIRN_WAYLAND=1): key text is
                    // already "!".."(". Shift must still count as consumed.
                    string wlGot = await ShiftDigitsAsync("!@#$%^&*()");
                    Console.WriteLine($"SELFTEST shift_digits_kitty_with_text={wlGot == shiftExpected} xkb={XkbKeyboardLayout.HasXkb} got={wlGot}");
                }
                else
                {
                    Console.WriteLine("SELFTEST shift_digits_kitty=skipped (not Linux)");
                }

                // Ctrl+Shift+C copies the selection; plain Ctrl+V is not paste.
                Avalonia.Input.Platform.IClipboard? clipboard = Clipboard;
                if (clipboard is not null)
                {
                    string? savedClipboard = await clipboard.TryGetTextAsync();
                    await clipboard.SetTextAsync("wraithcairn-clip-before");
                    intPane.SendInput($"echo wraithcairn-clip-{Guid.NewGuid():N}\n");
                    await Task.Delay(400);
                    intPane.SelectAll();
                    KeyEventArgs copyKey = new()
                    {
                        RoutedEvent = KeyDownEvent,
                        Key = Key.C,
                        KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
                        Source = intPane,
                    };
                    intPane.RaiseEvent(copyKey);
                    await Task.Delay(400);
                    string? copied = await clipboard.TryGetTextAsync();
                    Console.WriteLine($"SELFTEST ctrl_shift_c_copies={copied?.Contains("wraithcairn-clip-", StringComparison.Ordinal) == true && copied != "wraithcairn-clip-before"}");
                    intPane.ClearSelection();
                    if (savedClipboard is not null)
                    {
                        await clipboard.SetTextAsync(savedClipboard);
                    }
                }
                else
                {
                    Console.WriteLine("SELFTEST ctrl_shift_c_copies=skipped (no clipboard)");
                }

                bool menuHasClipboard = _panes.TryGetValue(intPane, out PaneInfo? menuInfo)
                    && menuInfo.Frame.ContextMenu is { } paneMenu
                    && paneMenu.Items.OfType<MenuItem>().Any(m => (string?)m.Header == "Copy")
                    && paneMenu.Items.OfType<MenuItem>().Any(m => (string?)m.Header == "Paste");
                bool plainKeysFree = !ClipboardKeys.CopyGestures.Any(g => g is { Key: Key.C, Modifiers: KeyModifiers.Control })
                    && !ClipboardKeys.PasteGestures.Any(g => g is { Key: Key.V, Modifiers: KeyModifiers.Control });
                Console.WriteLine($"SELFTEST context_menu_clipboard={menuHasClipboard} plain_ctrl_keys_free={plainKeysFree}");
            }
            int phantoms = tab.Nodes.Keys.Count(c => !ReferenceEquals(c, tab.Active)
                && _panes.TryGetValue(c, out PaneInfo? pi) && pi.RunningCommand is not null);
            Console.WriteLine($"SELFTEST idle_panes_clean={phantoms == 0}");

            // Swap (what a header drag does): first two leaves trade places,
            // then swap back so the saved layout is unchanged.
            IReadOnlyList<TerminalControl> order = TerminalPaneLayout.CollectLeafControls(tab.Root);
            if (order.Count >= 2)
            {
                TerminalControl x = order[0], y = order[^1];
                bool swapped = SwapPanes(x, y);
                IReadOnlyList<TerminalControl> after = TerminalPaneLayout.CollectLeafControls(tab.Root);
                bool moved = ReferenceEquals(after[0], y) && ReferenceEquals(after[^1], x);
                await Task.Delay(200);
                bool aliveAfter = x.HasActiveSession && y.HasActiveSession;
                bool back = SwapPanes(x, y);
                IReadOnlyList<TerminalControl> restoredOrder = TerminalPaneLayout.CollectLeafControls(tab.Root);
                bool roundTrip = restoredOrder.SequenceEqual(order);
                Console.WriteLine($"SELFTEST pane_swap={swapped && moved && back && roundTrip} alive={aliveAfter}");

                // Real gesture: press on x's header, move onto y's pane, release.
                bool dragged = SimulateHeaderDrag(x, y);
                IReadOnlyList<TerminalControl> dragOrder = TerminalPaneLayout.CollectLeafControls(tab.Root);
                bool dragMoved = ReferenceEquals(dragOrder[0], y) && ReferenceEquals(dragOrder[^1], x);
                SwapPanes(x, y); // put it back
                Console.WriteLine($"SELFTEST header_drag_swap={dragged && dragMoved} order_restored={TerminalPaneLayout.CollectLeafControls(tab.Root).SequenceEqual(order)}");
            }
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

                // Split Left / Split Up: the new pane must be the first child
                // of its split, i.e. left of / above the old one. (Focus may not
                // move headless, so find the new pane by diffing the node set.)
                foreach ((string header, TerminalPaneSplitOrientation want) in new[]
                {
                    ("Split Left", TerminalPaneSplitOrientation.Horizontal),
                    ("Split Up", TerminalPaneSplitOrientation.Vertical),
                })
                {
                    MenuItem? item = _menu?.Items.OfType<MenuItem>()
                        .FirstOrDefault(m => (string?)m.Header == "Pane")?.Items.OfType<MenuItem>()
                        .FirstOrDefault(m => (string?)m.Header == header);
                    int count = tmpTab.Nodes.Count;
                    HashSet<TerminalControl> existing = [.. tmpTab.Nodes.Keys];
                    item?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    await Task.Delay(1500);
                    TerminalPaneNode? newNode = tmpTab.Nodes
                        .FirstOrDefault(kv => !existing.Contains(kv.Key)).Value;
                    bool placed = newNode?.Parent is { } splitParent
                        && ReferenceEquals(splitParent.First, newNode)
                        && splitParent.Second?.Visual is not null
                        && splitParent.Orientation == want;
                    string key = header == "Split Left" ? "menu_split_left" : "menu_split_up";
                    Console.WriteLine($"SELFTEST {key}={item is not null && tmpTab.Nodes.Count == count + 1 && placed}");
                }
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

            // Same again through the real menu: open Layouts, open the
            // "Open Saved Layout" submenu, click the entry. Then delete it via
            // "Delete Saved Layout" the same way.
            (bool menuOpened, bool menuDeleted) = await DriveSavedLayoutMenuAsync(probe);
            Console.WriteLine($"SELFTEST menu_open_saved={menuOpened} menu_delete_saved={menuDeleted}");

            Startups.Remove(firstPaneId);
            NamedLayouts.Delete(probe);
            SaveWorkspace();

            // Menu bar order and the Help / Pane contents.
            List<MenuItem> top = _menu?.Items.OfType<MenuItem>().ToList() ?? [];
            string order = string.Join(",", top.Select(m => (string?)m.Header));
            List<string?> Headers(string menu) => top.FirstOrDefault(m => (string?)m.Header == menu)?
                .Items.OfType<MenuItem>().Select(m => m.Header as string).ToList() ?? [];
            bool helpOk = Headers("Help").Take(3).SequenceEqual(new[] { "Quick Start", "Guides", "Check for Updates\u2026" })
                && Headers("Help").Any(h => h?.StartsWith("About wraithcairn") == true);
            bool closeInPane = Headers("Pane").Contains("Close Pane") && !Headers("Shell").Contains("Close Pane");
            bool viewClean = !Headers("View").Any(h => h == "Check for Updates\u2026" || h?.StartsWith("About") == true);
            Console.WriteLine($"SELFTEST menu_order={order == "Shell,Edit,Layouts,View,Pane,Help"} help_menu={helpOk} close_in_pane={closeInPane} view_clean={viewClean} ({order})");

            await RunThemeSelfTestAsync();
            await RunUpdateSelfTestAsync();
            await RunVoiceSelfTestAsync();
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
    /// Selftest for Help > Check for Updates...: the menu item exists and
    /// opens the dialog, which finds the latest release from a fake local
    /// release folder (no network) and offers it to this dev build.
    /// </summary>
    private async Task RunUpdateSelfTestAsync()
    {
        string fake = Path.Combine(Path.GetTempPath(), $"wraithcairn-releases-{Environment.ProcessId}");
        string? previous = Environment.GetEnvironmentVariable("WRAITHCAIRN_RELEASES_URL");
        try
        {
            (string rid, string ext) = Updater.Platform();
            Directory.CreateDirectory(Path.Combine(fake, "latest", "download"));
            File.WriteAllText(Path.Combine(fake, "latest", "download", "SHA256SUMS"),
                $"{new string('a', 64)}  wraithcairn-9.8.7-{rid}.{ext}\n{new string('b', 64)}  wraithcairn-9.8.7-other-x64.zip\n");
            Environment.SetEnvironmentVariable("WRAITHCAIRN_RELEASES_URL", fake);

            MenuItem? help = _menu?.Items.OfType<MenuItem>().FirstOrDefault(m => (string?)m.Header == "Help");
            MenuItem? item = help?.Items.OfType<MenuItem>().FirstOrDefault(m => (string?)m.Header == "Check for Updates\u2026");
            if (item is null)
            {
                Console.WriteLine("SELFTEST update_menu=False (menu item missing)");
                return;
            }

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            UpdateDialog? dialog = null;
            for (int i = 0; i < 50 && dialog is null; i++)
            {
                await Task.Delay(100);
                dialog = OwnedWindows.OfType<UpdateDialog>().FirstOrDefault();
            }

            if (dialog is null)
            {
                Console.WriteLine("SELFTEST update_menu=False (dialog did not open)");
                return;
            }

            await dialog.Checked.WaitAsync(TimeSpan.FromSeconds(20));
            Updater.UpdateCheck? result = dialog.Result;
            bool found = result?.Latest is { Version: "9.8.7" } latest && latest.Rid == rid && result.IsNewer;
            await Task.Delay(200);
            SnapshotControl(dialog, "/tmp/wraithcairn-selftest-update.png");
            Console.WriteLine($"SELFTEST update_menu=True update_check={found} headline=\"{dialog.Headline}\" action=\"{dialog.PrimaryAction}\"");
            dialog.Close();
            await Task.Delay(100);

            // Version ordering used to decide "newer".
            bool order = Updater.CompareVersions("0.1.20", "0.1.19") > 0
                && Updater.CompareVersions("0.2.0", "0.1.99") > 0
                && Updater.CompareVersions("1.0.0", "1.0.0-rc1") > 0
                && Updater.CompareVersions("0.1.19", "0.1.19") == 0
                && Updater.CompareVersions("0.1.9", "0.1.10") < 0;
            Console.WriteLine($"SELFTEST update_version_order={order}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SELFTEST update_menu=error {ex}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WRAITHCAIRN_RELEASES_URL", previous);
            try
            {
                Directory.Delete(fake, recursive: true);
            }
            catch
            {
                // temp
            }
        }
    }

    /// <summary>
    /// Selftest for themes: catalog load, live preview, download + apply
    /// (saved to config.json, repainted without restart, new panes follow),
    /// cancel reverts the preview, Download All, and a hand edit of
    /// config.json being picked up live. Uses the repo's
    /// website/static/themes when present (no network); restores config.json
    /// and removes the theme files it added.
    /// </summary>
    private async Task RunThemeSelfTestAsync()
    {
        string repoThemes = Path.GetFullPath(Path.Combine("website", "static", "themes"));
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WRAITHCAIRN_THEMES_URL"))
            && File.Exists(Path.Combine(repoThemes, "index.json")))
        {
            Environment.SetEnvironmentVariable("WRAITHCAIRN_THEMES_URL", repoThemes);
        }

        Tab? tab = ActiveTab();
        TerminalControl? pane = tab?.Active;
        if (tab is null || pane is null)
        {
            Console.WriteLine("SELFTEST themes=skipped (no pane)");
            return;
        }

        string? originalConfig = File.Exists(AppConfig.AppConfigPath) ? File.ReadAllText(AppConfig.AppConfigPath) : null;
        HashSet<string> themesBefore = Directory.Exists(AppConfig.UserThemesDir)
            ? [.. Directory.GetFiles(AppConfig.UserThemesDir)]
            : [];
        string? themeBefore = Config.Theme;
        int reloadsBefore = s_configReloads;

        ThemeDialog dialog = new(ChromeBrush());
        try
        {
            dialog.Show(this);
            await dialog.CatalogLoaded.WaitAsync(TimeSpan.FromSeconds(30));
            ThemeCatalog.CatalogResult? catalog = dialog.Catalog;
            int count = catalog?.Themes.Count(t => t.InCatalog) ?? 0;
            Console.WriteLine($"SELFTEST theme_catalog={count >= 800} count={count} online={catalog?.Online} source={ThemeCatalog.BaseUrl}");
            if (catalog is null || count == 0)
            {
                return;
            }

            // Pick two catalog themes that are not the current one.
            string[] picks = [.. new[] { "Dracula", "Nord", "Gruvbox Dark", "Catppuccin Mocha" }
                .Where(n => n != themeBefore && catalog.Themes.Any(t => t.Name == n && t.InCatalog))];
            ThemeEntry first = catalog.Themes.First(t => t.Name == picks[0]);
            ThemeEntry second = catalog.Themes.First(t => t.Name == picks[1]);

            uint Bg(TerminalControl c) => c.Theme?.DefaultBackground ?? 0;
            uint Expected(ThemeEntry e) => e.ToTerminalTheme().DefaultBackground;

            dialog.Search(first.Name[..3]);
            bool searched = dialog.VisibleCount > 0 && dialog.VisibleCount < count;
            bool selected = dialog.Select(first.Name);
            await Task.Delay(150);
            bool previewed = Bg(pane) == Expected(first);
            await Task.Delay(300);
            SnapshotControl(dialog, "/tmp/wraithcairn-selftest-themes-dialog.png");
            Console.WriteLine($"SELFTEST theme_preview={selected && previewed} search={searched}");

            // Download + apply: file installed, config.json updated (comments
            // kept), every pane and header repainted, no restart.
            string firstFile = Path.Combine(AppConfig.UserThemesDir, first.Name);
            if (!themesBefore.Contains(firstFile))
            {
                File.Delete(firstFile);
            }
            bool applied = await dialog.ApplySelectedAsync();
            await Task.Delay(150);
            string configText = File.Exists(AppConfig.AppConfigPath) ? File.ReadAllText(AppConfig.AppConfigPath) : string.Empty;
            bool downloaded = File.Exists(Path.Combine(AppConfig.UserThemesDir, first.Name));
            bool saved = configText.Contains($"\"theme\": \"{first.Name}\"", StringComparison.Ordinal)
                && AppConfig.Load(writeDefault: false).Theme == first.Name;
            bool commentsKept = originalConfig is null || !originalConfig.Contains("//", StringComparison.Ordinal)
                || configText.Contains("//", StringComparison.Ordinal);
            bool allPanes = Windows.All(w => w._tabs.All(t => t.Nodes.Keys.All(c => Bg(c) == Expected(first))));
            bool chrome = Background is SolidColorBrush sb && sb.Color.ToUInt32() == Expected(first);
            Console.WriteLine($"SELFTEST theme_apply={applied && Config.Theme == first.Name && allPanes && chrome} "
                + $"downloaded={downloaded} saved={saved} comments_kept={commentsKept}");

            // New panes pick up the live theme too.
            HashSet<TerminalControl> existing = [.. tab.Nodes.Keys];
            int panesBefore = tab.Nodes.Count;
            tab.Active = pane;
            await SplitAsync(TerminalPaneSplitOrientation.Horizontal);
            TerminalControl? fresh = tab.Nodes.Keys.FirstOrDefault(c => !existing.Contains(c));
            bool freshHeader = fresh is not null && _panes.TryGetValue(fresh, out PaneInfo? freshInfo)
                && freshInfo.Header.Background is SolidColorBrush hb && hb.Color != Color.Parse("#1e1e2e");
            Console.WriteLine($"SELFTEST theme_new_pane={fresh is not null && Bg(fresh) == Expected(first) && freshHeader}");
            if (fresh is not null)
            {
                ClosePane(fresh);
                tab.Active = pane;
            }

            Console.WriteLine($"SELFTEST theme_new_pane_closed={tab.Nodes.Count == panesBefore}");

            // Preview something else, then close without applying: reverts.
            dialog.Select(second.Name);
            await Task.Delay(150);
            bool secondPreviewed = Bg(pane) == Expected(second);
            dialog.Close();
            await Task.Delay(150);
            Console.WriteLine($"SELFTEST theme_cancel_reverts={secondPreviewed && Bg(pane) == Expected(first) && Config.Theme == first.Name}");

            // Download All.
            dialog = new ThemeDialog(ChromeBrush());
            dialog.Show(this);
            await dialog.CatalogLoaded.WaitAsync(TimeSpan.FromSeconds(30));
            int written = await dialog.DownloadAllAsync();
            int onDisk = Directory.Exists(AppConfig.UserThemesDir) ? Directory.GetFiles(AppConfig.UserThemesDir).Length : 0;
            Console.WriteLine($"SELFTEST theme_download_all={written >= 800 && onDisk >= written} written={written}");
            dialog.Close();
            await Task.Delay(100);

            // Hand-edit config.json: the watcher applies it live.
            File.WriteAllText(AppConfig.AppConfigPath,
                Jsonc.SetTopLevelProperty(File.ReadAllText(AppConfig.AppConfigPath), "theme",
                    System.Text.Json.JsonSerializer.Serialize(second.Name)));
            bool watched = false;
            for (int i = 0; i < 40 && !watched; i++)
            {
                await Task.Delay(100);
                watched = Config.Theme == second.Name && Bg(pane) == Expected(second);
            }

            Console.WriteLine($"SELFTEST theme_watch_config={watched} reloads={s_configReloads - reloadsBefore}");
            SnapshotWindow("/tmp/wraithcairn-selftest-theme.png");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SELFTEST themes=error {ex}");
        }
        finally
        {
            dialog.Close();
            if (originalConfig is not null)
            {
                File.WriteAllText(AppConfig.AppConfigPath, originalConfig);
            }

            if (Directory.Exists(AppConfig.UserThemesDir))
            {
                foreach (string file in Directory.GetFiles(AppConfig.UserThemesDir).Where(f => !themesBefore.Contains(f)))
                {
                    File.Delete(file);
                }
            }

            ReloadThemeFromDisk();
            Console.WriteLine($"SELFTEST theme_restored={Config.Theme == themeBefore}");
        }
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

    private void SnapshotWindow(string path) => SnapshotControl(this, path);

    private static void SnapshotControl(Control control, string path)
    {
        try
        {
            PixelSize size = new((int)control.Bounds.Width, (int)control.Bounds.Height);
            if (size.Width <= 0 || size.Height <= 0)
            {
                return;
            }

            using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = new(size, new Vector(96, 96));
            bitmap.Render(control);
            bitmap.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"snapshot failed: {ex.Message}");
        }
    }

    // ----- shortcuts -------------------------------------------------------

    /// <summary>
    /// macOS (NSEvent.characters) and Windows (ToUnicode) report Ctrl+letter
    /// with the C0 control byte as the key text, e.g. Ctrl+C as "\u0003".
    /// RoyalTerminal hands that text to libghostty-vt's key encoder, which only
    /// maps printable characters to C0 bytes, so it fell through to CSI u and
    /// sent ESC[3;5u: Ctrl+C, Ctrl+D, Ctrl+R etc. did nothing in the shell.
    /// Linux already reports "c". Clearing the control text lets the encoder
    /// use the key itself, which yields 0x03 (and CSI u only when the program
    /// asked for the Kitty keyboard protocol). Ghostty's own macOS app strips
    /// these control characters the same way.
    ///
    /// The event is fixed in place (KeySymbol is init-only, hence reflection):
    /// TerminalControl also processes key events already marked handled, so
    /// replacing the event with a corrected copy would let the original leak.
    /// </summary>
    internal static void StripControlKeySymbol(KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && e.KeySymbol is { Length: 1 } symbol
            && (symbol[0] < 0x20 || symbol[0] == 0x7F))
        {
            KeySymbolProperty?.SetValue(e, null);
        }
    }

    private static readonly System.Reflection.PropertyInfo? KeySymbolProperty =
        typeof(KeyEventArgs).GetProperty(nameof(KeyEventArgs.KeySymbol));

    private static readonly System.Reflection.PropertyInfo? KeyProperty =
        typeof(KeyEventArgs).GetProperty(nameof(KeyEventArgs.Key));

    private static readonly System.Reflection.PropertyInfo? PhysicalKeyProperty =
        typeof(KeyEventArgs).GetProperty(nameof(KeyEventArgs.PhysicalKey));

    /// <summary>
    /// TerminalControl encodes key-downs even when they are already handled
    /// (its tunnel handler uses handledEventsToo), so a window shortcut such
    /// as Ctrl+Shift+I also reached the shell as "\e[105;6u" and showed up as
    /// "5;6u" at the prompt. Blank the key once a shortcut consumed it.
    /// </summary>
    private static void ConsumeShortcutKey(KeyEventArgs e)
    {
        KeySymbolProperty?.SetValue(e, null);
        KeyProperty?.SetValue(e, Key.None);
        PhysicalKeyProperty?.SetValue(e, PhysicalKey.None);
    }

    private void OnControlKeyUp(object? sender, KeyEventArgs e) => StripControlKeySymbol(e);

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        bool before = e.Handled;
        OnShortcutCore(e);
        if (e.Handled && !before)
        {
            ConsumeShortcutKey(e);
        }
    }

    private void OnShortcutCore(KeyEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("WRAITHCAIRN_TRACE") == "1")
        {
            Console.WriteLine($"TRACE key {e.Key} mods={e.KeyModifiers} sym={e.KeySymbol} phys={e.PhysicalKey} src={e.Source?.GetType().Name} focus={FocusManager?.GetFocusedElement()?.GetType().Name}");
        }

        // Mac/Windows Ctrl+letter: drop the C0 key text before the terminal sees it.
        StripControlKeySymbol(e);

        // Linux: Shift+1..0 etc. arrive without key text; fill it from XKB.
        XkbKeyboardLayout.FillKeySymbol(e);

        bool ctrlShift = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (_dragSource is not null && e.Key == Key.Escape) { CancelPaneDrag(); e.Handled = true; return; }

        if (ctrlShift && e.Key == Key.T) { _ = NewTabAsync(null); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.W) { ClosePane(); e.Handled = true; }
        else if (ctrlShift && alt && e.Key == Key.O) { _ = SplitAsync(TerminalPaneSplitOrientation.Horizontal, newPaneFirst: true); e.Handled = true; }
        else if (ctrlShift && alt && e.Key == Key.E) { _ = SplitAsync(TerminalPaneSplitOrientation.Vertical, newPaneFirst: true); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.O) { _ = SplitAsync(TerminalPaneSplitOrientation.Horizontal); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.E) { _ = SplitAsync(TerminalPaneSplitOrientation.Vertical); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.Z) { ToggleZoom(); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.P) { _ = ConfigurePaneAsync(); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.R && ActiveTab()?.Active is { } restartTarget) { _ = RestartPaneAsync(restartTarget); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.C && ActiveTab()?.Active is { HasSelection: true } sel) { _ = sel.CopySelectionAsync(); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.V && ActiveTab()?.Active is { } pasteTarget) { _ = pasteTarget.PasteAsync(); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.F) { OpenSearch(); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.K && ActiveTab()?.Active is { } clearTarget) { clearTarget.ClearScrollback(); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.I) { _ = ToggleVoiceInputAsync(); e.Handled = true; }
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

    /// <summary>
    /// Clipboard keys: copy is Ctrl+Shift+C and paste is Ctrl+Shift+V (plus
    /// Ctrl+Insert / Shift+Insert, and Cmd on macOS). RoyalTerminal's defaults
    /// also bind plain Ctrl+V to paste and Ctrl+C to copy while text is
    /// selected, which stole those keys from the shell (no SIGINT with a
    /// selection, no literal-next in readline/vim). Plain Ctrl+C/V/X/A now
    /// always go to the program.
    /// </summary>
    private static readonly TerminalShortcutConfiguration ClipboardKeys = new(
        copyGestures:
        [
            new(Key.C, KeyModifiers.Control | KeyModifiers.Shift),
            new(Key.Insert, KeyModifiers.Control, RequiresSelection: true),
            new(Key.C, KeyModifiers.Meta),
        ],
        pasteGestures:
        [
            new(Key.V, KeyModifiers.Control | KeyModifiers.Shift),
            new(Key.Insert, KeyModifiers.Shift),
            new(Key.V, KeyModifiers.Meta),
        ],
        cutGestures: [],
        selectAllGestures:
        [
            new(Key.A, KeyModifiers.Control | KeyModifiers.Shift),
            new(Key.A, KeyModifiers.Meta),
        ]);

    private TerminalControl CreateTerminal()
    {
        TerminalControl terminal = new(
            new TerminalSessionService(),
            XkbKeyboardLayout.Instance is { } xkb ? new DefaultTerminalInputAdapter(xkb) : new DefaultTerminalInputAdapter(),
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
            ShortcutConfiguration = ClipboardKeys,
        };

        if (!string.IsNullOrWhiteSpace(Config.FontFamily))
        {
            terminal.FontFamilyName = Config.FontFamily;
        }

        if ((s_paintedTheme ?? Config.ResolvedTheme) is { } theme)
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
            if (_panes.TryGetValue(terminal, out PaneInfo? info))
            {
                info.OscTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
                UpdateHeader(terminal);
            }

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
            TerminalShellIntegrationEvent ev = args.Value;
            if (Environment.GetEnvironmentVariable("WRAITHCAIRN_TRACE") == "1")
            {
                Console.WriteLine($"TRACE {DateTime.Now:HH:mm:ss.fff} ev={ev.Kind} cmd={ev.CommandLine} exit={ev.ExitCode}");
            }
            if (ev.Kind is TerminalShellIntegrationEventKind.OutputStarted
                or TerminalShellIntegrationEventKind.CommandFinished
                or TerminalShellIntegrationEventKind.FreshLineNewPrompt)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_panes.TryGetValue(terminal, out PaneInfo? info))
                    {
                        if (ev.Kind == TerminalShellIntegrationEventKind.OutputStarted)
                        {
                            // Prompt frameworks (starship etc.) and the integration
                            // itself run helpers through the DEBUG trap; skip them.
                            if (IsUserCommand(ev.CommandLine))
                            {
                                info.RunningCommand = ev.CommandLine!.Trim();
                            }
                        }
                        else if (ev.Kind == TerminalShellIntegrationEventKind.FreshLineNewPrompt)
                        {
                            info.PromptSeen = true;
                            info.RunningCommand = null;
                        }
                        else
                        {
                            if (info.RunningCommand is not null)
                            {
                                info.LastExitCode = ev.ExitCode;
                            }

                            info.RunningCommand = null;
                        }

                        UpdateHeader(terminal);
                    }
                });
            }

            if (ev.Kind == TerminalShellIntegrationEventKind.WorkingDirectoryChanged
                && ev.WorkingDirectory is { Length: > 0 } cwd)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    Tab? tab = FindTab(terminal);
                    if (tab is not null && tab.Nodes.TryGetValue(terminal, out TerminalPaneNode? node))
                    {
                        node.SetWorkingDirectory(NormalizeCwd(cwd));
                        UpdateHeader(terminal, node);
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
            if (_panes.TryGetValue(terminal, out PaneInfo? info))
            {
                info.ProcessExited = true;
                info.RunningCommand = null;
                info.LastExitCode = exitCode;
                UpdateHeader(terminal, node);
            }

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
            ["TERM_PROGRAM_VERSION"] = AppInfo.Version,
            // libghostty-vt speaks the Kitty graphics protocol, but TERM is
            // xterm-256color and TERM_PROGRAM is unknown to most apps, so
            // env-sniffing tools (jcode, ratatui-image, yazi, ...) fall back to
            // text. KITTY_WINDOW_ID is the de-facto "kitty graphics here"
            // signal. KITTY_PID is deliberately NOT set: tools use it to
            // decide they can drive kitty itself (`kitty @`, new windows).
            ["KITTY_WINDOW_ID"] = Interlocked.Increment(ref s_kittyWindowId)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        // Shell-only env (zsh's ZDOTDIR redirect) stays out of exec panes.
        if (_shell.Environment is not null && startup is not { IsExec: true })
        {
            foreach ((string key, string value) in _shell.Environment)
            {
                environment[key] = value;
            }
        }

        // Finder/Dock launches on macOS have no LANG; Terminal.app sets one,
        // and without it zsh, starship and friends mangle UTF-8.
        if (LoginEnvironment.Enabled && LoginEnvironment.DefaultLang() is { } lang)
        {
            environment["LANG"] = lang;
        }

        // Configured directory wins over the last-seen cwd.
        string? directory = startup?.ResolveDirectory()
            ?? (Directory.Exists(workingDirectory) ? workingDirectory : null)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        TerminalCommandSpec? command = _shell.Command;
        if (startup is { IsExec: true })
        {
            // No shell in between, so no login files run: take PATH etc.
            // from the login shell captured at startup (macOS), and resolve
            // the program on that PATH ourselves.
            foreach ((string key, string value) in await LoginEnvironment.GetAsync(ShellIntegration.UserShell(Config.Shell)))
            {
                environment.TryAdd(key, value);
            }

            string program = startup.Command!.Trim();
            if (program.StartsWith("~/", StringComparison.Ordinal))
            {
                program = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), program[2..]);
            }

            string? path = environment.TryGetValue("PATH", out string? p) ? p : Environment.GetEnvironmentVariable("PATH");
            string resolved = LoginEnvironment.ResolveCommand(program, path);
            command = new TerminalCommandSpec(resolved, startup.Args);
            if (!OperatingSystem.IsWindows() && !resolved.Contains('/'))
            {
                terminal.WriteOutput(System.Text.Encoding.UTF8.GetBytes(
                    $"\x1b[33m[wraithcairn: {program} not found on PATH={path}]\x1b[0m\r\n"));
            }
        }

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

        if (_panes.TryGetValue(terminal, out PaneInfo? info))
        {
            info.ProcessExited = false;
            info.RunningCommand = null;
            info.LastExitCode = null;
            info.OscTitle = null;
            info.PromptSeen = false;
        }

        await StartSessionAsync(terminal, node.WorkingDirectory, preserveScrollback: true, StartupFor(node));
        UpdateHeader(terminal, node);
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

        (Color background, Color foreground) = HeaderColors(s_paintedTheme ?? Config.ResolvedTheme);
        PaneHeader header = new(background, foreground);
        DockPanel.SetDock(header, Dock.Top);

        DockPanel body = new();
        body.Children.Add(header);
        body.Children.Add(TerminalPaneLayout.CreatePaneScrollViewer(terminal));

        Border paneBorder = new()
        {
            Child = body,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
        };
        paneBorder.ContextMenu = BuildPaneContextMenu(terminal);
        node.SetLeaf(terminal, paneBorder);

        _panes[terminal] = new PaneInfo { Header = header, Frame = paneBorder };
        header.Clicked += () => terminal.Focus();
        header.DoubleClicked += () =>
        {
            terminal.Focus();
            ToggleZoom();
        };
        header.DragStarted += () =>
        {
            terminal.Focus();
            _dragSource = terminal;
            _dragTarget = null;
        };
        header.DragMoved += point => UpdateDropTarget(point);
        header.DragEnded += point => EndPaneDrag(point);
        UpdateHeader(terminal, node);
        return node;
    }

    private ContextMenu BuildPaneContextMenu(TerminalControl terminal)
    {
        static KeyGesture CtrlShift(Key key) => new(key, KeyModifiers.Control | KeyModifiers.Shift);

        MenuItem copy = new() { Header = "Copy", InputGesture = CtrlShift(Key.C) };
        copy.Click += async (_, _) =>
        {
            if (terminal.HasSelection)
            {
                await terminal.CopySelectionAsync();
            }

            terminal.Focus();
        };
        MenuItem paste = new() { Header = "Paste", InputGesture = CtrlShift(Key.V) };
        paste.Click += async (_, _) =>
        {
            terminal.Focus();
            await terminal.PasteAsync();
        };
        MenuItem configure = new() { Header = "Configure Pane…" };
        configure.Click += (_, _) => { terminal.Focus(); _ = ConfigurePaneAsync(); };
        MenuItem restart = new() { Header = "Restart Pane" };
        restart.Click += (_, _) => _ = RestartPaneAsync(terminal);
        MenuItem right = new() { Header = "Split Right" };
        right.Click += (_, _) => { terminal.Focus(); _ = SplitAsync(TerminalPaneSplitOrientation.Horizontal); };
        MenuItem down = new() { Header = "Split Down" };
        down.Click += (_, _) => { terminal.Focus(); _ = SplitAsync(TerminalPaneSplitOrientation.Vertical); };
        MenuItem left = new() { Header = "Split Left" };
        left.Click += (_, _) => { terminal.Focus(); _ = SplitAsync(TerminalPaneSplitOrientation.Horizontal, newPaneFirst: true); };
        MenuItem up = new() { Header = "Split Up" };
        up.Click += (_, _) => { terminal.Focus(); _ = SplitAsync(TerminalPaneSplitOrientation.Vertical, newPaneFirst: true); };
        MenuItem close = new() { Header = "Close Pane" };
        close.Click += (_, _) => ClosePane(terminal);
        MenuItem voice = new() { Header = "Voice Input", InputGesture = CtrlShift(Key.I) };
        voice.Click += (_, _) => { terminal.Focus(); _ = ToggleVoiceInputAsync(terminal); };

        ContextMenu menu = new();
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(configure);
        menu.Items.Add(restart);
        menu.Items.Add(new Separator());
        menu.Items.Add(right);
        menu.Items.Add(down);
        menu.Items.Add(left);
        menu.Items.Add(up);
        menu.Items.Add(new Separator());
        menu.Items.Add(voice);
        menu.Items.Add(new Separator());
        menu.Items.Add(close);

        // Right-click focuses that pane; Copy is only useful with a selection.
        menu.Opening += (_, _) =>
        {
            copy.IsEnabled = terminal.HasSelection;
            voice.Header = IsDictatingInto(terminal) ? "Stop Voice Input" : "Voice Input";
            terminal.Focus();
        };
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
        UpdateHeader(terminal, node);

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
            StopVoiceInputFor(control);
            _panes.Remove(control);
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

    /// <summary>
    /// Splits the active pane. Horizontal puts the new pane right (or left
    /// with <paramref name="newPaneFirst"/>), Vertical puts it below (or above).
    /// </summary>
    private async Task SplitAsync(TerminalPaneSplitOrientation orientation, bool newPaneFirst = false)
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

        TerminalPaneNode first = newPaneFirst ? newLeaf : activeNode;
        TerminalPaneNode second = newPaneFirst ? activeNode : newLeaf;
        Grid grid = TerminalPaneLayout.CreateSplitGrid(
            orientation, 0.5, first.Visual, second.Visual);
        splitNode.SetSplit(orientation, 0.5, first, second, grid);

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
        FocusSoon(newControl);
        UpdateStatus(tab);
        MarkLayoutDirty();
    }

    /// <summary>
    /// Focuses a pane that was just (re)parented. Moving visuals around
    /// clears keyboard focus, and Focus() on a control that has not been laid
    /// out in its new place yet returns false, which left keys going to the
    /// window (the menu) after a split. Retry after the next layout pass.
    /// </summary>
    private void FocusSoon(TerminalControl control)
    {
        if (control.Focus())
        {
            return;
        }

        Dispatcher.UIThread.Post(() => control.Focus(), DispatcherPriority.Loaded);
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
        StopVoiceInputFor(control);
        tab.Nodes.Remove(control);
        _panes.Remove(control);

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
        if (tab.Active is { } survivor)
        {
            FocusSoon(survivor);
        }
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

    private void UpdateFocusBorders(Tab tab)
    {
        bool multiplePanes = tab.Nodes.Count > 1;
        foreach ((TerminalControl control, TerminalPaneNode node) in tab.Nodes)
        {
            bool active = ReferenceEquals(control, tab.Active);
            if (node.LeafContainer is Border border)
            {
                border.BorderBrush = ReferenceEquals(control, _dragTarget)
                    ? DropTargetBrush
                    : multiplePanes && active ? FocusBrush : Brushes.Transparent;
            }

            if (_panes.TryGetValue(control, out PaneInfo? info))
            {
                info.Header.SetActive(multiplePanes && active);
            }
        }
    }

    // ----- pane header bar -------------------------------------------------

    /// <summary>Refreshes the title + context text in a pane's header bar.</summary>
    private void UpdateHeader(TerminalControl terminal, TerminalPaneNode? node = null)
    {
        if (!_panes.TryGetValue(terminal, out PaneInfo? info))
        {
            return;
        }

        if (node is null && FindTab(terminal) is { } tab)
        {
            tab.Nodes.TryGetValue(terminal, out node);
        }

        string cwd = TildePath(node?.WorkingDirectory);
        PaneStartup? startup = node is null ? null : StartupFor(node);

        string title = info.OscTitle is { Length: > 0 } osc
            ? Shorten(osc, 60)
            : CwdBasename(node?.WorkingDirectory);

        List<string> context = [];
        if (!title.Contains(cwd, StringComparison.Ordinal))
        {
            context.Add(cwd);
        }

        if (info.RunningCommand is { } running)
        {
            context.Add($"\u25b6 {Shorten(running, 60)}");
        }
        else if (info.ProcessExited)
        {
            context.Add($"\u25a0 exited {info.LastExitCode}");
        }
        else if (info.LastExitCode is int code && code != 0)
        {
            context.Add($"\u2717 {code}");
        }

        if (startup is { HasCommand: true } su)
        {
            string line = $"{su.Command} {string.Join(' ', su.Args)}".Trim();
            context.Add($"\u2699 {Shorten(line, 40)}{(su.IsExec ? " (exec)" : string.Empty)}");
        }

        info.Header.SetContent(title, string.Join("  \u00b7  ", context));
    }

    /// <summary>
    /// Selftest helper: raises real pointer press/move/release events on a
    /// pane's header bar, ending over the centre of another pane.
    /// </summary>
    private bool SimulateHeaderDrag(TerminalControl source, TerminalControl target)
    {
        if (!_panes.TryGetValue(source, out PaneInfo? from)
            || !_panes.TryGetValue(target, out PaneInfo? to)
            || from.Header.TranslatePoint(new Point(20, 6), this) is not Point start
            || to.Frame.TranslatePoint(new Point(to.Frame.Bounds.Width / 2, to.Frame.Bounds.Height / 2), this) is not Point end)
        {
            return false;
        }

        Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        PointerPointProperties down = new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        PointerPointProperties drag = new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
        PointerPointProperties up = new(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
        ulong ts = 0;

        from.Header.RaiseEvent(new PointerPressedEventArgs(
            from.Header, pointer, this, start, ++ts, down, KeyModifiers.None));
        for (int i = 1; i <= 5; i++)
        {
            Point p = start + ((end - start) * (i / 5.0));
            from.Header.RaiseEvent(new PointerEventArgs(
                InputElement.PointerMovedEvent, from.Header, pointer, this, p, ++ts, drag, KeyModifiers.None));
        }

        bool targeted = ReferenceEquals(_dragTarget, target);
        from.Header.RaiseEvent(new PointerReleasedEventArgs(
            from.Header, pointer, this, end, ++ts, up, KeyModifiers.None, MouseButton.Left));
        return targeted && _dragSource is null;
    }

    private static bool IsUserCommand(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        string cmd = commandLine.Trim();
        return cmd is not ("true" or ":")
            && !cmd.StartsWith("__", StringComparison.Ordinal)
            && !cmd.StartsWith("starship_", StringComparison.Ordinal)
            && !cmd.StartsWith("_omp_", StringComparison.Ordinal);
    }

    private static string TildePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "~";
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        if (string.Equals(trimmed, home, StringComparison.Ordinal))
        {
            return "~";
        }

        return trimmed.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? "~" + trimmed[home.Length..]
            : trimmed;
    }

    /// <summary>The pane in the shown layout whose frame contains a window point.</summary>
    private TerminalControl? PaneAt(Point windowPoint)
    {
        if (ActiveTab() is not { } tab)
        {
            return null;
        }

        foreach ((TerminalControl control, TerminalPaneNode node) in tab.Nodes)
        {
            if (node.LeafContainer is not { IsVisible: true } container
                || container.TranslatePoint(default, this) is not Point origin)
            {
                continue;
            }

            if (new Rect(origin, container.Bounds.Size).Contains(windowPoint))
            {
                return control;
            }
        }

        return null;
    }

    private void UpdateDropTarget(Point windowPoint)
    {
        if (_dragSource is null || ActiveTab() is not { } tab)
        {
            return;
        }

        TerminalControl? over = PaneAt(windowPoint);
        TerminalControl? target = ReferenceEquals(over, _dragSource) ? null : over;
        if (!ReferenceEquals(target, _dragTarget))
        {
            _dragTarget = target;
            UpdateFocusBorders(tab);
            _status.Text = target is not null && _panes.TryGetValue(target, out PaneInfo? info)
                ? $"drop to swap with \"{info.Header.TitleText}\"   (Esc cancels)"
                : "drag onto another pane to swap   (Esc cancels)";
        }
    }

    private void EndPaneDrag(Point? windowPoint)
    {
        TerminalControl? source = _dragSource;
        if (windowPoint is Point point)
        {
            UpdateDropTarget(point);
        }

        TerminalControl? target = windowPoint is null ? null : _dragTarget;
        _dragSource = null;
        _dragTarget = null;

        if (source is not null && target is not null)
        {
            SwapPanes(source, target);
        }

        if (ActiveTab() is { } tab)
        {
            UpdateFocusBorders(tab);
            UpdateStatus(tab);
        }

        if (source is not null)
        {
            FocusSoon(source);
        }
    }

    private void CancelPaneDrag()
    {
        if (_dragSource is { } source && _panes.TryGetValue(source, out PaneInfo? info))
        {
            info.Header.CancelDrag();
        }
    }

    /// <summary>
    /// Swaps two panes' positions in the layout tree. Each pane keeps its id,
    /// process, scrollback and startup config; the split sizes stay with the
    /// positions.
    /// </summary>
    private bool SwapPanes(TerminalControl a, TerminalControl b)
    {
        if (ReferenceEquals(a, b)
            || FindTab(a) is not { } tab
            || !ReferenceEquals(FindTab(b), tab)
            || !tab.Nodes.TryGetValue(a, out TerminalPaneNode? nodeA)
            || !tab.Nodes.TryGetValue(b, out TerminalPaneNode? nodeB)
            || nodeA.Parent is not { SplitGrid: Grid gridA } parentA
            || nodeB.Parent is not { SplitGrid: Grid gridB } parentB)
        {
            return false;
        }

        if (tab.Zoom is not null)
        {
            TerminalControl? keep = tab.Active;
            ToggleZoom();
            tab.Active = keep;
        }

        Control visualA = nodeA.Visual;
        Control visualB = nodeB.Visual;
        (int rowA, int colA) = (Grid.GetRow(visualA), Grid.GetColumn(visualA));
        (int rowB, int colB) = (Grid.GetRow(visualB), Grid.GetColumn(visualB));
        bool aIsFirst = ReferenceEquals(parentA.First, nodeA);
        bool bIsFirst = ReferenceEquals(parentB.First, nodeB);

        gridA.Children.Remove(visualA);
        gridB.Children.Remove(visualB);

        // Tree links.
        if (aIsFirst) { parentA.First = nodeB; } else { parentA.Second = nodeB; }
        if (bIsFirst) { parentB.First = nodeA; } else { parentB.Second = nodeA; }
        nodeA.Parent = parentB;
        nodeB.Parent = parentA;

        // Visuals into each other's grid slots.
        Grid.SetRow(visualB, rowA);
        Grid.SetColumn(visualB, colA);
        Grid.SetRow(visualA, rowB);
        Grid.SetColumn(visualA, colB);
        gridA.Children.Add(visualB);
        gridB.Children.Add(visualA);

        UpdateFocusBorders(tab);
        MarkLayoutDirty();
        return true;
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
            if (tab.Active is { } unzoomed)
            {
                FocusSoon(unzoomed);
            }
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
        FocusSoon(active);
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
            ReportProgress($"reading {Path.GetFileName(WorkspacePath)}…");
            await YieldForSplashAsync();
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
            ReportProgress("starting shell…");
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

        int layoutIndex = 0;
        foreach (TerminalWorkspaceTab savedTab in window.Tabs)
        {
            layoutIndex++;
            ReportProgress($"building layout \"{savedTab.Title}\" ({layoutIndex}/{window.Tabs.Count})…", layoutIndex - 1, window.Tabs.Count);
            await YieldForSplashAsync();
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

        // Count panes as their shells come up so the splash shows real progress.
        int started = 0;
        int totalPanes = sessions.Count;
        ReportProgress($"starting {totalPanes} pane{(totalPanes == 1 ? "" : "s")}…", 0, totalPanes);
        await Task.WhenAll(sessions.Select(async session =>
        {
            try
            {
                await session;
            }
            finally
            {
                int done = Interlocked.Increment(ref started);
                ReportProgress($"started {done} of {totalPanes} pane{(totalPanes == 1 ? "" : "s")}", done, totalPanes);
            }
        }));
        selected?.Active?.Focus();
        if (selected is not null)
        {
            UpdateStatus(selected);
        }

        // Open the other saved windows (primary window only).
        if (siblings.Count(s => s.Tabs.Count > 0) is int extra and > 0)
        {
            ReportProgress($"opening {extra} more window{(extra == 1 ? "" : "s")}…");
        }

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

    // ----- themes (applied live, no restart) -------------------------------

    private static readonly TerminalTheme FallbackTheme = TerminalTheme.Dark;

    /// <summary>Theme currently painted (resolved config theme, or a picker preview).</summary>
    private static TerminalTheme? s_paintedTheme;
    private static string s_paintedFingerprint = AppConfig.Fingerprint(Config.ResolvedTheme);

    private static (Color Background, Color Foreground) HeaderColors(TerminalTheme? theme) =>
        theme is null
            ? (Color.Parse("#1e1e2e"), Color.Parse("#cdd6f4"))
            : (Color.FromUInt32(theme.DefaultBackground), Color.FromUInt32(theme.DefaultForeground));

    private static IBrush ChromeBrush(TerminalTheme? theme = null) =>
        new SolidColorBrush(HeaderColors(theme ?? s_paintedTheme ?? Config.ResolvedTheme).Background);

    /// <summary>Name of the theme in effect, for the picker and status.</summary>
    internal static string? CurrentThemeName => Config.Theme;

    /// <summary>True when config.json sets the theme (false = inherited from Ghostty).</summary>
    internal static bool ThemeFromAppConfig => Config.AppTheme is not null;

    /// <summary>Repaints every pane, header and window of every open window.</summary>
    private static void PaintThemeEverywhere(TerminalTheme? theme)
    {
        string fingerprint = AppConfig.Fingerprint(theme);
        if (fingerprint == s_paintedFingerprint)
        {
            return;
        }

        s_paintedFingerprint = fingerprint;
        s_paintedTheme = theme;
        foreach (MainWindow window in Windows.ToArray())
        {
            window.PaintTheme(theme);
        }
    }

    private void PaintTheme(TerminalTheme? theme)
    {
        Background = ChromeBrush(theme);
        ApplyChromeVariant(theme);
        (Color background, Color foreground) = HeaderColors(theme);
        foreach (Tab tab in _tabs)
        {
            foreach (TerminalControl control in tab.Nodes.Keys)
            {
                control.ApplyTheme(theme ?? FallbackTheme);
                if (_panes.TryGetValue(control, out PaneInfo? info))
                {
                    info.Header.SetColors(background, foreground);
                }
            }
        }
    }

    /// <summary>Shows a theme on every pane without saving it (picker preview).</summary>
    internal static void PreviewTheme(TerminalTheme theme) => PaintThemeEverywhere(theme);

    /// <summary>
    /// Menus, status bar and dialogs follow the terminal background: a light
    /// theme switches Fluent to its Light variant so the menu text stays
    /// readable (dark text on a light window).
    /// </summary>
    internal static void ApplyChromeVariant(TerminalTheme? theme)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        Avalonia.Styling.ThemeVariant variant = IsLight(theme)
            ? Avalonia.Styling.ThemeVariant.Light
            : Avalonia.Styling.ThemeVariant.Dark;
        if (app.RequestedThemeVariant != variant)
        {
            app.RequestedThemeVariant = variant;
        }
    }

    internal static bool IsLight(TerminalTheme? theme)
    {
        if (theme is null)
        {
            return false;
        }

        uint bg = theme.DefaultBackground;
        double luminance = ((0.2126 * ((bg >> 16) & 0xFF)) + (0.7152 * ((bg >> 8) & 0xFF)) + (0.0722 * (bg & 0xFF))) / 255.0;
        return luminance >= 0.5;
    }

    /// <summary>Drops any preview and repaints the configured theme.</summary>
    internal static void EndThemePreview() => PaintThemeEverywhere(Config.ResolvedTheme);

    /// <summary>
    /// Switches to a theme now (null = back to the Ghostty config's theme)
    /// and, when <paramref name="persist"/>, saves it as "theme" in
    /// config.json. The theme must be installed locally (downloaded or in a
    /// Ghostty theme folder). Returns false if the config could not be saved.
    /// </summary>
    internal static bool SetThemeLive(string? name, bool persist = true)
    {
        Config.SetTheme(name);
        PaintThemeEverywhere(Config.ResolvedTheme);
        foreach (MainWindow window in Windows)
        {
            if (window.ActiveTab() is { } tab)
            {
                window.UpdateStatus(tab);
            }
        }

        return !persist || AppConfig.SaveThemeSetting(Config.AppTheme);
    }

    // Watches config.json, the Ghostty config, and the user theme folder so
    // hand edits (or another wraithcairn instance) change the theme live.
    private static readonly List<FileSystemWatcher> ConfigWatchers = [];
    private static DispatcherTimer? s_configDebounce;
    private static int s_configReloads;

    private static void StartConfigWatcher()
    {
        if (ConfigWatchers.Count > 0 || Environment.GetCommandLineArgs().Contains("--dump-panes"))
        {
            return;
        }

        s_configDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            s_configDebounce!.Stop();
            ReloadThemeFromDisk();
        });

        void Watch(string dir, string filter)
        {
            try
            {
                Directory.CreateDirectory(dir);
                FileSystemWatcher watcher = new(dir, filter)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };
                FileSystemEventHandler poke = (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    s_configDebounce!.Stop();
                    s_configDebounce.Start();
                });
                watcher.Changed += poke;
                watcher.Created += poke;
                watcher.Deleted += poke;
                watcher.Renamed += (s, e) => poke(s, e);
                ConfigWatchers.Add(watcher);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"config watch failed for {dir}: {ex.Message}");
            }
        }

        Watch(AppConfig.ConfigDir, "config.json");
        Watch(AppConfig.UserThemesDir, "*");
        if (Config.UseGhosttyConfig && Path.GetDirectoryName(AppConfig.GhosttyConfigFile) is { } ghosttyDir
            && Directory.Exists(ghosttyDir))
        {
            Watch(ghosttyDir, "config");
        }
    }

    /// <summary>Re-reads the theme settings from disk and repaints if they changed.</summary>
    private static void ReloadThemeFromDisk()
    {
        try
        {
            AppConfig fresh = AppConfig.Load(writeDefault: false);
            Config.AdoptThemeFrom(fresh);
            s_configReloads++;
            if (!ThemeDialog.IsOpen)
            {
                PaintThemeEverywhere(Config.ResolvedTheme);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"config reload failed: {ex.Message}");
        }
    }

    private static bool s_updateOpen;

    private async Task ShowUpdatesAsync()
    {
        if (s_updateOpen)
        {
            return;
        }

        s_updateOpen = true;
        try
        {
            UpdateDialog dialog = new(ChromeBrush(), RestartIntoUpdate);
            await dialog.ShowDialog(this);
        }
        finally
        {
            s_updateOpen = false;
            ActiveTab()?.Active?.Focus();
        }
    }

    /// <summary>
    /// After an update: save the workspace, start the new binary with the same
    /// arguments, and close every window. The new process restores the layout
    /// (shells restart; with restoreScrollback on, their output comes back too).
    /// </summary>
    private static bool RestartIntoUpdate()
    {
        foreach (MainWindow window in Windows)
        {
            window._layoutDirty = false;
        }

        Windows.FirstOrDefault()?.SaveWorkspace();
        if (!Updater.StartNewInstance())
        {
            return false;
        }

        _shuttingDown = true;
        foreach (MainWindow window in Windows.ToArray())
        {
            window.Close();
        }

        return true;
    }

    private async Task ShowThemesAsync()
    {
        if (ThemeDialog.IsOpen)
        {
            return;
        }

        ThemeDialog dialog = new(ChromeBrush());
        await dialog.ShowDialog(this);
        ActiveTab()?.Active?.Focus();
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
