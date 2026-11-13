using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Platform;

namespace Wraithcairn;

/// <summary>
/// macOS key probe (`--mac-key-probe`, CI): sends real NSEvents for window
/// shortcuts through [NSApp sendEvent:], the same path a physical key press
/// takes (key equivalents, the main menu, the key window, AvnView keyDown:),
/// and reports which ones reached the shortcut handler and acted.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Key-downs the shortcut handler saw (probe).</summary>
    private static readonly List<string> s_shortcutSeen = [];

    private static int s_voiceToggles;

    private async Task RunMacKeyProbeAsync()
    {
        if (!OperatingSystem.IsMacOS() || TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle)
        {
            Console.WriteLine("PROBE skipped=not-macos");
            return;
        }

        nint app = MacNative.Send(MacNative.Class("NSApplication"), MacNative.Sel("sharedApplication"));
        MacNative.SendBool(app, MacNative.Sel("activateIgnoringOtherApps:"), true);
        MacNative.SendPtr(handle.NSWindow, MacNative.Sel("makeKeyAndOrderFront:"), 0);
        ActiveTab()?.Active?.Focus();
        await Task.Delay(1500);
        long windowNumber = MacNative.Send(handle.NSWindow, MacNative.Sel("windowNumber"));
        bool key = MacNative.Send(app, MacNative.Sel("keyWindow")) == handle.NSWindow;
        Console.WriteLine($"PROBE window={windowNumber} key_window={key} focus={FocusManager?.GetFocusedElement()?.GetType().Name}");

        const ulong Shift = 1UL << 17, Control = 1UL << 18, Option = 1UL << 19, Command = 1UL << 20;
        (string Name, ushort Code, ulong Flags, string Chars, string Plain)[] chords =
        [
            ("ctrl+shift+z", 0x06, Control | Shift, "\u001a", "Z"),
            ("ctrl+shift+z", 0x06, Control | Shift, "\u001a", "Z"),
            ("ctrl+shift+i", 0x22, Control | Shift, "\t", "I"),
            ("ctrl+shift+i", 0x22, Control | Shift, "\t", "I"),
            ("ctrl+shift+k", 0x28, Control | Shift, "\u000b", "K"),
            ("ctrl+shift+p", 0x23, Control | Shift, "\u0010", "P"),
            ("cmd+shift+i", 0x22, Command | Shift, "I", "I"),
            ("cmd+shift+i", 0x22, Command | Shift, "I", "I"),
        ];
        _ = Option;

        foreach ((string name, ushort code, ulong flags, string chars, string plain) in chords)
        {
            int seenBefore = s_shortcutSeen.Count;
            int togglesBefore = s_voiceToggles;
            bool zoomBefore = ActiveTab()?.Zoom is not null;
            Post(app, windowNumber, 10, code, flags, chars, plain);
            await Task.Delay(50);
            Post(app, windowNumber, 11, code, flags, chars, plain);
            await Task.Delay(600);
            string seen = s_shortcutSeen.Count > seenBefore ? string.Join(",", s_shortcutSeen.Skip(seenBefore)) : "(none)";
            Console.WriteLine($"PROBE {name} seen={seen} voice_toggled={s_voiceToggles > togglesBefore} "
                + $"zoom_changed={(ActiveTab()?.Zoom is not null) != zoomBefore}");
            // Close whatever a shortcut opened (Configure Pane, model download).
            foreach (Avalonia.Controls.Window owned in OwnedWindows.ToArray())
            {
                owned.Close();
            }

            await Task.Delay(300);
            ActiveTab()?.Active?.Focus();
        }

        StopVoiceInput(null);
        await Task.Delay(500);

        // Same chord through the window server (CGEvent), like a physical
        // keyboard: modifier flag changes, then the key, with real flags.
        // With a model and WRAITHCAIRN_VOICE_TEST_WAV this dictates for real.
        bool canDictate = VoiceModel.Default.IsInstalled(Config.ResolvedVoiceModelsDir)
            && Environment.GetEnvironmentVariable("WRAITHCAIRN_VOICE_TEST_WAV") is { Length: > 0 };
        foreach ((string how, Action<nint> post, ushort modifierKey, ulong modifier) in new (string, Action<nint>, ushort, ulong)[]
        {
            ("pid", ev => MacNative.CGEventPostToPid(Environment.ProcessId, ev), 0x3B, Control),
            ("hid", ev => MacNative.CGEventPost(0, ev), 0x3B, Control),
            ("hid-cmd", ev => MacNative.CGEventPost(0, ev), 0x37, Command),
        })
        {
            ActiveTab()?.Active?.Focus();
            await Task.Delay(300);
            int seenBefore = s_shortcutSeen.Count;
            int togglesBefore = s_voiceToggles;
            s_voiceTyped.Clear();
            nint src = MacNative.CGEventSourceCreate(1);
            async Task ChordAsync()
            {
                void Key(ushort code, bool down, ulong flags)
                {
                    nint ev = MacNative.CGEventCreateKeyboardEvent(src, code, down);
                    MacNative.CGEventSetFlags(ev, flags);
                    post(ev);
                    MacNative.CFRelease(ev);
                }

                Key(modifierKey, true, modifier);
                await Task.Delay(30);
                Key(0x38, true, modifier | Shift);
                await Task.Delay(30);
                Key(0x22, true, modifier | Shift);
                await Task.Delay(30);
                Key(0x22, false, modifier | Shift);
                await Task.Delay(30);
                Key(0x38, false, modifier);
                Key(modifierKey, false, 0);
            }

            await ChordAsync();
            await Task.Delay(800);
            bool listening = s_voice is not null;
            bool typed = false;
            if (canDictate && listening)
            {
                for (int i = 0; i < 150 && s_voiceTyped.Count == 0; i++)
                {
                    await Task.Delay(100);
                }

                typed = string.Concat(s_voiceTyped).Contains("country", StringComparison.OrdinalIgnoreCase);
                await ChordAsync(); // stop with the same keys
                for (int i = 0; i < 50 && s_voice is not null; i++)
                {
                    await Task.Delay(100);
                }
            }

            if (src != 0)
            {
                MacNative.CFRelease(src);
            }

            string seen = s_shortcutSeen.Count > seenBefore ? string.Join(",", s_shortcutSeen.Skip(seenBefore)) : "(none)";
            Console.WriteLine($"PROBE cg-{how} shift+i seen={seen} voice_toggled={s_voiceToggles > togglesBefore} "
                + (canDictate ? $"listening={listening} dictated={typed} stopped={s_voice is null} typed=\"{string.Concat(s_voiceTyped)}\"" : "dictation=skipped"));
            foreach (Avalonia.Controls.Window owned in OwnedWindows.ToArray())
            {
                owned.Close();
            }

            StopVoiceInput(null);
            ActiveTab()?.Active?.SendInput("\u0015");
            await Task.Delay(500);
        }
    }

    private static void Post(nint app, long windowNumber, ulong type, ushort code, ulong flags, string chars, string plain)
    {
        nint nsChars = MacNative.String(chars);
        nint nsPlain = MacNative.String(plain);
        double timestamp = MacNative.SendDouble(MacNative.Send(MacNative.Class("NSProcessInfo"), MacNative.Sel("processInfo")), MacNative.Sel("systemUptime"));
        nint ev = MacNative.KeyEvent(
            MacNative.Class("NSEvent"),
            MacNative.Sel("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
            type, new MacNative.Point(10, 10), flags, timestamp, windowNumber, 0, nsChars, nsPlain, false, code);
        if (ev == 0)
        {
            Console.WriteLine("PROBE error=no-event");
            return;
        }

        MacNative.SendPtr(app, MacNative.Sel("sendEvent:"), ev);
    }

    private static void NoteShortcutKey(KeyEventArgs e)
    {
        if (Environment.GetCommandLineArgs().Contains("--mac-key-probe"))
        {
            s_shortcutSeen.Add($"{e.KeyModifiers}+{e.Key}/{e.PhysicalKey}/{e.KeySymbol?.Length switch { null => "null", _ => string.Join("", e.KeySymbol.Select(c => $"{(int)c:x2}")) }}");
        }
    }
}

/// <summary>Bare Objective-C runtime calls for the macOS key probe.</summary>
internal static partial class MacNative
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct Point(double X, double Y);

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Class(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Sel(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendPtr(nint receiver, nint selector, nint arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial double SendDouble(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint SendString(nint receiver, nint selector, string arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint KeyEvent(
        nint cls, nint selector, ulong type, Point location, ulong flags, double timestamp, long windowNumber,
        nint context, nint characters, nint charactersIgnoringModifiers, [MarshalAs(UnmanagedType.I1)] bool repeat, ushort keyCode);

    public static nint String(string text) => SendString(Class("NSString"), Sel("stringWithUTF8String:"), text);

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [LibraryImport(CoreGraphics)]
    public static partial nint CGEventSourceCreate(int state);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGEventCreateKeyboardEvent(nint source, ushort keyCode, [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [LibraryImport(CoreGraphics)]
    public static partial void CGEventSetFlags(nint ev, ulong flags);

    [LibraryImport(CoreGraphics)]
    public static partial void CGEventPost(int tap, nint ev);

    [LibraryImport(CoreGraphics)]
    public static partial void CGEventPostToPid(int pid, nint ev);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    public static partial void CFRelease(nint obj);
}
