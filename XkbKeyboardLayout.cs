using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Input;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;

namespace Wraithcairn;

/// <summary>
/// Linux/X11 keyboard layout lookups through XKB, so shifted keys carry the
/// character they really type.
///
/// Avalonia's X11 backend leaves KeyEventArgs.KeySymbol empty when the
/// shifted keysym has no Avalonia <see cref="Key"/> (Shift+1..0 give
/// exclam, at, numbersign... which it cannot map), and RoyalTerminal's Linux
/// layout never reports Shift as consumed. Legacy input still worked (the
/// text arrives via TextInput), but once a program enabled the Kitty keyboard
/// protocol (jcode, neovim, helix, fish 4...) Shift+3 was encoded as
/// ESC[51;2u, "shift + 3", instead of "#": the program saw a 3 or nothing.
///
/// This fills the missing KeySymbol from XKB and reports the unshifted
/// codepoint and consumed modifiers, as RoyalTerminal already does on
/// Windows and macOS, so libghostty-vt's encoder sends "#" (or, with the
/// "report alternate keys" flag, ESC[51:35;2u, like Kitty and Ghostty).
/// Layout-aware: a UK layout's Shift+3 gives £.
/// </summary>
internal sealed partial class XkbKeyboardLayout : ITerminalKeyboardLayout
{
    public static XkbKeyboardLayout? Instance { get; } = OperatingSystem.IsLinux() && Xkb.Open() ? new XkbKeyboardLayout() : null;

    public TerminalKeyboardLayoutInfo GetInfo(KeyEventArgs key)
    {
        if (Xkb.KeycodeFor(key.PhysicalKey) is not { } keycode)
        {
            // Same as RoyalTerminal's Linux default: the event's own text.
            return new TerminalKeyboardLayoutInfo(key.KeyModifiers == KeyModifiers.None ? Scalar(key.KeySymbol) : 0u, 0);
        }

        uint unshifted = Scalar(Xkb.Translate(keycode, KeyModifiers.None));
        string? symbol = key.KeySymbol;
        TerminalModifiers consumed = 0;
        if (Scalar(symbol) != 0 && Xkb.Translate(keycode, key.KeyModifiers) == symbol)
        {
            // A modifier is consumed when dropping it changes the character.
            if (key.KeyModifiers.HasFlag(KeyModifiers.Shift)
                && Xkb.Translate(keycode, key.KeyModifiers & ~KeyModifiers.Shift) != symbol)
            {
                consumed |= TerminalModifiers.Shift;
            }

            if (key.KeyModifiers.HasFlag(KeyModifiers.Alt)
                && Xkb.Translate(keycode, key.KeyModifiers & ~KeyModifiers.Alt) != symbol)
            {
                consumed |= TerminalModifiers.Alt;
            }
        }

        return new TerminalKeyboardLayoutInfo(unshifted, consumed);
    }

    /// <summary>
    /// Fills an empty KeySymbol (Shift+digit and friends) with the character
    /// the key types on the current layout. Only for text-producing chords:
    /// no Ctrl/Meta (those must stay key codes).
    /// </summary>
    public static void FillKeySymbol(KeyEventArgs e)
    {
        if (Instance is null
            || !string.IsNullOrEmpty(e.KeySymbol)
            || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0
            || Xkb.KeycodeFor(e.PhysicalKey) is not { } keycode)
        {
            return;
        }

        string? text = Xkb.Translate(keycode, e.KeyModifiers);
        if (Scalar(text) != 0)
        {
            KeySymbolProperty?.SetValue(e, text);
        }
    }

    private static readonly System.Reflection.PropertyInfo? KeySymbolProperty =
        typeof(KeyEventArgs).GetProperty(nameof(KeyEventArgs.KeySymbol));

    /// <summary>Single printable code point, else 0 (same rule as RoyalTerminal).</summary>
    private static uint Scalar(string? text) =>
        text is not null && Rune.TryGetRuneAt(text, 0, out Rune rune) && rune.Utf16SequenceLength == text.Length
            && !Rune.IsControl(rune)
            ? (uint)rune.Value
            : 0u;

    /// <summary>Thin libX11 XKB binding on a private display connection.</summary>
    private static partial class Xkb
    {
        private const int XkbUseCoreKbd = 0x0100;
        private const uint ShiftMask = 1;
        private const uint Mod1Mask = 8; // Alt

        private static nint s_display;
        private static readonly Dictionary<(byte, uint), string?> Cache = [];
        private static int s_cachedGroup = -1;

        public static bool Open()
        {
            try
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
                {
                    return false;
                }

                s_display = XOpenDisplay(0);
                return s_display != 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        public static string? Translate(byte keycode, KeyModifiers modifiers)
        {
            uint state = 0;
            if (modifiers.HasFlag(KeyModifiers.Shift))
            {
                state |= ShiftMask;
            }

            if (modifiers.HasFlag(KeyModifiers.Alt))
            {
                state |= Mod1Mask;
            }

            // The active group (layout) can change at runtime (layout switcher).
            int group = CurrentGroup();
            if (group != s_cachedGroup)
            {
                Cache.Clear();
                s_cachedGroup = group;
            }

            state |= (uint)(group & 3) << 13;
            if (Cache.TryGetValue((keycode, state), out string? cached))
            {
                return cached;
            }

            string? result = null;
            if (XkbLookupKeySym(s_display, keycode, state, out _, out nint keysym) && keysym != 0)
            {
                Span<byte> buffer = stackalloc byte[16];
                int n = XkbTranslateKeySym(s_display, ref keysym, 0, ref MemoryMarshal.GetReference(buffer), buffer.Length, out _);
                if (n > 0)
                {
                    result = Encoding.UTF8.GetString(buffer[..n]);
                }
            }

            Cache[(keycode, state)] = result;
            return result;
        }

        private static int CurrentGroup()
        {
            Span<byte> xkbState = stackalloc byte[32]; // XkbStateRec; group is the first byte
            return XkbGetState(s_display, XkbUseCoreKbd, ref MemoryMarshal.GetReference(xkbState)) == 0 ? xkbState[0] : 0;
        }

        /// <summary>X keycode (evdev + 8) for the keys that type characters.</summary>
        public static byte? KeycodeFor(PhysicalKey key)
        {
            int evdev = key switch
            {
                PhysicalKey.Digit1 => 2, PhysicalKey.Digit2 => 3, PhysicalKey.Digit3 => 4, PhysicalKey.Digit4 => 5,
                PhysicalKey.Digit5 => 6, PhysicalKey.Digit6 => 7, PhysicalKey.Digit7 => 8, PhysicalKey.Digit8 => 9,
                PhysicalKey.Digit9 => 10, PhysicalKey.Digit0 => 11, PhysicalKey.Minus => 12, PhysicalKey.Equal => 13,
                PhysicalKey.Q => 16, PhysicalKey.W => 17, PhysicalKey.E => 18, PhysicalKey.R => 19, PhysicalKey.T => 20,
                PhysicalKey.Y => 21, PhysicalKey.U => 22, PhysicalKey.I => 23, PhysicalKey.O => 24, PhysicalKey.P => 25,
                PhysicalKey.BracketLeft => 26, PhysicalKey.BracketRight => 27,
                PhysicalKey.A => 30, PhysicalKey.S => 31, PhysicalKey.D => 32, PhysicalKey.F => 33, PhysicalKey.G => 34,
                PhysicalKey.H => 35, PhysicalKey.J => 36, PhysicalKey.K => 37, PhysicalKey.L => 38,
                PhysicalKey.Semicolon => 39, PhysicalKey.Quote => 40, PhysicalKey.Backquote => 41,
                PhysicalKey.Backslash => 43,
                PhysicalKey.Z => 44, PhysicalKey.X => 45, PhysicalKey.C => 46, PhysicalKey.V => 47, PhysicalKey.B => 48,
                PhysicalKey.N => 49, PhysicalKey.M => 50, PhysicalKey.Comma => 51, PhysicalKey.Period => 52,
                PhysicalKey.Slash => 53, PhysicalKey.IntlBackslash => 86, PhysicalKey.IntlRo => 89,
                PhysicalKey.IntlYen => 124,
                _ => 0,
            };
            return evdev == 0 ? null : (byte)(evdev + 8);
        }

        [LibraryImport("libX11.so.6")]
        private static partial nint XOpenDisplay(nint name);

        [LibraryImport("libX11.so.6")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool XkbLookupKeySym(nint display, byte keycode, uint modifiers, out uint modsRtrn, out nint keysymRtrn);

        [LibraryImport("libX11.so.6")]
        private static partial int XkbTranslateKeySym(nint display, ref nint keysym, uint modifiers, ref byte buffer, int nbytes, out int extra);

        [LibraryImport("libX11.so.6")]
        private static partial int XkbGetState(nint display, uint deviceSpec, ref byte state);
    }
}
