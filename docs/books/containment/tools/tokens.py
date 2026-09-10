"""Transcribe, verbatim, the distinct chapter/volume TOKENS and bracketed labels a rip puts on its pages.

`chapters_from_archive.py` reduces a page name to a number. When two numbering systems are present -
the scanlator's running count and the book's own printed chapter label - that reduction hides the
conflict (PLAN.md §6.3, the Baltimore defect). This prints the raw tokens instead and decides nothing.

    python tokens.py <itemId> [<itemId> ...]
"""
import collections
import re
import subprocess
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"
IMG = (".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif")
RX_TOK = re.compile(r"\bc\d{1,4}(?:\.\d+)?(?:x\d+)?\b", re.I)
RX_VOL = re.compile(r"\(v\d{1,3}(?:\.\d+)?\)", re.I)
RX_LBL = re.compile(r"\[([^\]]+)\]")
SKIP = {"dig", "digital", "Cover", "ToC", "VIZ Media", "Kodansha Comics", "Yen Press", "HQ", "r2",
        "Seven Seas Entertainment", "Square Enix", "Dark Horse Manga", "Vertical Comics"}

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
for iid in (int(a) for a in sys.argv[1:]):
    path, fn = con.execute("SELECT Path, FileName FROM Item WHERE Id = ?", (iid,)).fetchone()
    out = subprocess.run([SEVENZIP, "l", "-ba", path], capture_output=True, text=True,
                         timeout=300, errors="ignore").stdout
    ch, vol, lbl, n = collections.Counter(), collections.Counter(), collections.Counter(), 0
    for line in out.splitlines():
        parts = line.split(None, 5)
        if len(parts) < 6 or not parts[5].lower().endswith(IMG):
            continue
        n += 1
        name = parts[5]
        for t in RX_TOK.findall(name):
            ch[t.lower()] += 1
        for t in RX_VOL.findall(name):
            vol[t.lower()] += 1
        for t in RX_LBL.findall(name):
            if t not in SKIP and not t.startswith("Chapter 0"):
                lbl[t] += 1
    print(f"\n[{iid}] {fn[:78]}   {n} images")
    print(f"   chapter tokens: {', '.join(f'{k}x{v}' for k, v in sorted(ch.items()))}" if ch
          else "   chapter tokens: none")
    if vol:
        print(f"   volume tokens : {', '.join(f'{k}x{v}' for k, v in sorted(vol.items()))}")
    if lbl:
        print(f"   labels        : {', '.join(f'{k} x{v}' for k, v in sorted(lbl.items())[:24])}")
