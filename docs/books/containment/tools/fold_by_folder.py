"""Re-key every file under a TITLE FOLDER to the run its folder names — across all Series at once.

WHY THIS AND NOT split_by_folder.py. Splitting one shelf at a time separates a run's trades from its
issues whenever the parser had already scattered them: `01 Nightwing v1 (1995) + v2 (1996)` holds files
currently on FIVE different Series, and `04 Nightwing v4.1 (2016)` on five more. Containment only nests
inside a Series, so a per-shelf split can strand a trade away from the issues it collects — measured, the
first pass of splits dropped nested issues from 11,731 to 10,333 for exactly this reason.

The folder tree is the librarian's own judgement about which run a file belongs to, and it does not care
which Series the filename parser guessed. So the unit of work is the title folder, and every file under
one of its run folders is keyed to that run.

WHAT COUNTS AS A RUN FOLDER. An immediate child of the title folder carrying BOTH a volume marker and a
year, after a leading ordinal is stripped (`04 Nightwing v4.1 (2016)` -> `Nightwing v4.1 (2016)`).
Subdivisions merge: `v4.1`, `v4.2` and `v4.3` are one run whose issues run straight through, so they fold
to `Nightwing v4 (2016)` — the earliest year the subdivisions carry. A folder naming two runs at once
(`v1 (1995) + v2 (1996)`) is only folded when the issue numbers present can only be one of them; the tool
reports the evidence and refuses otherwise.

THE GUARD. A file is folded only when its current ParsedSeriesKey is the run's own title (one is a prefix
of the other, normalized). `Nightwing - The New Order` sitting inside a Nightwing run folder is a
different work and is LEFT ALONE, listed, for a person to look at. Files under no run folder — chronology
trees, `_Trades, Minis and One-shots` — are never touched.

`python fold_by_folder.py "<title folder name>" [--write out.jsonl] [--all]`
  --all  every title folder under 5 - Comics that has more than one run folder (survey mode)
"""
import collections
import json
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEP = chr(92)
RX_VOL = re.compile(r"\bv(?:ol(?:ume)?)?\.?\s*\d{1,3}(?:\.\d+)?\b", re.I)
RX_VOLNUM = re.compile(r"\bv(?:ol(?:ume)?)?\.?\s*(\d{1,3})(?:\.(\d+))?\b", re.I)
RX_YEAR = re.compile(r"\((?:19|20)\d{2}\)")
RX_TAIL = re.compile(r"(?:\s*\((?:19|20)\d{2}\)|\s+v(?:ol)?\.?\s*\d{1,3}(?:\.\d+)?)+\s*$", re.I)
RX_LEAD = re.compile(r"^\s*[#_]*\d{1,3}[\s._-]+")


def norm(s):
    return re.sub(r"[^a-z0-9]+", "", (s or "").lower())


def run_of(folder):
    """(title stem, volume numbers, years, is_subdivision) for a run folder, or None.

    The YEAR is part of a run's identity and only a `vN.M` subdivision may cross it. `Fantastic Four
    v1 (1961)`, `v1 (2003)` and `v1 (2015)` are three separate runs that share a volume number, and keying
    on the number alone fuses 495 files from three shelves into one. `Harley Quinn v4.1 (2021)` and
    `v4.2 (2024)` are one run whose issues run straight through, and only there does the year give way."""
    name = RX_LEAD.sub("", folder).strip()
    if not RX_VOL.search(name) or not RX_YEAR.search(name):
        return None
    nums = RX_VOLNUM.findall(name)
    years = RX_YEAR.findall(name)
    if not nums or not years:
        return None
    stem = name[:RX_VOLNUM.search(name).start()].strip(" -_.")
    subdivided = any(sub for _, sub in nums)
    return stem, sorted({n for n, _ in nums}), sorted(set(years)), subdivided


def fold(title_folder, con, verbose=True, assign=None, rows=None):
    """assign: {top folder name: key} — a folder naming two runs, settled by READING it. Used for
    `01 Nightwing v1 (1995) + v2 (1996)`, whose 96 files carry #0 and #71-153 and eight TPBs that all
    belong to the 1996 ongoing; the 1995 four-issue mini has no file here at all."""
    assign = assign or {}
    mark = SEP + title_folder + SEP
    if rows is not None:
        rows = [r for r in rows if mark in (r[1] or "")]
    else:
        rows = con.execute("""SELECT i.Id, i.Path, i.FileName, cd.IsCollection, cd.IssueNo, cd.ParsedSeriesKey,
                                 i.SeriesId
                          FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
                          WHERE i.Path LIKE ? AND coalesce(i.IsExcluded,0) = 0""",
                          (f"%{mark}%",)).fetchall()
    # A candidate folder is only a RUN folder if it looks like a run rather than one book. The library
    # names trade folders the same way — `Green Lantern - New Guardians v01 (2012)` holds exactly one
    # collected edition, and `Grayson v01 (2015)` another — so the shape alone cannot tell them apart.
    # What can: a run has issues, or it has several books. One collected edition alone in a folder is a
    # trade, and folding it would invent a run out of a single book.
    stats = collections.defaultdict(lambda: [0, 0])
    for _, path, _, iscol, _, _, _ in rows:
        segs = path.split(mark, 1)[1].split(SEP)
        cand = next((x for x in segs[:-1] if run_of(x)), None)      # [:-1] — the last segment is the file
        if cand:
            stats[cand][0] += 1
            stats[cand][1] += 0 if iscol else 1
    real = {k for k, (n, iss) in stats.items() if iss >= 1 or n >= 3}

    groups, skipped, offkey = collections.defaultdict(list), [], collections.Counter()
    for iid, path, fn, iscol, issue, key, sid in rows:
        # The run folder is the SHALLOWEST segment below the title folder that names a run. Shallowest,
        # not deepest: _Green Lantern puts its runs one level down under _Completed Series, while a run
        # folder such as `08 Batman v3.1 (2020)` can itself CONTAIN a trade folder named
        # `Batman Vol. 05 - Fear State Tie Ins (2021)` — which matches the very same shape. Taking the
        # first match reaches the first and never the second.
        segs = path.split(mark, 1)[1].split(SEP)
        # `Item.Path` ends in the FILE NAME, so the last segment is a file, not a folder — and a manga
        # volume file is named exactly like a run folder (`One Piece v001 (2003) (Digital) ....cbz`).
        # Falling back to segs[0] when nothing qualifies handed that filename straight to run_of and made
        # one Series per VOLUME: 103 for One Piece, 73 for Naruto, 67 for Bleach. When no segment is a real
        # run folder the file simply is not part of a run this tool can name, and it is left alone.
        top = next((x for x in segs if run_of(x) and x in real), None)
        if top is None:
            skipped.append((iid, segs[0], key))
            continue
        if top in assign:
            groups[("ASSIGNED", assign[top])].append((iid, key, issue, iscol, sid, ""))
            continue
        r = run_of(top)
        if r is None:
            skipped.append((iid, top, key))
            continue
        stem, nums, years, subdivided = r
        if len(nums) > 1:                               # `v1 (1995) + v2 (1996)`
            groups[("AMBIGUOUS", stem, tuple(nums), tuple(years))].append((iid, key, issue, iscol, sid))
            continue
        # a subdivided folder drops the year so its eras rejoin; every other run keeps it
        groups[(stem, nums[0], "" if subdivided else years[0])].append(
            (iid, key, issue, iscol, sid, years[0]))

    proposal, notes = [], []
    for g, members in sorted(groups.items(), key=lambda kv: str(kv[0])):
        if g[0] == "AMBIGUOUS":
            _, stem, nums, years = g
            iss = sorted(float(m[2]) for m in members if m[2] not in (None, "") and str(m[2]).replace(".", "").isdigit())
            notes.append((stem, nums, years, members, iss))
            continue
        if g[0] == "ASSIGNED":
            key = g[1]
            stem = re.split(r"\sv\d", key)[0].strip()
        else:
            stem, vol, year = g
            if not year:
                year = min(m[5] for m in members)       # subdivisions: the earliest era names the run
            key = f"{stem} v{vol} {year}"
        for iid, cur, _, _, _, _ in members:
            # EXACT match after stripping a trailing year or volume marker — not a prefix. A prefix test
            # folds `Nightwing - Alfred's Return` (a 1995 one-shot carrying #1) into the 1996 ongoing and
            # collides it with that run's own #1. `Nightwing`, `Nightwing (1996)` and `Nightwing (2013)`
            # all reduce to the same stem and do belong; a named spin-off does not.
            a, b = norm(RX_TAIL.sub("", cur or "")), norm(stem)
            if a and b and a != b:
                offkey[cur] += 1
                continue
            proposal.append({"itemId": iid, "key": key})
        if verbose:
            srcs = collections.Counter(m[4] for m in members)
            print(f"  {key:<46} {len(members):>4} files from {len(srcs)} shelf/shelves {sorted(srcs)[:6]}")
    for stem, nums, years, members, iss in notes:
        span = f"{int(iss[0])}..{int(iss[-1])}" if iss else "no issue numbers"
        if verbose:
            print(f"  ! folder names runs v{'+v'.join(nums)} at once ({', '.join(years)}); issues present {span}"
                  f" — {len(members)} files LEFT for a decision")
    if verbose and offkey:
        print(f"  guard held {sum(offkey.values())} file(s) whose own key is a different work: "
              + ", ".join(f"{k!r}x{v}" for k, v in offkey.most_common(5)))
    if verbose and skipped:
        tops = collections.Counter(t for _, t, _ in skipped)
        print(f"  {len(skipped)} file(s) under no run folder: " + ", ".join(f"{t!r}" for t, _ in tops.most_common(4)))
    return proposal, notes


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    out_path, assign = None, {}
    for a in sys.argv[1:]:
        if a.startswith("--write"):
            out_path = a.split("=", 1)[1] if "=" in a else "fold.jsonl"
        elif a.startswith("--assign="):
            folder, key = a.split("=", 1)[1].split("|", 1)
            assign[folder] = key
    con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)

    if "--all" in sys.argv:
        folders = collections.Counter()
        for (p,) in con.execute("SELECT Path FROM Item WHERE Path LIKE '%5 - Comics%' AND coalesce(IsExcluded,0)=0"):
            parts = p.split("5 - Comics" + SEP, 1)[-1].split(SEP)
            if len(parts) >= 3:
                folders[parts[1]] += 1
        todo = []
        for name in sorted(folders):
            runs = set()
            for (p,) in con.execute("SELECT DISTINCT Path FROM Item WHERE Path LIKE ? AND coalesce(IsExcluded,0)=0",
                                    (f"%{SEP}{name}{SEP}%",)):
                parts = p.split(SEP + name + SEP, 1)
                if len(parts) < 2:
                    continue
                top = parts[1].split(SEP)[0]
                if run_of(top):
                    runs.add(run_of(top)[0] + "|" + run_of(top)[1][0])
            if len(runs) > 1:
                todo.append((name, len(runs), folders[name]))
        todo.sort(key=lambda t: -t[2])
        print(f"{len(todo)} title folders hold more than one run folder\n")
        for name, nruns, nfiles in todo[:40]:
            print(f"  {nfiles:>5} files  {nruns:>2} runs   {name}")
        return

    total = []
    for name in args:
        print(f"== {name}")
        p, _ = fold(name, con, assign=assign)
        total += p
    print(f"\n{len(total)} decision lines" + (f" -> {out_path}" if out_path else " (not written)"))
    if out_path:
        with open(out_path, "w", encoding="utf-8") as fh:
            for d in total:
                fh.write(json.dumps(d, ensure_ascii=False) + "\n")


main()
