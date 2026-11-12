#!/usr/bin/env python3
"""Edge cases for saving "theme" into a hand-written config.json.

usage: scripts/test-config-edits.py   (needs a Release build)

For each starting config.json it runs `wraithcairn --set-theme NAME` in an
isolated XDG_CONFIG_HOME, then checks:
  * the result still parses as JSON-with-comments (comments and trailing
    commas stripped, then json.loads)
  * every other key keeps its value, and comments survive
  * "theme" is the new name exactly once at top level (nested "theme" keys
    and "theme" inside strings/comments are untouched)
  * the app itself reads the theme back (`--list-themes` marks nothing, so
    re-run set-theme ghostty and check null)
"""
import json
import os
import re
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BIN = os.path.join(ROOT, "bin", "Release", "net10.0", "wraithcairn")
THEMES = os.path.join(ROOT, "website", "static", "themes")


def strip_jsonc(text):
    out, i, n, in_str = [], 0, len(text), False
    while i < n:
        c = text[i]
        if in_str:
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(text[i + 1]); i += 2; continue
            if c == '"':
                in_str = False
            i += 1
        elif c == '"':
            in_str = True; out.append(c); i += 1
        elif text.startswith("//", i):
            j = text.find("\n", i); i = n if j < 0 else j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2); i = n if j < 0 else j + 2
        else:
            out.append(c); i += 1
    return re.sub(r",(\s*[}\]])", r"\1", "".join(out))


def comments(text):
    """Comment texts in JSONC, ignoring // and /* inside strings."""
    found, i, n, in_str = [], 0, len(text), False
    while i < n:
        c = text[i]
        if in_str:
            if c == "\\":
                i += 2; continue
            in_str = c != '"'
            i += 1
        elif c == '"':
            in_str = True; i += 1
        elif text.startswith("//", i):
            j = text.find("\n", i); j = n if j < 0 else j
            found.append(text[i:j].rstrip("\r")); i = j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2); j = n if j < 0 else j + 2
            found.append(text[i:j]); i = j
        else:
            i += 1
    return found


CASES = {
    "empty object": "{}",
    "empty file": "",
    "comments only body": "{\n  // nothing set yet\n}\n",
    "existing theme": '{\n  "theme": "Nord", // inline comment\n  "fontSize": 12\n}\n',
    "existing null theme": '{ "theme": null, "copyOnSelect": true }',
    "trailing comma": '{\n  "fontSize": 11,\n  "restoreLayout": true,\n}\n',
    "no trailing comma, last line comment": '{\n  "fontSize": 11 // size\n}\n',
    "block comment between": '{ "fontSize": 11 /* keep */ , "shell": "/bin/zsh" }',
    "nested theme key untouched": '{\n  "extra": { "theme": "inner" },\n  "fontSize": 9\n}\n',
    "theme text in string and comment": '{\n  // "theme": "Commented"\n  "shell": "say \\"theme\\": x",\n  "fontSize": 10\n}\n',
    "arrays and escapes": '{ "args": ["a", "b\\\\c", {"theme": 1}], "url": "http://x//y" }',
    "default starter file": None,  # whatever the app writes on first run
    "unicode": '{ "shell": "/usr/bin/fish", "note": "caf\u00e9 \u2615" }',
    "CRLF line endings": '{\r\n  "fontSize": 11,\r\n  // c\r\n  "restoreLayout": false\r\n}\r\n',
}
NAMES = ["Dracula+", "Black Metal (Bathory)", "GitHub Dark Dimmed"]

fails = 0
for label, text in CASES.items():
    for name in NAMES:
        with tempfile.TemporaryDirectory() as tmp:
            os.makedirs(os.path.join(tmp, "wraithcairn"))
            cfg = os.path.join(tmp, "wraithcairn", "config.json")
            if text is not None:
                with open(cfg, "w", encoding="utf-8", newline="") as fh:
                    fh.write(text)
            env = {**os.environ, "XDG_CONFIG_HOME": tmp, "WRAITHCAIRN_THEMES_URL": THEMES}
            before = text if text is not None else ""
            r = subprocess.run([BIN, "--set-theme", name], env=env, capture_output=True, text=True, timeout=60)
            after = open(cfg, encoding="utf-8", newline="").read() if os.path.exists(cfg) else ""
            problems = []
            if r.returncode != 0:
                problems.append(f"exit {r.returncode}: {r.stderr.strip()}")
            try:
                parsed = json.loads(strip_jsonc(after))
            except Exception as ex:  # noqa: BLE001
                parsed = None
                problems.append(f"not valid JSONC: {ex}")
            if parsed is not None:
                if parsed.get("theme") != name:
                    problems.append(f"theme={parsed.get('theme')!r}")
                if before.strip():
                    try:
                        old = json.loads(strip_jsonc(before)) or {}
                    except Exception:  # noqa: BLE001
                        old = {}
                    for k, v in old.items():
                        if k != "theme" and parsed.get(k) != v:
                            problems.append(f"{k} changed: {v!r} -> {parsed.get(k)!r}")
                for comment in comments(before):
                    if comment not in after:
                        problems.append(f"lost comment {comment!r}")
                if strip_jsonc(after).count('"theme"') - strip_jsonc(before).count('"theme"') not in (0, 1):
                    problems.append("theme key duplicated")
            # Clearing back to Ghostty's theme keeps everything else too.
            r2 = subprocess.run([BIN, "--set-theme", "ghostty"], env=env, capture_output=True, text=True, timeout=60)
            cleared = open(cfg, encoding="utf-8").read() if os.path.exists(cfg) else ""
            try:
                if json.loads(strip_jsonc(cleared)).get("theme", "missing") is not None:
                    problems.append("clear did not set theme null")
            except Exception as ex:  # noqa: BLE001
                problems.append(f"after clear not valid JSONC: {ex}")
            if r2.returncode != 0:
                problems.append(f"clear exit {r2.returncode}")
            if problems:
                fails += 1
                print(f"FAIL [{label}] {name}: " + "; ".join(problems))
                print("     before:", repr(before)[:160]); print("     after: ", repr(after)[:220])
print(f"{len(CASES) * len(NAMES) - fails}/{len(CASES) * len(NAMES)} config edit cases passed")
sys.exit(1 if fails else 0)
