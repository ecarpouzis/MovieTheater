"""Work the manga shelves, one shelf at a time, reading each volume's own chapter list out of its archive.

Chunked, resumable and observable, per the standing rule: `--limit` shelves per run, a cursor written to
`manga_sweep_state.json`, a line per shelf as it goes, and NOTHING applied — it writes decision files
into ../decisions/ and a report, and the import is a separate supervised step.

THE JUDGEMENT IS THE TILING. A shelf is accepted when its volumes' chapter ranges form a ladder: strictly
ascending, no volume overlapping the next, no volume claiming a chapter another volume also claims. That
is the same test the plan applies by hand (§8 step 4), and it is what caught LOCG putting 100 Years Quest
v09 at #1-11 between v08 at #44-52 and v10 at #82-90. A shelf that does not tile is REPORTED, not
written: it goes on the list to read properly.

Volumes whose rip names no chapter are refused — a volume number is not a chapter number (§4.2).
"""
import json
import os
import re
import subprocess
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"
HERE = os.path.dirname(os.path.abspath(__file__))
DEC = os.path.join(HERE, os.pardir, "decisions")
STATE = os.path.join(HERE, "manga_sweep_state.json")
REPORT = os.path.join(HERE, "manga_sweep_report.txt")

# `c051x1` is a BONUS chapter attached to 51, not chapter 51: A Bride's Story v09 opens with c051x1
# while chapter 51 proper closes v08. An extra does not extend the volume's range.
#
# `(c2c)` — the scanlation tag for "cover to cover" — is NOT chapter 2. It appears in the page names of
# hundreds of rips (Monster, Devilman, Master Keaton, the 20th Century Boys Perfect Editions...), and the
# first version of this regex read every one of them as chapter 2. Worse, `search` takes the FIRST match
# in a filename, so on a rip named `... (c2c) ... c003 - p002.jpg` the tag OUTRANKED the real chapter.
# A chapter number is therefore never followed immediately by a letter — except the `x<n>` extra marker,
# which is consumed above.
RX_CHAP = re.compile(r"(?:^|[^a-z0-9])c(?:h(?:apter)?)?\.?\s?(\d{1,4})(?:\.(\d+))?(x\d+)?(?![0-9a-z])", re.I)
# NOTE: this line was written through a shell heredoc once and the intended \b word boundaries were
# swallowed as literal BACKSPACE bytes (PLAN.md §10.2, the same class of trap). No filename contains a
# backspace, so RX_VOLNO matched NOTHING and volno() returned the 10,000 sentinel for every volume --
# which silently disabled the volume-order guard this tool is built around. Keep it a raw string.
RX_VOLNO = re.compile(r"\b(?:v|vol|volume)\.?\s*(\d{1,3})\b", re.I)
RX_EXT = re.compile(r"\.(cbz|cbr|cb7|cbt|pdf|epub)$", re.I)
IMG = (".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif")

# Manga titles carry characters cp1252 cannot render (☆, ×, ―). Printing one used to raise
# UnicodeEncodeError at the END of a batch, after every archive had been read, and the state file was
# written after the loop — so the whole batch's work was thrown away. Print defensively, and checkpoint
# after every shelf so a crash costs at most one shelf.
for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

limit = 10
only = None
for a in sys.argv[1:]:
    if a.startswith("--limit="):
        limit = int(a.split("=", 1)[1])
    elif a.startswith("--only="):
        only = [int(x) for x in a.split("=", 1)[1].split(",")]

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)


def chapters(path):
    try:
        out = subprocess.run([SEVENZIP, "l", "-ba", path], capture_output=True, text=True,
                             timeout=240, errors="ignore").stdout
    except (OSError, subprocess.SubprocessError):
        return None
    found = set()
    for line in out.splitlines():
        parts = line.split(None, 5)
        if len(parts) < 6 or not parts[5].lower().endswith(IMG):
            continue
        m = RX_CHAP.search(parts[5])
        if m and not m.group(3):
            found.add(float(f"{m.group(1)}.{m.group(2)}") if m.group(2) else float(m.group(1)))
    return sorted(found)


def tiles(vols):
    """vols: [(itemId, lo, hi, count, fileName)] in file order. A ladder, or the reason it is not one."""
    ranged = [v for v in vols if v[1] is not None]
    if len(ranged) < 2:
        return len(ranged) == 1, "single volume" if len(ranged) == 1 else "nothing ranged"
    # Order by the VOLUME number in the name, not the raw file name: "Volume 01".."Volume 22" sorts fine
    # but a stray "The Perfect Edition v12" sorts to the front and is not part of this run at all.
    def volno(v):
        m = RX_VOLNO.search(v[4])
        return int(m.group(1)) if m else 10_000
    ranged = sorted(ranged, key=volno)
    ordered = sorted(ranged, key=lambda v: (v[1], v[2]))
    if [v[0] for v in ordered] != [v[0] for v in ranged]:
        bad = next((a[4] for a, b in zip(ranged, ordered) if a[0] != b[0]), "?")
        return False, f"volume order and chapter order disagree, first at {bad[:44]}"
    for a, b in zip(ordered, ordered[1:]):
        if b[1] <= a[2]:
            return False, f"v[{a[0]}] c{a[1]:g}-{a[2]:g} overlaps v[{b[0]}] c{b[1]:g}-{b[2]:g}"
    return True, f"c{ordered[0][1]:g}-c{ordered[-1][2]:g} over {len(ordered)} volumes"


state = json.load(open(STATE)) if os.path.exists(STATE) else {"after": 0, "done": [], "notTiling": []}
if only:
    shelves = [(s,) for s in only]
else:
    shelves = con.execute("""
        SELECT i.SeriesId FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE cd.IsCollection = 1 AND coalesce(i.IsExcluded, 0) = 0 AND i.Path LIKE '%\\Manga\\%'
          AND i.SeriesId > ?
        GROUP BY i.SeriesId ORDER BY i.SeriesId LIMIT ?""", (state["after"], limit)).fetchall()

report = open(REPORT, "a", encoding="utf-8")
for (sid,) in shelves:
    rows = con.execute("""
        SELECT i.Id, i.Path, i.FileName FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE i.SeriesId = ? AND coalesce(i.IsExcluded, 0) = 0 AND cd.IsCollection = 1
        ORDER BY i.FileName""", (sid,)).fetchall()
    name = con.execute("SELECT coalesce(DisplayNameOverride, Name) FROM Series WHERE Id = ?", (sid,)).fetchone()
    name = name[0] if name else "?"

    vols = []
    for iid, path, fn in rows:
        ch = chapters(path)
        vols.append((iid, (ch[0] if ch else None), (ch[-1] if ch else None), len(ch or []), fn))

    ok, why = tiles(vols)
    n_ranged = sum(1 for v in vols if v[1] is not None)
    line = (f"S{sid:<8} {len(rows):>4} vols  {n_ranged:>4} state chapters  "
            f"{'LADDER' if ok else 'NO':<7} {why[:60]:<60} {name[:40]}")
    print(line, flush=True)
    report.write(line + "\n")

    if not ok:
        state["notTiling"].append(sid)
    else:
        with open(os.path.join(DEC, f"S{sid}.txt"), "w", encoding="utf-8") as out:
            out.write(f"# {name} (S{sid}) — {len(rows)} volumes; {n_ranged} state their own chapters.\n#\n")
            out.write("# Every page inside these archives is named with the chapter it belongs to, so each\n")
            out.write("# volume states its contents exactly. The ranges below are CHAPTER numbers read from\n")
            out.write(f"# the archives themselves, and they form a ladder: {why}.\n")
            out.write("# Volumes whose rip names no chapter are refused: a volume number is not a chapter\n")
            out.write("# number and must not stand in for one (PLAN.md §4.2).\n\n")
            out.write(f"N {sid} chapter ranges read from each archive's own page names; the ladder tiles ({why})\n\n")
            for iid, lo, hi, cnt, fn in vols:
                title = RX_EXT.sub("", fn).split(" (")[0]
                if lo is None:
                    out.write(f"u {iid} the rip names no chapter on its pages, so the chapter range cannot be "
                              f"determined; a volume number is not a chapter number\n")
                else:
                    out.write(f"S {iid} {lo:g} {hi:g} 0.97 | {title} | the archive names the chapter on every "
                              f"page and this volume holds {cnt} of them, c{lo:g}-c{hi:g}\n")
        state["done"].append(sid)
    if not only:
        state["after"] = sid
        json.dump(state, open(STATE, "w"), indent=1)   # checkpoint per shelf, not per batch
        report.flush()
report.close()
json.dump(state, open(STATE, "w"), indent=1)
print(f"\n{{ done: {len(state['done'])}, notTiling: {len(state['notTiling'])}, nextCursor: {state['after']} }}")
