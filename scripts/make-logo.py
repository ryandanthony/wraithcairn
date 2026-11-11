#!/usr/bin/env python3
"""Generate the wraithcairn icon and logo.

A drystone tower cairn (after the Nine Standards Rigg cairns) on a fell, with a
glowing >_ prompt standing centered in front of it.

Writes assets/icon.svg and assets/logo.svg, renders every PNG size with headless
Chrome, then builds the .ico and copies the results to icons/ and website/.

usage: scripts/make-logo.py          (run from the repo root)
"""
import math
import os
import random
import shutil
import subprocess
import tempfile

from PIL import Image

CX = 256

# original logo palette (assets/icon.svg)
STONES = ["#6B7D8F", "#647688", "#5D6E7E", "#576877", "#536372",
          "#4E5E6C", "#4A5866", "#46525F", "#414C58"]
MORTAR = "#131A21"
TEAL = "#7DF2D8"
RIM = "#C9F7EB"


def smooth(pts, k=0.32):
    """Closed path through pts with rounded corners."""
    n = len(pts)
    def lerp(a, b, t):
        return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t)
    mids = [lerp(pts[i], pts[(i + 1) % n], 0.5) for i in range(n)]
    d = f"M {mids[-1][0]:.1f} {mids[-1][1]:.1f} "
    for i in range(n):
        p = pts[i]
        a = lerp(mids[i - 1], p, 1 - k)
        b = lerp(p, mids[i], k)
        d += f"L {a[0]:.1f} {a[1]:.1f} Q {p[0]:.1f} {p[1]:.1f} {b[0]:.1f} {b[1]:.1f} "
        d += f"L {mids[i][0]:.1f} {mids[i][1]:.1f} "
    return d + "Z"


def boulder(cx, cy, rx, ry, rnd, n=8, irr=0.18):
    pts = []
    for i in range(n):
        a = i / n * 2 * math.pi
        r = 1 + rnd.uniform(-irr, irr)
        # flatter bottom, rounder top
        yy = math.sin(a)
        pts.append((cx + rx * r * math.cos(a), cy + ry * r * (yy if yy < 0 else yy * 0.55)))
    return smooth(pts, 0.42)


def interp(profile, t):
    for (t0, w0), (t1, w1) in zip(profile, profile[1:]):
        if t0 <= t <= t1:
            u = (t - t0) / (t1 - t0)
            u = u * u * (3 - 2 * u)
            return w0 + (w1 - w0) * u
    return profile[-1][1]


def tower(seed, base_y, top_y, half_w, profile, course=(24, 38), mark_y=300):
    """Drystone column: courses of big irregular slabs. The silhouette comes
    from the stones themselves, so edge stones poke out and the outline is
    lumpy like a real hand-built cairn."""
    rnd = random.Random(seed)
    H = base_y - top_y
    stones = []  # (pts, fill, is_edge)
    y = base_y
    while y > top_y + 6:
        h = rnd.uniform(*course)
        if y - h < top_y:
            h = y - top_y
        y0, y1 = y - h, y
        t = min(1, max(0, (base_y - (y0 + y1) / 2) / H))
        hw = half_w * interp(profile, t)
        # course edges stick out or tuck in a little
        xl = CX - hw - rnd.uniform(-5, 9)
        xr = CX + hw + rnd.uniform(-5, 9)
        # split the course into 2-4 slabs of uneven width
        span = xr - xl
        n = 1 if span < 60 else (2 if span < 105 else rnd.choice([2, 3, 3]))
        cuts = sorted(rnd.uniform(0.2, 0.8) for _ in range(n - 1))
        # keep cuts apart
        edges = [0.0] + cuts + [1.0]
        for k in range(n):
            x0 = xl + span * edges[k]
            x1 = xl + span * edges[k + 1]
            g = 2.6  # dark gap between stones
            j = lambda s=2.6: rnd.uniform(-s, s)
            sy0 = y0 + g + j(2.2)
            sy1 = y1 - g + j(1.6)
            # wedge-ish slabs: tops/bottoms not quite parallel
            tilt = rnd.uniform(-3.5, 3.5)
            pts = [
                (x0 + g + j(), sy0 + tilt),
                (x0 + (x1 - x0) * rnd.uniform(0.3, 0.45), sy0 + tilt / 2 + j(2.5)),
                (x0 + (x1 - x0) * rnd.uniform(0.55, 0.7), sy0 - tilt / 2 + j(2.5)),
                (x1 - g + j(), sy0 - tilt),
                (x1 - g + j(3.5), (sy0 + sy1) / 2 + j(3)),
                (x1 - g + j(), sy1 - tilt / 2),
                ((x0 + x1) / 2 + j(5), sy1 + j(1.5)),
                (x0 + g + j(), sy1 + tilt / 2),
                (x0 + g + j(3.5), (sy0 + sy1) / 2 + j(3)),
            ]
            # edge stones get rounder ends
            idx = int((1 - t) * 3.5 + rnd.uniform(0, 5.5))
            fill = STONES[max(0, min(len(STONES) - 1, idx))]
            stones.append((pts, fill, k in (0, n - 1), y0, y1))
        y = y0

    # capstone
    top_hw = half_w * profile[-1][1]
    cap = [(CX - top_hw * 0.9, top_y + 4), (CX - top_hw * 0.6, top_y - 14), (CX - 4, top_y - 22),
           (CX + top_hw * 0.55, top_y - 15), (CX + top_hw * 0.95, top_y + 3)]

    out = []
    # dark mass behind stones = the shadowed gaps
    out.append(f'<g id="mass{seed}">')
    for pts, _, _, _, _ in stones:
        grown = [(px + (2.5 if px > CX else -2.5), py) for px, py in pts]
        out.append(f'<path d="{smooth(grown, 0.4)}" fill="{MORTAR}" stroke="{MORTAR}" stroke-width="6" stroke-linejoin="round"/>')
    out.append(f'<path d="{smooth(cap, 0.45)}" fill="{MORTAR}" stroke="{MORTAR}" stroke-width="8" stroke-linejoin="round"/>')
    out.append('</g>')

    # all stones plus shading, clipped to the stone shapes themselves
    clip = "".join(f'<path d="{smooth(p, 0.4)}"/>' for p, *_ in stones) + f'<path d="{smooth(cap, 0.45)}"/>'
    out.append(f'<clipPath id="st{seed}">{clip}</clipPath>')
    out.append(f'<g clip-path="url(#st{seed})">')
    for pts, fill, edge, y0, y1 in stones:
        out.append(f'<path d="{smooth(pts, 0.4)}" fill="{fill}"/>')
    out.append(f'<path d="{smooth(cap, 0.45)}" fill="{STONES[0]}"/>')
    out.append('<rect width="512" height="512" fill="url(#cyl)"/>')
    out.append('<rect width="512" height="512" fill="url(#vshade)"/>')
    out.append(f'<circle cx="{CX}" cy="{mark_y}" r="150" fill="url(#tealSpill)"/>')
    out.append('</g>')

    # top-edge catchlights and lichen, drawn over the shading
    for pts, fill, edge, y0, y1 in stones:
        a, b_, c, d = pts[0], pts[1], pts[2], pts[3]
        lit = 0.42 if a[0] < CX else 0.2
        out.append(f'<path d="M {a[0]+5:.1f} {a[1]+2:.1f} L {b_[0]:.1f} {b_[1]+2:.1f} L {c[0]:.1f} {c[1]+2:.1f} '
                   f'L {d[0]-5:.1f} {d[1]+2:.1f}" stroke="#9FB2C2" stroke-width="2" '
                   f'opacity="{lit * rnd.uniform(0.6, 1.1):.2f}" fill="none" stroke-linecap="round" stroke-linejoin="round"/>')
        if rnd.random() < 0.22:
            lx = rnd.uniform(a[0] + 8, d[0] - 8) if d[0] - a[0] > 20 else (a[0] + d[0]) / 2
            ly = rnd.uniform(y0 + 8, y1 - 6)
            out.append(f'<ellipse cx="{lx:.1f}" cy="{ly:.1f}" rx="{rnd.uniform(2.5,6):.1f}" '
                       f'ry="{rnd.uniform(1.6,3):.1f}" fill="#B9C8C4" opacity="{rnd.uniform(0.15,0.3):.2f}"/>')
    ca, cb = cap[1], cap[3]
    out.append(f'<path d="M {ca[0]+2:.1f} {ca[1]+3:.1f} Q {CX-4:.1f} {top_y-19:.1f} {cb[0]-2:.1f} {cb[1]+3:.1f}" '
               f'stroke="{RIM}" stroke-width="2.5" opacity="0.45" fill="none" stroke-linecap="round"/>')
    return "\n".join(out)


def mark(cy, s=1.0, glow="glow"):
    """>_ : chevron points right, underscore on the baseline."""
    hh, cw, gap, bw, bh, sw = 50 * s, 62 * s, 14 * s, 70 * s, 20 * s, 22 * s
    total = cw + gap + bw
    x0 = CX - total / 2 + sw / 4
    x1 = x0 + cw
    bx = x1 + gap
    by = cy + hh - bh / 2 - 1
    chev = f"M {x0:.1f} {cy-hh:.1f} L {x1:.1f} {cy:.1f} L {x0:.1f} {cy+hh:.1f}"
    o = []
    # dark backing so the mark separates from the stones (reads as in front)
    o.append(f'<g filter="url(#soft9)" opacity="0.75">'
             f'<path d="{chev}" fill="none" stroke="#05080B" stroke-width="{sw+16:.1f}" '
             f'stroke-linecap="round" stroke-linejoin="round" transform="translate(4 7)"/>'
             f'<rect x="{bx-8:.1f}" y="{by-8:.1f}" width="{bw+16:.1f}" height="{bh+16:.1f}" '
             f'rx="{(bh+16)/2:.1f}" fill="#05080B" transform="translate(4 7)"/></g>')
    if glow == "glowXL":
        # wide soft bloom underneath the crisp mark
        o.append(f'<g filter="url(#bloom)" opacity="0.9">'
                 f'<path d="{chev}" fill="none" stroke="#2FE3C0" stroke-width="{sw+10:.1f}" '
                 f'stroke-linecap="round" stroke-linejoin="round"/>'
                 f'<rect x="{bx-5:.1f}" y="{by-5:.1f}" width="{bw+10:.1f}" height="{bh+10:.1f}" '
                 f'rx="{(bh+10)/2:.1f}" fill="#2FE3C0"/></g>')
    o.append(f'<g filter="url(#{glow})">'
             f'<path d="{chev}" fill="none" stroke="{TEAL}" stroke-width="{sw:.1f}" '
             f'stroke-linecap="round" stroke-linejoin="round"/>'
             f'<rect x="{bx:.1f}" y="{by:.1f}" width="{bw:.1f}" height="{bh:.1f}" rx="{bh*0.45:.1f}" fill="{TEAL}"/></g>')
    return "\n".join(o)


DEFS = f"""
<radialGradient id="bgG" cx="0.5" cy="0.36" r="0.8">
  <stop offset="0" stop-color="#1A2733"/><stop offset="1" stop-color="#0B1016"/>
</radialGradient>
<linearGradient id="cyl" x1="0" y1="0" x2="1" y2="0">
  <stop offset="0.30" stop-color="#FFFFFF" stop-opacity="0.10"/>
  <stop offset="0.45" stop-color="#FFFFFF" stop-opacity="0"/>
  <stop offset="0.58" stop-color="#000000" stop-opacity="0.05"/>
  <stop offset="0.68" stop-color="#000000" stop-opacity="0.38"/>
</linearGradient>
<linearGradient id="vshade" x1="0" y1="0" x2="0" y2="1">
  <stop offset="0.45" stop-color="#000000" stop-opacity="0"/>
  <stop offset="0.92" stop-color="#000000" stop-opacity="0.35"/>
</linearGradient>
<radialGradient id="tealSpill">
  <stop offset="0" stop-color="{TEAL}" stop-opacity="0.24"/>
  <stop offset="1" stop-color="{TEAL}" stop-opacity="0"/>
</radialGradient>
<radialGradient id="tealSpillXL">
  <stop offset="0" stop-color="{TEAL}" stop-opacity="0.42"/>
  <stop offset="0.6" stop-color="{TEAL}" stop-opacity="0.12"/>
  <stop offset="1" stop-color="{TEAL}" stop-opacity="0"/>
</radialGradient>
<linearGradient id="ridge" x1="0" y1="0" x2="0" y2="1">
  <stop offset="0" stop-color="#22313E"/><stop offset="1" stop-color="#121A22"/>
</linearGradient>
<linearGradient id="mist" x1="0" y1="0" x2="1" y2="0">
  <stop offset="0" stop-color="#9FB2C2" stop-opacity="0"/>
  <stop offset="0.5" stop-color="#C9DCE8" stop-opacity="0.55"/>
  <stop offset="1" stop-color="#9FB2C2" stop-opacity="0"/>
</linearGradient>
<filter id="glow" x="-80%" y="-80%" width="260%" height="260%">
  <feGaussianBlur stdDeviation="7" result="b"/>
  <feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge>
</filter>
<filter id="glowXL" x="-80%" y="-80%" width="260%" height="260%">
  <feGaussianBlur stdDeviation="5" result="a"/>
  <feGaussianBlur stdDeviation="12" result="b"/>
  <feMerge><feMergeNode in="b"/><feMergeNode in="a"/><feMergeNode in="SourceGraphic"/></feMerge>
</filter>
<filter id="bloom" x="-100%" y="-100%" width="300%" height="300%"><feGaussianBlur stdDeviation="22"/></filter>
<filter id="soft50" x="-120%" y="-120%" width="340%" height="340%"><feGaussianBlur stdDeviation="50"/></filter>
<filter id="soft9" x="-60%" y="-60%" width="220%" height="220%"><feGaussianBlur stdDeviation="9"/></filter>
<filter id="soft8" x="-120%" y="-120%" width="340%" height="340%"><feGaussianBlur stdDeviation="8"/></filter>
<filter id="soft5" x="-50%" y="-200%" width="200%" height="500%"><feGaussianBlur stdDeviation="5"/></filter>
"""


def tile(body):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="100%" height="100%" viewBox="0 0 512 512">\n'
            f'<defs>{DEFS}</defs>\n'
            f'<rect x="0" y="0" width="512" height="512" rx="118" fill="url(#bgG)"/>\n'
            f'<rect x="1.5" y="1.5" width="509" height="509" rx="117" fill="none" stroke="#2B3F52" stroke-width="3" opacity="0.5"/>\n'
            f'<g clip-path="url(#tileClip)">{body}</g>\n'
            f'<clipPath id="tileClip"><rect width="512" height="512" rx="118"/></clipPath>\n</svg>\n')


def foot_rocks(seed, spec):
    rnd = random.Random(seed)
    o = []
    for cx, cy, rx, ry in spec:
        o.append(f'<path d="{boulder(cx, cy, rx, ry, rnd)}" fill="#46525F" stroke="{MORTAR}" stroke-width="3"/>')
        o.append(f'<path d="M {cx-rx*0.55:.1f} {cy-ry*0.55:.1f} Q {cx:.1f} {cy-ry*0.85:.1f} {cx+rx*0.45:.1f} {cy-ry*0.6:.1f}" '
                 f'stroke="#9FB2C2" stroke-width="2" opacity="0.3" fill="none" stroke-linecap="round"/>')
    return "\n".join(o)


FONT = "'JetBrainsMono NFM','JetBrainsMono Nerd Font Mono','JetBrains Mono',monospace"


def scene():
    """The mark: fell, cairn and prompt (512x512, unclipped)."""
    my = 296
    prof = [(0, 1.0), (0.2, 1.05), (0.55, 0.95), (0.85, 0.84), (1.0, 0.64)]
    b = []
    b.append(f'<circle cx="{CX}" cy="{my}" r="170" fill="#2FE3C0" opacity="0.16" filter="url(#soft50)"/>')
    b.append('<path d="M 0 360 C 80 330 150 338 220 342 C 300 346 360 322 420 326 C 470 329 500 340 512 344 '
             'L 512 512 L 0 512 Z" fill="url(#ridge)"/>')
    b.append('<path d="M 0 360 C 80 330 150 338 220 342 C 300 346 360 322 420 326 C 470 329 500 340 512 344" '
             'stroke="#5FD9BD" stroke-width="2" opacity="0.3" fill="none"/>')
    b.append(f'<ellipse cx="{CX}" cy="455" rx="170" ry="13" fill="#000" opacity="0.5" filter="url(#soft8)"/>')
    b.append(tower(23, 455, 112, 74, prof, mark_y=my).replace('url(#tealSpill)', 'url(#tealSpillXL)'))
    b.append(foot_rocks(9, [(120, 458, 40, 17), (176, 466, 26, 12), (372, 460, 44, 19), (425, 450, 22, 11)]))
    b.append(mark(my, s=1.32, glow="glowXL"))
    return "\n".join(b)


def icon_svg():
    return tile(scene())


def logo_svg():
    """1280x320 wordmark: the icon tile plus 'wraithcairn' and the tagline."""
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="100%" height="100%" viewBox="0 0 1280 320">\n'
            f'<defs>{DEFS}\n'
            f'<linearGradient id="wordG" x1="0" y1="0" x2="1" y2="0">'
            f'<stop offset="0" stop-color="#DCE6EE"/><stop offset="1" stop-color="#9FB2C2"/></linearGradient>\n'
            f'<clipPath id="tileClip"><rect width="512" height="512" rx="118"/></clipPath></defs>\n'
            f'<g transform="translate(40,40) scale(0.46875)">\n'
            f'<rect width="512" height="512" rx="118" fill="url(#bgG)"/>\n'
            f'<rect x="1.5" y="1.5" width="509" height="509" rx="117" fill="none" stroke="#2B3F52" stroke-width="3" opacity="0.5"/>\n'
            f'<g clip-path="url(#tileClip)">{scene()}</g></g>\n'
            f'<text x="330" y="196" font-family="{FONT}" font-size="118" font-weight="700" letter-spacing="-2" '
            f'fill="url(#wordG)">wraith<tspan fill="#5FD9BD">cairn</tspan></text>\n'
            f'<text x="336" y="252" font-family="{FONT}" font-size="34" font-weight="500" letter-spacing="10" '
            f'fill="#5E7082">YOUR SHELLS, RISEN</text>\n</svg>\n')


def chrome(svg_path, png_path, w, h, transparent=True):
    bg = ["--default-background-color=00000000"] if transparent else []
    subprocess.run(["google-chrome", "--headless", "--disable-gpu", "--hide-scrollbars",
                    f"--screenshot={png_path}", f"--window-size={w},{h}", *bg,
                    "file://" + os.path.abspath(svg_path)],
                   check=True, capture_output=True)


def main():
    with open("assets/icon.svg", "w") as f:
        f.write(icon_svg())
    with open("assets/logo.svg", "w") as f:
        f.write(logo_svg())

    with tempfile.TemporaryDirectory() as tmp:
        big = os.path.join(tmp, "icon-1024.png")
        chrome("assets/icon.svg", big, 1024, 1024)
        master = Image.open(big).convert("RGBA")
        sizes = [16, 32, 48, 64, 128, 256, 512]
        for s in sizes:
            im = master.resize((s, s), Image.LANCZOS)
            for d in ("assets", "icons"):
                im.save(f"{d}/icon-{s}.png", optimize=True)
        ico = [(s, s) for s in sizes if s <= 256]
        for d in ("assets", "icons"):
            master.resize((256, 256), Image.LANCZOS).save(f"{d}/icon.ico", sizes=ico)

        # opaque white like before: used as the social card
        chrome("assets/logo.svg", "assets/logo.png", 1280, 320, transparent=False)
        Image.open("assets/logo.png").convert("RGB").save("assets/logo.png", optimize=True)

    shutil.copy("assets/icon.svg", "website/static/img/icon.svg")
    shutil.copy("assets/icon-512.png", "website/static/img/icon-512.png")
    shutil.copy("assets/icon-32.png", "website/static/img/favicon.png")
    shutil.copy("assets/logo.png", "website/static/img/wraithcairn-social-card.png")
    print("wrote assets/, icons/ and website/static/img/")


if __name__ == "__main__":
    main()
