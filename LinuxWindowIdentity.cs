using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;

namespace Wraithcairn;

/// <summary>
/// Tells the Linux window manager which .desktop entry a window belongs to.
///
/// Without a hint KWin/Plasma guesses from WM_CLASS or the process's systemd
/// scope. Launched from a terminal (Ghostty, Konsole), wraithcairn runs inside
/// that terminal's scope, so Plasma files its window under the terminal's
/// taskbar icon instead of showing its own. Setting _KDE_NET_WM_DESKTOP_FILE
/// (KWin) and _GTK_APPLICATION_ID (GNOME/mutter) makes the mapping explicit.
/// Avalonia runs through XWayland on Wayland sessions by default, so this
/// covers both. With WRAITHCAIRN_WAYLAND=1 the native Wayland backend sets
/// xdg_toplevel app_id instead (WaylandPlatformOptions.AppId in Program.cs)
/// and the window handle is not an XID, so this is skipped.
/// </summary>
internal static partial class LinuxWindowIdentity
{
    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
        {
            return;
        }

        try
        {
            if (window.TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle
                || XOpenDisplay(null) is var display && display == 0)
            {
                return;
            }

            try
            {
                SetUtf8(display, handle.Handle, "_KDE_NET_WM_DESKTOP_FILE", DesktopIntegration.AppId);
                SetUtf8(display, handle.Handle, "_GTK_APPLICATION_ID", DesktopIntegration.AppId);
                XFlush(display);
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // no libX11 (pure Wayland backend); nothing to do
        }
    }

    private static void SetUtf8(nint display, nint window, string property, string value)
    {
        nint name = XInternAtom(display, property, false);
        nint utf8 = XInternAtom(display, "UTF8_STRING", false);
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        XChangeProperty(display, window, name, utf8, 8, PropModeReplace, bytes, bytes.Length);
    }

    private const int PropModeReplace = 0;

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XOpenDisplay(string? name);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(nint display);

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [LibraryImport("libX11.so.6")]
    private static partial int XChangeProperty(
        nint display, nint window, nint property, nint type, int format, int mode, byte[] data, int elements);
}
