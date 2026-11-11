using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Wraithcairn;

/// <summary>
/// Narrow bar above each pane: a grip, the pane title, and its context
/// (folder, running command, last exit code, configured startup program).
///
/// Dragging the bar onto another pane swaps the two panes. The header only
/// detects the gesture; <see cref="MainWindow"/> hit-tests and swaps.
/// Click focuses the pane, double-click toggles zoom, right-click shows the
/// pane context menu (inherited from the pane container).
/// </summary>
public sealed class PaneHeader : Border
{
    /// <summary>Pixels the pointer must travel before a press becomes a drag.</summary>
    private const double DragThreshold = 6;

    private readonly TextBlock _text;
    private readonly Run _grip;
    private readonly Run _title;
    private readonly Run _context;
    private IBrush _idleBackground = Brushes.Transparent;
    private IBrush _activeBackground = Brushes.Transparent;
    private IBrush _titleBrush = Brushes.White;
    private IBrush _contextBrush = Brushes.Gray;
    private bool _active;

    private Point? _pressPoint;
    private bool _dragging;

    /// <summary>Raised on a plain click (no drag).</summary>
    public event Action? Clicked;

    /// <summary>Raised on double-click.</summary>
    public event Action? DoubleClicked;

    /// <summary>Raised once when a press turns into a drag.</summary>
    public event Action? DragStarted;

    /// <summary>Raised while dragging; position is relative to the window.</summary>
    public event Action<Point>? DragMoved;

    /// <summary>Raised when the drag ends; position is null when cancelled.</summary>
    public event Action<Point?>? DragEnded;

    public PaneHeader(Color background, Color foreground)
    {
        _grip = new Run("\u283f ");
        _title = new Run { FontWeight = FontWeight.SemiBold };
        _context = new Run();

        _text = new TextBlock
        {
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _text.Inlines!.Add(_grip);
        _text.Inlines.Add(_title);
        _text.Inlines.Add(_context);

        Child = _text;
        Padding = new Thickness(6, 2);
        SetColors(background, foreground);
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Drag onto another pane to swap. Double-click to zoom.");
    }

    /// <summary>Recolors the bar from the terminal theme (live theme switch).</summary>
    public void SetColors(Color background, Color foreground)
    {
        _idleBackground = new SolidColorBrush(Mix(background, Colors.White, 0.07));
        _activeBackground = new SolidColorBrush(Mix(background, Color.Parse("#89b4fa"), 0.28));
        _titleBrush = new SolidColorBrush(foreground);
        _contextBrush = new SolidColorBrush(Mix(foreground, background, 0.45));
        _grip.Foreground = _contextBrush;
        _title.Foreground = _titleBrush;
        _context.Foreground = _contextBrush;
        Background = _active ? _activeBackground : _idleBackground;
    }

    public string TitleText => _title.Text ?? string.Empty;

    public string ContextText => _context.Text ?? string.Empty;

    /// <summary>Full single-line text, as shown.</summary>
    public string DisplayText => $"{TitleText}{ContextText}";

    public bool IsDragging => _dragging;

    public void SetContent(string title, string context)
    {
        _title.Text = title;
        _context.Text = string.IsNullOrEmpty(context) ? string.Empty : "   " + context;
    }

    public void SetActive(bool active)
    {
        _active = active;
        Background = active ? _activeBackground : _idleBackground;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            _pressPoint = null;
            DoubleClicked?.Invoke();
            e.Handled = true;
            return;
        }

        _pressPoint = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressPoint is not Point start)
        {
            return;
        }

        if (!_dragging)
        {
            Vector delta = e.GetPosition(this) - start;
            if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            {
                return;
            }

            _dragging = true;
            Cursor = new Cursor(StandardCursorType.DragMove);
            Opacity = 0.6;
            DragStarted?.Invoke();
        }

        if (TopLevel.GetTopLevel(this) is { } top)
        {
            DragMoved?.Invoke(e.GetPosition(top));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressPoint is null)
        {
            return;
        }

        bool wasDragging = _dragging;
        Point? at = TopLevel.GetTopLevel(this) is { } top ? e.GetPosition(top) : null;
        Reset();
        e.Pointer.Capture(null);

        if (wasDragging)
        {
            DragEnded?.Invoke(at);
        }
        else
        {
            Clicked?.Invoke();
        }

        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragging)
        {
            Reset();
            DragEnded?.Invoke(null);
        }

        _pressPoint = null;
    }

    /// <summary>Aborts an in-progress drag (e.g. Escape).</summary>
    public void CancelDrag()
    {
        if (_dragging)
        {
            Reset();
            DragEnded?.Invoke(null);
        }
    }

    private void Reset()
    {
        _pressPoint = null;
        _dragging = false;
        Opacity = 1;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + ((b.R - a.R) * t)),
        (byte)(a.G + ((b.G - a.G) * t)),
        (byte)(a.B + ((b.B - a.B) * t)));
}
