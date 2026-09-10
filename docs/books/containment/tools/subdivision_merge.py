"""Find runs my split CUT IN TWO, and prove it by the issue numbers.

`Harley Quinn v4.1 (2021)` and `v4.2 (2024)` are our own subdivisions of ONE publishing run: the folder
suffix `.1`/`.2` marks a creative-team era, not a relaunch, and the issues run straight through (#1-37 then
#38-47). batch_split keys a run by (volume number, year) — the year guard that correctly keeps
`Sonic v1 (1993)` apart from `Sonic v1 (2018)` — and that guard cuts these apart.

The claim is not taken from the naming. It is MEASURED: two shelves are one run only when their issue
numbers are DISJOINT (a relaunch restarts at 1 and would overlap). Overlap => genuinely two runs, left
alone and reported.

Read-only. `python subdivision_merge.py [--write out.jsonl]`
"""
import collections
import re
import sqlite3
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
RX_SUB = re.compile(r"^(?P<title>.+?)\s+v(?P<vol>\d{1,3})\.(?P<sub>\d+)\s*(?:\([^)]*\)\s*)*$", re.I)

out_path = None
for a in sys.argv[1:]:
    if a.startswith("--write"):
        out_path = a.split("=", 1)[1] if "=" in a else "subdivision_merge.jsonl"

con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)
rows = con.execute("""
    SELECT s.Id, coalesce(s.DisplayNameOverride, s.Name), s.ParsedKey
    FROM Series s WHERE s.CanonicalKey NOT LIKE 'book:%'""").fetchall()

groups = collections.defaultdict(list)
for sid, name, parsed in rows:
    for cand in (parsed or "", name or ""):
        m = RX_SUB.match(cand.strip())
        if m:
            groups[(m.group("title").strip().lower(), m.group("vol"))].append((sid, cand.strip()))
            break

merge, kept = [], []
for (title, vol), members in sorted(groups.items()):
    if len(members) < 2:
        continue
    nums, spans = {}, {}
    for sid, key in members:
        got = con.execute("""SELECT cd.IssueNo FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
            WHERE i.SeriesId=? AND coalesce(i.IsExcluded,0)=0 AND cd.IsCollection=0
              AND cd.IssueNo IS NOT NULL""", (sid,)).fetchall()
        nums[sid] = {r[0] for r in got}
        spans[sid] = (min(nums[sid]), max(nums[sid])) if nums[sid] else None
    overlap = False
    seen = set()
    for sid in nums:
        if nums[sid] & seen:
            overlap = True
        seen |= nums[sid]
    blind = [s for s in nums if not nums[s]]
    line = f"  {members[0][1][:44]:<44} " + "  ".join(
        f"S{sid}[{'-'.join(str(int(x)) for x in spans[sid]) if spans[sid] else 'no issue numbers'}]"
        for sid, _ in members)
    if overlap:
        kept.append(line + "   OVERLAP -> two real runs")
    elif blind and len(blind) == len(nums):
        kept.append(line + "   no numbers on either side -> cannot prove")
    else:
        merge.append((members, line))

print(f"{len(groups)} subdivision groups; {len(merge)} are ONE run cut in two; {len(kept)} left alone\n")
for _, line in merge:
    print(line)
if kept:
    print("\nleft alone:")
    for line in kept:
        print(line)

if out_path and merge:
    import json
    n = 0
    with open(out_path, "w", encoding="utf-8") as fh:
        for members, _ in merge:
            # the merged run drops the subdivision entirely: `Detective Comics v1.1 (2016)` +
            # `v1.2 (2019)` + ... is ONE run, and its key is `Detective Comics v1 (2016)` — the earliest
            # year the subdivisions carry, so title_of() groups it with its siblings.
            stem = RX_SUB.match(members[0][1]).group("title").strip()
            vol = RX_SUB.match(members[0][1]).group("vol")
            years = sorted(y for _, k in members for y in re.findall(r"\((19|20)\d{2}\)", k))
            yrs = sorted(m.group(0) for _, k in members for m in re.finditer(r"\((?:19|20)\d{2}\)", k))
            key = f"{stem} v{vol} {yrs[0]}" if yrs else f"{stem} v{vol}"
            for sid, _ in members:
                for (iid,) in con.execute(
                        "SELECT Id FROM Item WHERE SeriesId=? AND coalesce(IsExcluded,0)=0", (sid,)):
                    fh.write(json.dumps({"itemId": iid, "key": key}) + "\n")
                    n += 1
    print(f"\n{n} decision lines -> {out_path}")
