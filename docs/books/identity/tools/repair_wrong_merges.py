"""Undo the merges wave 2 made backwards: the refused shelf swallowed the run that had earned the volume.

    python repair_wrong_merges.py [--backup data/books/v2/backup-20260910-100107]      dry run
    python repair_wrong_merges.py --backup <dir> --apply

**The defect, exactly.** A reader wrote `S 95391 cv=3194` (the 1983 Outsiders run) and, on the shelf whose
STORED link was 3194, `R 2156` + `F 2156 wrong-cv-link` (the 2019 trades). The applier wrote nothing at all
for an `R`, so S2156's parsed key still carried cv:3194. At the resolve both keys resolved to the group
`cv:3194`, and the survivor rule prefers the row that ALREADY holds the key —

    grp.OrderByDescending(s => s.CanonicalKey == key)          // SeriesResolver.cs, step 4
       .ThenByDescending(s => CountFor(s.ParsedKey))
       .ThenBy(s => s.Id)

— which is S2156. The run was fused INTO the shelf that had been refused. Ten times.

**The repair is two writes, not one.** Clearing S2156's Cv link is necessary: it moves S2156 into its own
`parsed:` group so the two stop colliding. It is NOT sufficient, and this is the part worth reading twice:

    `books-resolve --series` NEVER CREATES A `Series` ROW.

`SeriesRebuildJob.Identity` only UPDATEs rows that already exist (`hot.Update("Series", "Id", id, …)`), and
`SeriesResolver.Compute` forms its groups from `series.GroupBy(s => CanonicalKeyFor(s.ParsedKey))` — one
group per EXISTING row's own ParsedKey. The merged-away run's row was deleted by the wave, so after a bare
clear its parsed key belongs to no group at all: step 4b finds no survivor holding `cv:3194` and no survivor
normalising to the same key, the key gets no `SeriesAlias` entry, and the re-point

    UPDATE Item SET SeriesId = (SELECT a.SeriesId FROM SeriesAlias a JOIN ComicDetail cd … )

writes **NULL** — the run's files come off the shelf and land nowhere. `books-series-split` is the only verb
that seeds a row for an orphaned key, and it does it by hand (`SeriesSplitCommand`, `nextId = max(Id)+1`,
CanonicalKey `parsed:<norm>`), which is the shape copied below.

So per pair: clear the survivor's Cv link, and seed a `Series` row for the run's parsed key. At the next
resolve the run's key is the only member of the `cv:<vol>` group, so the seeded row survives, takes the
canonical key, the CvVolume name and the volume id; the survivor keeps its own id and its own files under a
`parsed:` key. The run comes back with a NEW id, and every decision that named the old id is answered by
`SeriesMerge`, which the checker and the auditor already read as "it took effect".
"""
import json
import os
import re
import sqlite3
import sys
import time

import idbase


def norm_key(k):
    """`SeriesResolver.NormalizeKey`, character for character: lowercase, drop a leading "the ", every
    non-alphanumeric becomes a space, collapse. A seeded CanonicalKey that does not agree with what the
    resolver will compute either trips the UNIQUE index or lands in the wrong group."""
    s = (k or "").strip().lower()
    if s.startswith("the "):
        s = s[4:]
    return " ".join("".join(c if c.isalnum() else " " for c in s).split())

APPLY = "--apply" in sys.argv
opt = {}
for k, a in enumerate(sys.argv):
    if a.startswith("--") and k + 1 < len(sys.argv) and not sys.argv[k + 1].startswith("--"):
        opt[a[2:]] = sys.argv[k + 1]
BACKUP = opt.get("backup") or r"F:/Work/MovieTheater/data/books/v2/backup-20260910-100107"
bpath = os.path.join(BACKUP, "books.db") if os.path.isdir(BACKUP) else BACKUP
if not os.path.isfile(bpath):
    raise SystemExit(f"no pre-wave books.db at {bpath}")

live = idbase.open_hot()
old = sqlite3.connect(f"file:{bpath}?mode=ro", uri=True)

decides, winner, _sup, _dup = idbase.scan_decisions()

# the shelves a reader REFUSED while calling their stored link wrong — the ones that must not have survived
refused_wrong, s_volume = {}, {}
for path, rec in decides.items():
    base = os.path.splitext(os.path.basename(path))[0]
    wrong = {sid for sid, fls in rec.get("flags_full", {}).items()
             for fl in fls if fl.split("=")[0] == "wrong-cv-link"}
    for sid in rec["sids"]:
        if winner.get(sid) != path:
            continue
        kind, cvv = rec["kinds"].get(sid), rec["cv"].get(sid)
        # The Cosplayers pair (S4251 -> S34939) is this same defect with `F split-needed` instead of
        # `F wrong-cv-link`: the refusal text says the stored link is wrong, the flag chosen does not. So
        # the criterion is the one that actually predicts the fusion — a survivor the pass REFUSED, however
        # it was flagged — plus a wrong-cv-link that named no replacement volume.
        if kind == "R" or (sid in wrong and kind == "S" and cvv is None) or (sid in wrong and kind is None):
            refused_wrong[sid] = base
        if kind == "S" and cvv is not None:
            s_volume[sid] = (cvv, base)

live_series = {r[0] for r in live.execute("SELECT Id FROM Series")}
old_series = {r[0]: (r[1], r[2]) for r in old.execute("SELECT Id, ParsedKey, CanonicalKey FROM Series")}
merged = {r[0]: r[1] for r in live.execute("SELECT OldSeriesId, NewSeriesId FROM SeriesMerge")}

# THE CRITERION: a shelf that existed before the wave, is gone now, and whose survivor is a shelf the pass
# REFUSED. A refused shelf must never be the destination of a merge — that is the whole defect in one line.
pairs = []
for oldid, newid in sorted(merged.items()):
    if oldid in live_series or oldid not in old_series or newid not in refused_wrong:
        continue
    key, _canon = old_series[oldid]
    vol, batch = s_volume.get(oldid, (None, None))
    n_items = live.execute("""SELECT count(*) FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
                              WHERE cd.ParsedSeriesKey = ? AND i.Kind = 0
                                AND coalesce(i.IsExcluded,0) = 0""", (key,)).fetchone()[0]
    pairs.append({"old": oldid, "survivor": newid, "key": key, "cv": vol,
                  "batch": batch or refused_wrong[newid], "files": n_items,
                  "survivorName": (live.execute("SELECT coalesce(DisplayNameOverride,Name) FROM Series WHERE Id=?",
                                                (newid,)).fetchone() or ["?"])[0]})

print(f"{len(pairs)} wrong merge(s) — a shelf the pass REFUSED became the survivor of the run that earned "
      f"the volume\n")
for p in pairs:
    print(f"   S{p['old']:<7} -> S{p['survivor']:<7} {p['survivorName'][:38]:<38} "
          f"cv {p['cv'] or '?':<8} key {p['key']!r} ({p['files']} file(s))")

if not pairs:
    raise SystemExit(0)

# ── the two writes ───────────────────────────────────────────────────────────────────────────────
STAMP = time.strftime("%Y%m%d-%H%M%S")
existing = {(r[0], r[1]): dict(zip(("ParsedKey", "Provider", "ProviderKey", "Status", "Score"), r))
            for r in live.execute("SELECT ParsedKey, Provider, ProviderKey, Status, Score FROM SeriesKeyLink")}
alias_of = {}
for sid, k in live.execute("SELECT SeriesId, ParsedKey FROM SeriesAlias"):
    alias_of.setdefault(sid, set()).add(k)
for sid, k in live.execute("SELECT Id, ParsedKey FROM Series WHERE ParsedKey IS NOT NULL"):
    alias_of.setdefault(sid, set()).add(k)
canon_taken = {r[0] for r in live.execute("SELECT CanonicalKey FROM Series")}
next_id = live.execute("SELECT coalesce(max(Id),0) FROM Series").fetchone()[0]

plan, undo, warn = [], [], []
print("\nwrites:")
for p in pairs:
    surv, runkey = p["survivor"], p["key"]
    # 1. clear every Cv link on the SURVIVOR's own keys — but never the run's key, which carries the
    #    identity the reader earned
    for k in sorted(alias_of.get(surv, ()) - {runkey}):
        was = existing.get((k, idbase.P_CV))
        if not was or was["ProviderKey"] is None:
            continue
        rec = {"table": "SeriesKeyLink", "key": [k, idbase.P_CV], "was": was}
        undo.append(rec)
        plan.append(("""UPDATE SeriesKeyLink SET ProviderKey = NULL, Status = 6, Score = NULL, AttemptedAt = ?
                        WHERE ParsedKey = ? AND Provider = 0""", (STAMP, k)))
        print(f"   clear  S{surv:<7} key {k!r} cv {was['ProviderKey']} (status {was['Status']}) -> NULL/Cleared")
    # 2. seed the run's row back, or the resolve orphans its files (see the module docstring)
    canonical = "parsed:" + norm_key(runkey)
    if canonical in canon_taken:
        warn.append(f"S{p['old']}: canonical {canonical!r} is already taken — seeding would trip the UNIQUE "
                    f"index; this pair needs a hand-picked key")
        continue
    canon_taken.add(canonical)
    next_id += 1
    p["newId"] = next_id
    src = old.execute("SELECT Franchise, TitleId, YearStart, YearEnd FROM Series WHERE Id=?", (p["old"],)).fetchone()
    plan.append(("""INSERT INTO Series (Id, ParsedKey, CanonicalKey, Name, Franchise, TitleId, YearStart,
                                        YearEnd, IssueCount, IsOngoing)
                    VALUES (?,?,?,?,?,?,?,?,0,0)""",
                 (next_id, runkey, canonical, runkey, src[0] if src else None, src[1] if src else None,
                  src[2] if src else None, src[3] if src else None)))
    undo.append({"table": "Series", "key": [next_id], "was": None, "note": "seeded by repair_wrong_merges"})
    print(f"   seed   S{next_id:<7} ParsedKey {runkey!r} canonical {canonical!r} "
          f"(franchise {src[0] if src else None!r}, title {src[1] if src else None}) "
          f"— the resolve will move it to cv:{p['cv']} and rename it from CvVolume")

# a key that normalises onto the survivor's own key would just re-merge under `parsed:` — check, don't hope
for p in pairs:
    if "newId" not in p:
        continue
    sk = (live.execute("SELECT ParsedKey FROM Series WHERE Id=?", (p["survivor"],)).fetchone() or [""])[0]
    if sk and re.sub(r"[^a-z0-9]+", " ", sk.lower()).strip() == \
            re.sub(r"[^a-z0-9]+", " ", (p["key"] or "").lower()).strip():
        warn.append(f"S{p['old']}: the run's key and the survivor's key NORMALISE THE SAME — after the clear "
                    f"they would re-merge under one `parsed:` group; this pair needs a DisplayNameOverride "
                    f"or a different key, not a clear")

print(f"\n{len(plan)} statement(s): "
      f"{sum(1 for s, _ in plan if s.lstrip().startswith('UPDATE'))} clear, "
      f"{sum(1 for s, _ in plan if s.lstrip().startswith('INSERT'))} seed")
for w in warn:
    print(f"   ⚠ {w}")

if not APPLY:
    print("\n(dry run — nothing written. Re-run with --apply, then books-resolve --series.)")
    raise SystemExit(0)

os.makedirs(idbase.UNDO, exist_ok=True)
up = os.path.join(idbase.UNDO, f"repair-wrong-merges-{STAMP}.jsonl")
with open(up, "w", encoding="utf-8") as f:
    for rec in undo:
        f.write(json.dumps(rec, ensure_ascii=False) + "\n")
w = sqlite3.connect(idbase.HOT)
for sql, params in plan:
    w.execute(sql, params)
w.commit()
w.close()
print(f"\napplied {len(plan)} statement(s); undo -> {up}")
print("NOW: books-resolve --series, then reseat_flags.py --apply, then merge_refusals.py, then the chain.")
