"""Summarise chap_batch.txt: which shelves state their own chapters on the page, and which are silent.

A shelf whose archives name a chapter on every page has already stated its contents; a silent one has
to be opened. This only sorts the work - it decides nothing and writes no decision file.

    python chap_summary.py [--show] [--tokens]
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "chap_batch.txt")

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

show = "--show" in sys.argv
only_tokens = "--tokens" in sys.argv

shelf, books, out = None, [], []


def flush():
    if shelf is not None:
        out.append((shelf, books[:]))


for line in open(SRC, encoding="utf-8", errors="replace"):
    if line.startswith("====="):
        flush()
        shelf = line.strip()[6:]
        books = []
    elif line.startswith("  ["):
        books.append(line.rstrip())
flush()

for shelf, bs in out:
    withtok = [b for b in bs if re.search(r"\bc[\d.]+-[\d.]+ \(", b)]
    withvol = [b for b in bs if " vols " in b]
    tag = "TOKENS " if withtok else ("VOLS   " if withvol else "silent ")
    if only_tokens and not withtok and not withvol:
        continue
    print(f"{tag} {len(withtok):>3}/{len(bs):<3}  {shelf[:74]}")
    if show and withtok:
        for b in bs:
            print(f"     {b.strip()}")
