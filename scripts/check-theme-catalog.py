#!/usr/bin/env python3
"""Checks the published theme catalog against Ghostty's own theme files.

usage: scripts/check-theme-catalog.py GHOSTTY_THEMES_DIR [BASE_URL]

For every Ghostty theme: present in index.json, same name, every color equal
(background, foreground, cursor, selection, all 16 palette entries), and the
per-theme file and the zip entry parse back to the same colors. With BASE_URL
(e.g. https://ryandanthony.github.io/wraithcairn/themes/) also checks that the
deployed index.json and zip are byte-identical to the repo and samples
per-theme files.
"""
import io
import json
import os
import random
import sys
import urllib.request
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "website", "static", "themes")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sync = __import__("sync-ghostty-themes")

errors = []


def err(msg):
    errors.append(msg)
    if len(errors) <= 20:
        print("FAIL", msg)


def colors(parsed):
    k, p = parsed["keys"], parsed["palette"]
    return {
        "background": k.get("background"),
        "foreground": k.get("foreground"),
        "cursorColor": k.get("cursor-color"),
        "cursorText": k.get("cursor-text"),
        "selectionBackground": k.get("selection-background"),
        "selectionForeground": k.get("selection-foreground"),
        "palette": [p.get(i) for i in range(16)],
    }


def parse_text(text):
    import tempfile
    with tempfile.NamedTemporaryFile("w", delete=False, encoding="utf-8") as fh:
        fh.write(text)
    try:
        return sync.parse(fh.name)
    finally:
        os.unlink(fh.name)


src = sys.argv[1]
index = json.load(open(os.path.join(OUT, "index.json"), encoding="utf-8"))
by_name = {t["name"]: t for t in index["themes"]}
zf = zipfile.ZipFile(os.path.join(OUT, "wraithcairn-themes.zip"))
zip_names = {n[len("themes/"):] for n in zf.namelist()}

source_names = sorted(n for n in os.listdir(src) if os.path.isfile(os.path.join(src, n)))
print(f"ghostty themes: {len(source_names)}  index: {len(by_name)}  zip: {len(zip_names)}  "
      f"files: {len(os.listdir(os.path.join(OUT, 'ghostty')))}")
if index["count"] != len(index["themes"]):
    err("index count field mismatch")

checked = 0
for name in source_names:
    want = colors(sync.parse(os.path.join(src, name)))
    entry = by_name.get(name)
    if entry is None:
        err(f"missing from index: {name}")
        continue
    got = {k: entry.get(k) for k in want}
    if got != want:
        err(f"index colors differ: {name}: {got} != {want}")
    per_file = os.path.join(OUT, "ghostty", entry["slug"])
    if colors(sync.parse(per_file)) != want:
        err(f"per-theme file differs: {name}")
    if name not in zip_names:
        err(f"missing from zip: {name}")
    elif colors(parse_text(zf.read("themes/" + name).decode())) != want:
        err(f"zip entry differs: {name}")
    checked += 1

extra = set(by_name) - set(source_names)
if extra:
    err(f"index has themes Ghostty lacks: {sorted(extra)[:5]}")
print(f"compared {checked} themes color-for-color (index, per-theme file, zip)")

if len(sys.argv) > 2:
    base = sys.argv[2].rstrip("/") + "/"

    def fetch(rel):
        req = urllib.request.Request(base + urllib.request.quote(rel), headers={"User-Agent": "wc-check"})
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.read()

    for rel in ("index.json", "wraithcairn-themes.zip", "NOTICE.txt"):
        live = fetch(rel)
        local = open(os.path.join(OUT, rel), "rb").read()
        print(f"live {rel}: {'identical' if live == local else 'DIFFERENT'} ({len(live)} bytes)")
        if live != local:
            err(f"deployed {rel} differs from repo")
    random.seed(7)
    sample = random.sample(index["themes"], 25) + [by_name[n] for n in ("Dracula", "Black Metal (Bathory)", "Dracula+") if n in by_name]
    for t in sample:
        live = fetch("ghostty/" + t["slug"])
        if live != open(os.path.join(OUT, "ghostty", t["slug"]), "rb").read():
            err(f"deployed ghostty/{t['slug']} differs")
    print(f"live per-theme files sampled: {len(sample)}")

print("RESULT", "OK" if not errors else f"{len(errors)} problem(s)")
sys.exit(1 if errors else 0)
