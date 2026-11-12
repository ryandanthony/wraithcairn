#!/usr/bin/env python3
"""Make the app icons' rounded corners transparent.

The icon PNGs in assets/ were exported without an alpha channel, so the area
outside the rounded tile is solid white. It shows as white corners in the
title bar, the splash, the taskbar and the macOS Dock.

assets/icon.svg draws the tile as a rounded rect, rx=118 on a 512 canvas.
This rebuilds each icon-N.png as RGBA with an antialiased rounded-rect alpha
mask of exactly that radius (scaled), plus a multi-size icon.ico, and writes
them to the output dir. assets/ itself is left untouched.

usage: make-transparent-icons.py <assets-dir> <out-dir>
"""
import os
import sys

from PIL import Image, ImageDraw

RADIUS_AT_512 = 118  # from assets/icon.svg: <rect ... width="512" rx="118">
SUPERSAMPLE = 4

assets, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)


def rounded_mask(size: int) -> Image.Image:
    big = size * SUPERSAMPLE
    mask = Image.new("L", (big, big), 0)
    radius = RADIUS_AT_512 * big / 512
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, big - 1, big - 1), radius=radius, fill=255)
    return mask.resize((size, size), Image.LANCZOS)


written = []
for name in sorted(os.listdir(assets)):
    if not (name.startswith("icon-") and name.endswith(".png")):
        continue
    src = Image.open(os.path.join(assets, name)).convert("RGBA")
    if src.width != src.height:
        continue
    mask = rounded_mask(src.width)
    # Keep any alpha the source already had, then cut the corners.
    alpha = src.getchannel("A")
    alpha = Image.composite(alpha, Image.new("L", src.size, 0), mask)

    # The export antialiased the tile edge against white, so edge pixels are
    # a white/tile blend. Undo that blend (colour = (c - 255*(1-a)) / a) so
    # the soft edge doesn't leave a pale halo on dark backgrounds.
    px = src.load()
    apx = alpha.load()
    for y in range(src.height):
        for x in range(src.width):
            a = apx[x, y]
            if 0 < a < 255:
                r, g, b, _ = px[x, y]
                if min(r, g, b) >= 245:
                    # pure background white that the mask grazed: drop it
                    apx[x, y] = 0
                    continue
                f = a / 255
                px[x, y] = tuple(max(0, min(255, round((c - 255 * (1 - f)) / f))) for c in (r, g, b)) + (255,)

    src.putalpha(alpha)
    src.save(os.path.join(out, name), optimize=True)
    written.append((src.width, src))

# Windows .ico from the corrected sizes (<=256).
ico_sizes = [(s, s) for s, _ in written if s <= 256]
if written:
    largest = max(written, key=lambda w: w[0])[1]
    largest.save(os.path.join(out, "icon.ico"), sizes=ico_sizes)

print(f"wrote {len(written)} transparent icons + icon.ico to {out}")
