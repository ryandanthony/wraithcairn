#!/usr/bin/env python3
"""Rebuild the wraithcairn theme catalog from Ghostty's built-in themes.

Ghostty ships ~800 color themes (from iTerm2-Color-Schemes) as small
key = value files. This normalizes each one and publishes them with the docs
site, where the app's theme picker downloads them:

  website/static/themes/index.json             catalog: every theme's colors
  website/static/themes/ghostty/<slug>         one Ghostty-format file each
  website/static/themes/wraithcairn-themes.zip all of them, named as in Ghostty
  website/static/themes/NOTICE.txt             attribution

usage: scripts/sync-ghostty-themes.py [GHOSTTY_THEMES_DIR]

Without an argument it looks in the usual install dirs, then extracts the
themes from a Ghostty AppImage (~/Applications/Ghostty-*.AppImage, or the one
`ghostty` on PATH points at). The argument may also be an AppImage.
"""
import glob
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "website", "static", "themes")
KEYS = (
    "background",
    "foreground",
    "cursor-color",
    "cursor-text",
    "selection-background",
    "selection-foreground",
)
HEX = re.compile(r"^#?([0-9a-fA-F]{6})$")


def extract_appimage(appimage: str) -> str | None:
    """Pulls share/ghostty/themes out of an AppImage into a temp dir."""
    tmp = tempfile.mkdtemp(prefix="ghostty-themes-")
    subprocess.run(
        [appimage, "--appimage-extract", "share/ghostty/themes/*"],
        cwd=tmp,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        timeout=120,
        check=False,
    )
    for found in glob.glob(os.path.join(tmp, "*", "share", "ghostty", "themes")):
        if len(os.listdir(found)) > 100:
            return found
    return None


def find_source() -> str:
    candidates = [
        "/usr/share/ghostty/themes",
        "/usr/local/share/ghostty/themes",
        os.path.expanduser("~/.local/share/ghostty/themes"),
        "/Applications/Ghostty.app/Contents/Resources/ghostty/themes",
    ]
    for c in candidates:
        if os.path.isdir(c) and len(os.listdir(c)) > 100:
            return c

    appimages = sorted(glob.glob(os.path.expanduser("~/Applications/Ghostty-*.AppImage")), reverse=True)
    on_path = shutil.which("ghostty")
    if on_path and os.path.realpath(on_path).endswith(".AppImage"):
        appimages.insert(0, os.path.realpath(on_path))
    for appimage in appimages:
        if found := extract_appimage(appimage):
            return found
    sys.exit("Ghostty themes not found; pass the themes directory (or AppImage) as an argument")


def slugify(name: str) -> str:
    s = name.lower().replace("+", "-plus")
    return re.sub(r"[^a-z0-9]+", "-", s).strip("-")


def color(value: str) -> str | None:
    m = HEX.match(value.strip())
    return f"#{m.group(1).lower()}" if m else None


def parse(path: str) -> dict:
    keys: dict[str, str] = {}
    palette: dict[int, str] = {}
    with open(path, encoding="utf-8") as fh:
        for raw in fh:
            line = raw.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = (p.strip() for p in line.split("=", 1))
            if key == "palette":
                idx, _, hex_ = value.partition("=")
                c = color(hex_)
                if idx.strip().isdigit() and c:
                    palette[int(idx)] = c
            elif key in KEYS and (c := color(value)):
                keys[key] = c
    return {"keys": keys, "palette": palette}


def luminance(hex_: str) -> float:
    r, g, b = (int(hex_[i : i + 2], 16) / 255 for i in (1, 3, 5))
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def to_ghostty(theme: dict) -> str:
    lines = [f"palette = {i}={c}" for i, c in enumerate(theme["palette"])]
    for key, field in (
        ("background", "background"),
        ("foreground", "foreground"),
        ("cursor-color", "cursorColor"),
        ("cursor-text", "cursorText"),
        ("selection-background", "selectionBackground"),
        ("selection-foreground", "selectionForeground"),
    ):
        if theme.get(field):
            lines.append(f"{key} = {theme[field]}")
    return "\n".join(lines) + "\n"


def ghostty_version() -> str:
    try:
        out = subprocess.run(
            ["ghostty", "--version"], capture_output=True, text=True, timeout=10, stdin=subprocess.DEVNULL
        ).stdout
        m = re.search(r"Ghostty (\S+)", out)
        return m.group(1) if m else "unknown"
    except Exception:
        return "unknown"


def main() -> None:
    src = sys.argv[1] if len(sys.argv) > 1 else find_source()
    if os.path.isfile(src) and src.endswith(".AppImage"):
        src = extract_appimage(os.path.abspath(src)) or sys.exit(f"no themes in {src}")
    themes = []
    slugs: dict[str, str] = {}
    for name in sorted(os.listdir(src), key=str.casefold):
        path = os.path.join(src, name)
        if not os.path.isfile(path):
            continue
        parsed = parse(path)
        keys, pal = parsed["keys"], parsed["palette"]
        if "background" not in keys or "foreground" not in keys or len(pal) < 16:
            print(f"skip {name}: incomplete", file=sys.stderr)
            continue
        slug = slugify(name)
        if slug in slugs:
            sys.exit(f"slug collision: {name!r} and {slugs[slug]!r} -> {slug}")
        slugs[slug] = name
        themes.append(
            {
                "name": name,
                "slug": slug,
                "dark": luminance(keys["background"]) < 0.5,
                "background": keys["background"],
                "foreground": keys["foreground"],
                "cursorColor": keys.get("cursor-color"),
                "cursorText": keys.get("cursor-text"),
                "selectionBackground": keys.get("selection-background"),
                "selectionForeground": keys.get("selection-foreground"),
                "palette": [pal[i] for i in range(16)],
            }
        )

    if os.path.isdir(OUT):
        shutil.rmtree(OUT)
    os.makedirs(os.path.join(OUT, "ghostty"))

    version = ghostty_version()
    index = {
        "version": 1,
        "source": f"Ghostty {version} built-in themes (from iTerm2-Color-Schemes, MIT)",
        "count": len(themes),
        "themes": [{k: v for k, v in t.items() if v is not None} for t in themes],
    }
    with open(os.path.join(OUT, "index.json"), "w", encoding="utf-8") as fh:
        json.dump(index, fh, separators=(",", ":"), ensure_ascii=False)
        fh.write("\n")

    with zipfile.ZipFile(os.path.join(OUT, "wraithcairn-themes.zip"), "w", zipfile.ZIP_DEFLATED) as zf:
        for t in themes:
            text = to_ghostty(t)
            with open(os.path.join(OUT, "ghostty", t["slug"]), "w", encoding="utf-8") as fh:
                fh.write(text)
            info = zipfile.ZipInfo(f"themes/{t['name']}", date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            zf.writestr(info, text)

    with open(os.path.join(OUT, "NOTICE.txt"), "w", encoding="utf-8") as fh:
        fh.write(
            f"These {len(themes)} color themes mirror the themes bundled with Ghostty {version}\n"
            "(https://ghostty.org), which come from the iTerm2-Color-Schemes project\n"
            "(https://github.com/mbadolato/iTerm2-Color-Schemes), MIT licensed.\n"
            "Each theme's colors belong to its original author. Regenerate with\n"
            "scripts/sync-ghostty-themes.py.\n"
        )

    print(f"{len(themes)} themes from {src} -> {OUT}")


if __name__ == "__main__":
    main()
