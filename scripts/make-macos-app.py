#!/usr/bin/env python3
"""Build wraithcairn.app around a published macOS single-file executable.

macOS only gives a process a Dock icon, name and app-switcher entry when it
runs from an application bundle (Contents/Info.plist + Contents/MacOS +
Contents/Resources/*.icns). A bare executable started from Finder or a
terminal shows up as "exec" or not at all. This wraps the binary so it
behaves like any other Mac app; it runs on Linux CI (no iconutil needed).

usage: make-macos-app.py <publish-dir> <out-dir> <version> [icons-dir]
"""
import os
import plistlib
import shutil
import stat
import struct
import subprocess
import sys
import tempfile

publish, out_dir, version = sys.argv[1], sys.argv[2], sys.argv[3]
assets = sys.argv[4] if len(sys.argv) > 4 else os.path.join(os.path.dirname(__file__), "..", "icons")

app = os.path.join(out_dir, "wraithcairn.app")
contents = os.path.join(app, "Contents")
macos = os.path.join(contents, "MacOS")
resources = os.path.join(contents, "Resources")
shutil.rmtree(app, ignore_errors=True)
os.makedirs(macos)
os.makedirs(resources)

# Everything the publish produced (single-file exe plus any loose natives)
# goes in Contents/MacOS so AppContext.BaseDirectory still finds them.
for name in os.listdir(publish):
    if name.endswith(".pdb"):
        continue
    src = os.path.join(publish, name)
    dst = os.path.join(macos, name)
    if os.path.isdir(src):
        shutil.copytree(src, dst)
    else:
        shutil.copy2(src, dst)

exe = os.path.join(macos, "wraithcairn")
os.chmod(exe, os.stat(exe).st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

# .icns from the bundled PNG sizes. On macOS use iconutil; elsewhere write
# PNG-encoded icns entries directly (supported since 10.7). No Pillow needed.
icns_path = os.path.join(resources, "wraithcairn.icns")
sizes = {16: "icp4", 32: "icp5", 64: "icp6", 128: "ic07", 256: "ic08", 512: "ic09"}
if shutil.which("iconutil"):
    with tempfile.TemporaryDirectory() as tmp:
        iconset = os.path.join(tmp, "wraithcairn.iconset")
        os.makedirs(iconset)
        for size in (16, 32, 128, 256, 512):
            shutil.copy(os.path.join(assets, f"icon-{size}.png"), os.path.join(iconset, f"icon_{size}x{size}.png"))
            double = size * 2
            if os.path.exists(os.path.join(assets, f"icon-{double}.png")):
                shutil.copy(os.path.join(assets, f"icon-{double}.png"),
                            os.path.join(iconset, f"icon_{size}x{size}@2x.png"))
        subprocess.run(["iconutil", "-c", "icns", iconset, "-o", icns_path], check=True)
else:
    entries = []
    for size, kind in sizes.items():
        with open(os.path.join(assets, f"icon-{size}.png"), "rb") as f:
            data = f.read()
        entries.append(kind.encode("ascii") + struct.pack(">I", len(data) + 8) + data)
    body = b"".join(entries)
    with open(icns_path, "wb") as f:
        f.write(b"icns" + struct.pack(">I", len(body) + 8) + body)

short = version.split("-")[0].split("+")[0]
with open(os.path.join(contents, "Info.plist"), "wb") as f:
    plistlib.dump({
        "CFBundleName": "wraithcairn",
        "CFBundleDisplayName": "wraithcairn",
        "CFBundleIdentifier": "com.ryandanthony.wraithcairn",
        "CFBundleExecutable": "wraithcairn",
        "CFBundleIconFile": "wraithcairn",
        "CFBundlePackageType": "APPL",
        "CFBundleShortVersionString": short,
        "CFBundleVersion": short,
        "CFBundleInfoDictionaryVersion": "6.0",
        "LSMinimumSystemVersion": "12.0",
        "LSApplicationCategoryType": "public.app-category.developer-tools",
        "NSHighResolutionCapable": True,
        "NSPrincipalClass": "NSApplication",
        "NSSupportsAutomaticGraphicsSwitching": True,
        # Voice input (Ctrl+Shift+I). Without this key macOS terminates the
        # app the moment it opens the microphone instead of asking.
        "NSMicrophoneUsageDescription": "Voice input (Ctrl+Shift+I) turns your speech into text in a terminal pane. Recognition runs entirely on this Mac; audio never leaves it.",
    }, f)

with open(os.path.join(contents, "PkgInfo"), "w") as f:
    f.write("APPL????")

print(f"built {app}")
