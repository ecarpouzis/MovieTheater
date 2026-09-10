"""Summarise dirs_batch.txt: which shelves' archives carry per-chapter FOLDERS and which are flat.

A flat archive says nothing and its book has to be opened; one with per-chapter folders has already
stated its contents. This only sorts the work - it decides nothing and writes no decision file.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "dirs_batch.txt")

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

show = "--show" in sys.argv
shelf = None
books = []
out = []


def flush():
    if shelf is None:
        return
    withdirs = [b for b in books if b[1]]
    out.append((shelf, len(books), len(withdirs), withdirs[:2]))


for line in open(SRC, encoding="utf-8", errors="replace"):
    if line.startswith("====="):
        flush()
        shelf = line.strip()[6:]
        books = []
    elif line.startswith("  ["):
        books.append([line.strip(), []])
    elif line.startswith("        ") and books:
        d = line.strip()
        if not d.startswith("<"):
            books[-1][1].append(d)
flush()

for shelf, n, k, sample in out:
    tag = "FOLDERS" if k else "flat   "
    print(f"{tag} {k:>3}/{n:<3}  {shelf[:78]}")
    if show and sample:
        for _, ds in sample:
            print(f"        {ds[0]}   ...   {ds[-1]}")
