#!/usr/bin/env python3
"""Count panes (leaves) in a wraithcairn workspace.json: leaves.py FILE -> N, or ? if unreadable."""
import json
import sys


def leaves(pane):
    split = pane.get("split")
    return 1 if not split else leaves(split["firstPane"]) + leaves(split["secondPane"])


try:
    data = json.load(open(sys.argv[1]))
    print(sum(leaves(t["rootPane"]) for w in data["windows"] for t in w["tabs"]))
except Exception:
    print("?")
