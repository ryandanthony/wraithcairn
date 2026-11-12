#!/usr/bin/env python3
"""Screenshot an X display's root window to PNG (no ImageMagick needed).

usage: DISPLAY=:99 scripts/x-shot.py out.png [WIDTH HEIGHT]
"""
import ctypes
import ctypes.util
import sys

from PIL import Image

x = ctypes.CDLL(ctypes.util.find_library("X11"))
x.XOpenDisplay.restype = ctypes.c_void_p
x.XOpenDisplay.argtypes = [ctypes.c_char_p]
x.XDefaultRootWindow.restype = ctypes.c_ulong
x.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
x.XGetImage.restype = ctypes.c_void_p
x.XGetImage.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_int,
                        ctypes.c_uint, ctypes.c_uint, ctypes.c_ulong, ctypes.c_int]


class XImage(ctypes.Structure):
    _fields_ = [("width", ctypes.c_int), ("height", ctypes.c_int), ("xoffset", ctypes.c_int),
                ("format", ctypes.c_int), ("data", ctypes.POINTER(ctypes.c_ubyte)),
                ("byte_order", ctypes.c_int), ("bitmap_unit", ctypes.c_int),
                ("bitmap_bit_order", ctypes.c_int), ("bitmap_pad", ctypes.c_int),
                ("depth", ctypes.c_int), ("bytes_per_line", ctypes.c_int),
                ("bits_per_pixel", ctypes.c_int)]


d = x.XOpenDisplay(None)
if not d:
    sys.exit("cannot open display")
w = int(sys.argv[2]) if len(sys.argv) > 2 else 1200
h = int(sys.argv[3]) if len(sys.argv) > 3 else 800
img = ctypes.cast(x.XGetImage(d, x.XDefaultRootWindow(d), 0, 0, w, h, 0xFFFFFFFF, 2),
                  ctypes.POINTER(XImage)).contents
data = ctypes.string_at(img.data, img.bytes_per_line * h)
Image.frombuffer("RGB", (w, h), data, "raw", "BGRX", img.bytes_per_line, 1).save(sys.argv[1])
