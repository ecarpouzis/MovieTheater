"""Group one Series by the FOLDER that names each publishing run, and emit a split decision.

`python split_by_folder.py <seriesId> [--write out.jsonl]`

The other axis (propose_split.py, by title) is for shelves holding different WORKS — Fairy Tail vs Fairy
Tail - Blue Mistral. This one is for the SAME title relaunched, where only the folder tells the runs
apart: `_Suicide Squad\\01 Suicide Squad v1 (1987)` and `\\06 Suicide Squad v5 (2016)` are two runs, each
numbering from #1, and every file in both is titled "Suicide Squad".

A run folder is one carrying BOTH a volume marker (v5, Vol. 2) and a year in brackets. Files under no
such folder — chronology trees, `_Trades, Minis and One-shots`, loose specials — are left alone: this
prints them so they can be judged, and never guesses a run for them.

Writes nothing to the database. `books-series-split` applies the file, and only after a person reads it.
"""
import collections
import json
import os
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEP = chr(92)
PREFIX = SEP + SEP + "Library" + SEP + "Public" + SEP + "5 - Comics" + SEP

RX_VOL = re.compile(r"\bv(?:ol(?:ume)?)?\.?\s*\d{1,3}\b", re.I)
RX_YEAR = re.compile(r"\((?:19|20)\d{2}\)")
RX_LEAD = re.compile(r"^\s*[#_]*\d{1,3}[\s._-]+")

args = [a for a in sys.argv[1:] if not a.startswith("--")]
out_path = None
for a in sys.argv[1:]:
    if a.startswith("--write"):
        out_path = a.split("=", 1)[1] if "=" in a else "split.jsonl"

sid = int(args[0])
con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
name = con.execute("SELECT coalesce(DisplayNameOverride, Name) FROM Series WHERE Id = ?", (sid,)).fetchone()
rows = con.execute("""
    SELECT i.Id, i.Path, i.FileName, cd.IsCollection, cd.IssueNo, cd.ParsedSeriesKey
    FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
    WHERE i.SeriesId = ? AND coalesce(i.IsExcluded, 0) = 0
    ORDER BY i.Path, i.FileName""", (sid,)).fetchall()


def run_folder(path):
    """The deepest FOLDER (never the file) that names a run: a volume marker plus a year."""
    parts = [p for p in (path or "").replace("/", SEP).split(SEP) if p]
    found = None
    for part in parts[:-1]:
        if RX_VOL.search(part) and RX_YEAR.search(part):
            found = part
    return RX_LEAD.sub("", found).strip() if found else None


# A file that already carries its OWN distinct ParsedSeriesKey has an identity of its own — a compendium,
# a deluxe edition, a spin-off — and the run folder it happens to sit inside must not absorb it. Suicide
# Squad taught this: "Director's Cut" and "Casualties of War" both live under run folders.
dominant = collections.Counter(r[5] or "" for r in rows).most_common(1)[0][0]
groups = collections.defaultdict(list)
own_identity = []
for r in rows:
    if (r[5] or "") != dominant:
        own_identity.append(r)
        continue
    groups[run_folder(r[1])].append(r)

print(f"S{sid}  {name[0] if name else '?'}   {len(rows)} files, "
      f"{sum(1 for k in groups if k)} run folders + {len(groups.get(None, []))} files under none")
print(f"current ParsedSeriesKey(s): {', '.join(sorted({(r[5] or '(null)') for r in rows}))}\n")

proposal = []
for key, items in sorted(groups.items(), key=lambda kv: (kv[0] is None, -len(kv[1]))):
    nums = sorted({float(i[4]) for i in items if i[4] and re.match(r"^\d+(\.\d+)?$", str(i[4]))})
    span = f"#{nums[0]:g}-{nums[-1]:g}" if nums else "-"
    cols = sum(1 for i in items if i[3])
    label = key or "(NO RUN FOLDER — judge these by hand)"
    print(f"  [{len(items):>4} files, {cols:>3} collections]  {label}   numbers {span}")
    for f, n in collections.Counter(os.path.dirname(i[1] or "") for i in items).most_common(3):
        short = f[len(PREFIX):] if f.startswith(PREFIX) else f
        print(f"           {n:>4}  {short[:94]}")
    if key:
        for i in items:
            proposal.append({"itemId": i[0], "key": key})

if own_identity:
    print("")
    print(f"  [{len(own_identity):>4} files] LEFT ALONE - they already carry their own key, so no run absorbs them")
    for r in own_identity:
        print(f"           {r[5]!r}  {r[2][:60]}")

if out_path:
    with open(out_path, "w", encoding="utf-8") as fh:
        for p in proposal:
            fh.write(json.dumps(p, ensure_ascii=False) + "\n")
    print(f"\n{len(proposal)} decision lines -> {out_path}   "
          f"({len(groups.get(None, []))} files under no run folder were NOT included)")
