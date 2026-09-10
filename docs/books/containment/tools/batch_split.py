"""Propose splits for every SPLIT-routed shelf in a half, in one pass, with a summary to eyeball.

A split asserts nothing about contents — it only stops two runs sharing one number space, and it is
reversible (books-series-split writes every previous key to an undo CSV). That is why it can be batched
where a RANGE cannot: over-claiming a range loses files, whereas splitting wrongly only separates two
things that could be rejoined.

Still, it is proposed and printed, never auto-applied: this writes a mapping file and a summary, and
`books-series-split` remains a separate, dry-run-by-default step.

`python batch_split.py --half=a [--write out.jsonl] [--min-runs 2]`
"""
import collections
import json
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
SEP = chr(92)
RX_VOL = re.compile(r"\bv(?:ol(?:ume)?)?\.?\s*\d{1,3}\b", re.I)
RX_YEAR = re.compile(r"\((?:19|20)\d{2}\)")
RX_LEAD = re.compile(r"^\s*[#_]*\d{1,3}[\s._-]+")
RX_NUM = re.compile(r"^\d+(\.\d+)?$")

half, out_path, min_runs = "a", None, 2
for a in sys.argv[1:]:
    if a.startswith("--half"):
        half = a.split("=", 1)[1]
    elif a.startswith("--write"):
        out_path = a.split("=", 1)[1] if "=" in a else "batch_split.jsonl"
    elif a.startswith("--min-runs"):
        min_runs = int(a.split("=", 1)[1])

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""
 SELECT i.SeriesId, sum(cd.IsCollection), sum(1-cd.IsCollection),
        sum(CASE WHEN n.TrackRole=1 AND n.SpanSource IN (2,3,4) THEN 1 ELSE 0 END),
        coalesce(x.DisplayNameOverride, x.Name)
 FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
 LEFT JOIN CollectedEditionSpan s ON s.ItemId=i.Id AND s.Source=3
 LEFT JOIN CollectionNode n ON n.ItemId=i.Id
 LEFT JOIN Series x ON x.Id=i.SeriesId
 WHERE coalesce(i.IsExcluded,0)=0 AND i.SeriesId IS NOT NULL
 GROUP BY i.SeriesId HAVING sum(cd.IsCollection)>0 AND sum(1-cd.IsCollection)>0
   AND sum(CASE WHEN s.Source=3 AND s.IssueStart IS NOT NULL THEN 1 ELSE 0 END)=0
   AND sum(CASE WHEN n.TrackRole=1 AND n.SpanSource IN (2,3,4) THEN 1 ELSE 0 END)>0""").fetchall()
rows.sort(key=lambda r: -(r[3] * 3 + r[1]))
rows = [r for n, r in enumerate(rows) if (n % 2 == 0) == (half == "a")]


RX_VOLNUM = re.compile(r"\bv(?:ol(?:ume)?)?\.?\s*(\d{1,3})(?:\.\d+)?\b", re.I)
RX_SUBDIV = re.compile(r"\bv(?:ol(?:ume)?)?\.?\s*\d{1,3}\.\d+\b", re.I)


def run_folder(path):
    """The deepest folder naming a run, plus the volume NUMBERS it names.

    The number is what identifies a run, not the folder's whole text. `Batman '66 v1 (Digital First)`,
    `v1 (Static Image Version)` and `v1 (Traditional)` are three RIPS of one run and must not become three
    Series; they all say v1. And a folder naming TWO numbers — `Nightwing v1 (1995) + v2 (1996)` — is a
    combined folder that cannot be assigned to either run, so its shelf is left for a person."""
    parts = [p for p in (path or "").replace("/", SEP).split(SEP) if p]
    found = None
    for part in parts[:-1]:
        if RX_VOL.search(part) and RX_YEAR.search(part):
            found = part
    if not found:
        return None, None
    nums = RX_VOLNUM.findall(found)
    years = RX_YEAR.findall(found)
    # `v4.1 (2021)` and `v4.2 (2024)` are OUR OWN subdivisions of one run — the suffix marks a creative-team
    # era, and the issues run straight through it (Harley Quinn #1-37 then #38-47; Detective Comics v1.1
    # through v1.4 are one #934-1082 numbering). RX_VOLNUM already drops the `.M`, so the only thing that
    # can cut such a run apart is the year, and here it must not: a subdivided folder keys on the number
    # alone. A plain `v1 (1993)` vs `v1 (2018)` keeps the year, which is what tells Sonic's two runs apart.
    if RX_SUBDIV.search(found):
        return RX_LEAD.sub("", found).strip(), tuple((n, "") for n in sorted(set(nums)))
    # A run is identified by its NUMBER **and its year**. Batman '66 v1 (2013) appears three times as
    # three rip formats and is one run; Sonic the Hedgehog v1 (1993) (Archie) and v1 (2018) (IDW) share a
    # number and are two. Merging on the number alone would fuse them.
    return RX_LEAD.sub("", found).strip(), tuple(sorted({(n, y) for n in set(nums) for y in set(years)}))


proposal, summary, skipped = [], [], []
for sid, cols, iss, prov, name in rows:
    items = con.execute("""SELECT i.Id, i.Path, i.FileName, cd.ParsedSeriesKey
        FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
        WHERE i.SeriesId=? AND coalesce(i.IsExcluded,0)=0""", (sid,)).fetchall()
    dominant = collections.Counter(r[3] or "" for r in items).most_common(1)[0][0]
    groups, left, ambiguous = collections.defaultdict(list), 0, False
    label_for = {}
    for iid, path, fn, key in items:
        if (key or "") != dominant:
            left += 1
            continue
        label, nums = run_folder(path)
        if nums and len({n for n, y in nums}) > 1:
            ambiguous = True
            break
        # key the group by the RUN NUMBER, so several rips of one run collapse into it
        gk = nums[0] if nums else None
        if gk and gk not in label_for:
            label_for[gk] = label
        groups[gk].append(iid)
    if ambiguous:
        skipped.append((sid, name, "a folder names two runs at once"))
        continue
    named = {label_for[k]: v for k, v in groups.items() if k}
    if len(named) < min_runs:
        skipped.append((sid, name, f"only {len(named)} run folder(s) once rips of the same run are merged"))
        continue
    for k, ids in named.items():
        for i in ids:
            proposal.append({"itemId": i, "key": k})
    summary.append((sid, name, len(named), sum(len(v) for v in named.values()),
                    len(groups.get(None, [])), left, sorted(named, key=lambda k: -len(named[k]))))

print(f"half {half}: {len(summary)} shelves would split into {sum(s[2] for s in summary)} runs; "
      f"{len(proposal)} files re-keyed; "
      f"{sum(s[4] for s in summary)} files under no run folder and {sum(s[5] for s in summary)} with their "
      f"own key are LEFT ALONE\n")
for sid, name, nruns, nfiles, nofolder, left, keys in summary:
    print(f"  S{sid:<8} {nruns} runs, {nfiles:>4} files  ({nofolder} unfoldered, {left} own-key)  {(name or '?')[:34]}")
    print(f"            {' | '.join(keys[:4])[:104]}")

if skipped:
    print("")
    print(f"{len(skipped)} shelves NOT proposed:")
    for sid, name, why in skipped[:14]:
        print(f"  S{sid:<8} {why:<52} {(name or '?')[:32]}")
    if len(skipped) > 14:
        print(f"  ... and {len(skipped)-14} more")

if out_path:
    with open(out_path, "w", encoding="utf-8") as fh:
        for p in proposal:
            fh.write(json.dumps(p, ensure_ascii=False) + "\n")
    print(f"\n{len(proposal)} decision lines -> {out_path}")
