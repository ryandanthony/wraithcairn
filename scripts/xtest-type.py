#!/usr/bin/env python3
"""Type real X11 key events (XTest) into the focused window.

usage: xtest-type.py SPEC...
  SPEC is a keysym name (e.g. "3", "a", "Return") optionally prefixed by
  modifiers joined with '+': "shift+3", "ctrl+d". Plain text runs can be
  given as text:hello world (letters/digits/space/symbols on a US layout).
"""
import ctypes
import ctypes.util
import sys
import time

x11 = ctypes.CDLL(ctypes.util.find_library("X11"))
xtst = ctypes.CDLL(ctypes.util.find_library("Xtst"))
x11.XOpenDisplay.restype = ctypes.c_void_p
x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
x11.XStringToKeysym.restype = ctypes.c_ulong
x11.XStringToKeysym.argtypes = [ctypes.c_char_p]
x11.XKeysymToKeycode.restype = ctypes.c_ubyte
x11.XKeysymToKeycode.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
x11.XFlush.argtypes = [ctypes.c_void_p]
xtst.XTestFakeKeyEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]

dpy = x11.XOpenDisplay(None)
if not dpy:
    sys.exit("cannot open display")

MODS = {"shift": "Shift_L", "ctrl": "Control_L", "alt": "Alt_L", "super": "Super_L"}
NAMES = {"\\": "backslash", "[": "bracketleft", "]": "bracketright", "`": "grave", " ": "space", "#": "numbersign", "-": "minus", "/": "slash", ".": "period", ">": "greater",
         "|": "bar", "$": "dollar", "_": "underscore", "'": "apostrophe", '"': "quotedbl", "=": "equal",
         ";": "semicolon", ":": "colon", ",": "comma", "<": "less", "!": "exclam", "&": "ampersand",
         "(": "parenleft", ")": "parenright", "*": "asterisk", "~": "asciitilde"}
# Characters typed with Shift on a US layout, and the unshifted key they share.
SHIFT_OF = {"~": "grave", "!": "1", "@": "2", "#": "3", "$": "4", "%": "5", "^": "6", "&": "7", "*": "8",
            "(": "9", ")": "0", "_": "minus", "+": "equal", "{": "bracketleft", "}": "bracketright",
            "|": "backslash", ":": "semicolon", '"': "apostrophe", "<": "comma", ">": "period", "?": "slash"}


def code(name):
    sym = x11.XStringToKeysym(name.encode())
    if sym == 0:
        sys.exit(f"unknown keysym {name}")
    return x11.XKeysymToKeycode(dpy, sym)


def press(mods, key):
    for m in mods:
        xtst.XTestFakeKeyEvent(dpy, code(MODS[m]), True, 0)
    k = code(key)
    xtst.XTestFakeKeyEvent(dpy, k, True, 0)
    xtst.XTestFakeKeyEvent(dpy, k, False, 0)
    for m in reversed(mods):
        xtst.XTestFakeKeyEvent(dpy, code(MODS[m]), False, 0)
    x11.XFlush(dpy)
    time.sleep(0.04)


for spec in sys.argv[1:]:
    if spec.startswith("click:"):
        # click:X,Y  move the pointer there and left-click (focus without a WM)
        x, y = (int(v) for v in spec[6:].split(","))
        xtst.XTestFakeMotionEvent.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
        xtst.XTestFakeButtonEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
        xtst.XTestFakeMotionEvent(dpy, -1, x, y, 0)
        xtst.XTestFakeButtonEvent(dpy, 1, True, 0)
        xtst.XTestFakeButtonEvent(dpy, 1, False, 0)
        x11.XFlush(dpy)
        time.sleep(0.3)
        continue
    if spec.startswith("text:"):
        # Press physical keys like a person on a US keyboard: shifted symbols
        # are Shift + the base key, capitals are Shift + the letter.
        for ch in spec[5:]:
            if ch in SHIFT_OF:
                press(["shift"], SHIFT_OF[ch])
            elif ch.isupper():
                press(["shift"], ch.lower())
            else:
                press([], NAMES.get(ch, ch))
        continue
    parts = spec.split("+")
    press(parts[:-1], parts[-1])
