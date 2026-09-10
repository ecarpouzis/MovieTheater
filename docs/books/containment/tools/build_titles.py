"""Populate SeriesTitle and point every Series at its title, then repair Franchise through that tier.

WHERE THE KEY COMES FROM, and why it is not a guess:
  exact     `books-series-split` writes the ParsedSeriesKey `<Title> v<N> (<Year>)`, so the title is the
            stem before the volume marker — the split itself said what the title is.
  inferred  for a Series nobody split, the title is its name with a trailing `(YYYY)` removed. That is a
            convention of this library's naming, not an inference about the comic.

A title is only created when it groups MORE THAN ONE Series. A lone run is not a title worth a row: it
would double the table for nothing and imply a grouping that was never observed.

FRANCHISE REPAIR. My splits created run-Series without carrying the originating shelf's franchise across,
so runs sit beside siblings that still have one. The title tier makes the repair evidence-based: a run
adopts the franchise its OWN title carries, and only when every run of that title that HAS one agrees.
A title whose runs disagree is left alone and reported.

Dry-run by default. `python build_titles.py [--apply]`
"""
import collections
import re
import sqlite3
import sys
from datetime import datetime, timezone

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
APPLY = "--apply" in sys.argv

RX_RUN = re.compile(r"^(?P<title>.+?)\s+v(?:ol(?:ume)?)?\.?\s*\d{1,3}(?:\.\d+)?\s*\((?:19|20)\d{2}\)\s*$", re.I)
RX_YEAR = re.compile(r"\s*\((?:19|20)\d{2}\)\s*$")

con = sqlite3.connect(HOT)
rows = con.execute("""
    SELECT s.Id, coalesce(s.DisplayNameOverride, s.Name), s.Franchise, s.ParsedKey, s.PublisherId,
           s.YearStart, s.YearEnd,
           (SELECT count(*) FROM Item i WHERE i.SeriesId = s.Id AND coalesce(i.IsExcluded,0)=0)
    FROM Series s WHERE s.CanonicalKey NOT LIKE 'book:%'""").fetchall()


def title_of(name, parsed):
    for cand in (parsed or "", name or ""):
        m = RX_RUN.match(cand.strip())
        if m:
            return m.group("title").strip(), True
    return RX_YEAR.sub("", (name or "").strip()).strip(), False


groups = collections.defaultdict(list)
exact_keys = set()
for sid, name, fr, parsed, pub, y0, y1, nfiles in rows:
    t, is_exact = title_of(name, parsed)
    if not t:
        continue
    groups[t].append((sid, name, fr, pub, y0, y1, nfiles))
    if is_exact:
        exact_keys.add(t)

multi = {t: g for t, g in groups.items() if len(g) > 1}
print(f"comic Series                : {len(rows):>6}")
print(f"titles grouping 2+ runs     : {len(multi):>6}   covering {sum(len(g) for g in multi.values())} Series, "
      f"{sum(n for g in multi.values() for *_, n in g)} files")
print(f"  of those, keyed EXACTLY by a split : {len(exact_keys & set(multi)):>4}")

# ── franchise repair, through the tier ────────────────────────────────────────────────────────────
fixed, conflict = [], []
for t, g in multi.items():
    have = {f for *_, f, _, _, _, _ in [(x[0], x[1], x[2], x[3], x[4], x[5], x[6]) for x in g] if f} \
        if False else {x[2] for x in g if x[2]}
    if len(have) > 1:
        conflict.append((t, sorted(have)))
        continue
    if len(have) == 1:
        fr = next(iter(have))
        for sid, name, cur, *_ in g:
            if not cur:
                fixed.append((sid, fr, t, name))

print(f"\nSeries inside a title that can ADOPT its franchise : {len(fixed)}")
print(f"titles whose runs disagree (left alone)            : {len(conflict)}")
for t, opts in conflict[:6]:
    print(f"   {t[:40]:<40} {opts}")
for sid, fr, t, name in fixed[:8]:
    print(f"   S{sid:<7} <- {fr!r:<20} (title {t!r})")

if not APPLY:
    print("\n(dry run - re-run with --apply)")
    raise SystemExit

next_id = (con.execute("SELECT coalesce(max(Id),0) FROM SeriesTitle").fetchone()[0] or 0)
now = datetime.now(timezone.utc).isoformat()
made = linked = 0
for t, g in sorted(multi.items()):
    existing = con.execute("SELECT Id FROM SeriesTitle WHERE Key = ?", (t,)).fetchone()
    if existing:
        tid = existing[0]
    else:
        next_id += 1
        tid = next_id
        frs = collections.Counter(x[2] for x in g if x[2])
        pubs = collections.Counter(x[3] for x in g if x[3])
        years = [x[4] for x in g if x[4]] + [x[5] for x in g if x[5]]
        con.execute("""INSERT INTO SeriesTitle (Id, Key, Name, Franchise, PublisherId, YearStart, YearEnd,
                       RunCount, Note, CreatedAt) VALUES (?,?,?,?,?,?,?,?,?,?)""",
                    (tid, t, t, frs.most_common(1)[0][0] if frs else None,
                     pubs.most_common(1)[0][0] if pubs else None,
                     min(years) if years else None, max(years) if years else None,
                     len(g), "exact" if t in exact_keys else "inferred from the series name", now))
        made += 1
    for sid, *_ in g:
        con.execute("UPDATE Series SET TitleId = ? WHERE Id = ?", (tid, sid))
        linked += 1
    con.execute("UPDATE SeriesTitle SET RunCount = ? WHERE Id = ?", (len(g), tid))

con.executemany("UPDATE Series SET Franchise = ? WHERE Id = ?", [(fr, sid) for sid, fr, _, _ in fixed])
con.commit()
print(f"\napplied: {made} titles created, {linked} Series linked, {len(fixed)} franchises repaired")
con.close()
