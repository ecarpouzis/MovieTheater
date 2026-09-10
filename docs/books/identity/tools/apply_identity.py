"""Store the identities that were READ. Derives nothing. Dry run by default.

`python apply_identity.py <decisions/A-001.txt> [more...] [--apply]`

The ancestor is `apply_read_issue_numbers.py`, and the reason is the same one written at the top of it:
every previous attempt to infer this class of fact with a rule over the whole population produced a rule
that was right about most rows and quietly wrong about the rest. So there is no inference in this file.
It takes the ids off the lines, prints the shelf's name beside each write so the write can be checked
against what was read, and refuses any batch `check_identity.py` does not pass.

What it writes, per PLAN §7 Phase 0:

  S, confidence >= 0.9  SeriesKeyLink(ParsedKey, Provider=0 Cv, ProviderKey=<volume>, Status=5 Manual,
                        Score=conf*100) for EVERY parsed key aliased to the shelf — the resolver is keyed
                        on ParsedKey, not on Series.Id, so a key left behind keeps its old canonical group
                        (§6.4). `gcd=` writes the same row shape at Provider=3, which `SeriesResolver`
                        never reads (SeriesResolver.cs reads Provider 0 and 1 only), so it stores the GCD
                        link without a migration and without moving a shelf.
  S, confidence 0.7     nothing linked; SeriesMatchReview(Scope='series', State='review') only.
  every line            one SeriesInferenceDecision(Class='identity') carrying the evidence sentence and an
                        UndoJson of the rows as they were, so a wave can be walked back row by row.
  I                     ItemProviderLink(Status=5 Manual, Method='identity-read', Confidence).

⚠ Linking two shelves to one CV volume MERGES them at the next `books-resolve --series` (§6.5) and linking
renames the shelf (§6.6). Both are intended; both are why the checker runs first and why the wave recipe
(`wave_land.ps1`) backs up before this runs.
"""
import json
import os
import re
import sqlite3
import sys
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass
import time
from collections import Counter

import idbase
import check_identity

APPLY = "--apply" in sys.argv
args = [a for a in sys.argv[1:] if not a.startswith("--")]
if not args:
    raise SystemExit("usage: apply_identity.py <batch name or path> [...] [--apply]")
files = [idbase.resolve_decision_file(a) for a in args]

ck = check_identity.Checker()
ro = ck.con

# shelf -> every parsed key aliased to it, and the shelf's name, read once
keys, names, primary = {}, {}, {}
for sid, k in ro.execute("SELECT SeriesId, ParsedKey FROM SeriesAlias WHERE ParsedKey IS NOT NULL"):
    keys.setdefault(sid, set()).add(k)
for sid, k, nm in ro.execute("SELECT Id, ParsedKey, coalesce(DisplayNameOverride, Name) FROM Series"):
    names[sid] = nm
    if k:
        keys.setdefault(sid, set()).add(k)
        primary[sid] = k
for sid in keys:
    primary.setdefault(sid, sorted(keys[sid])[0])

existing = {(r[0], r[1]): dict(zip(("ParsedKey", "Provider", "ProviderKey", "Status", "Score"), r))
            for r in ro.execute("SELECT ParsedKey, Provider, ProviderKey, Status, Score FROM SeriesKeyLink")}
item_links = {(r[0], r[1]): dict(zip(("ItemId", "Provider", "ProviderKey", "SecondaryKey", "Status", "Method"), r))
              for r in ro.execute("SELECT ItemId, Provider, ProviderKey, SecondaryKey, Status, Method "
                                  "FROM ItemProviderLink WHERE Provider IN (0,3)")}

# Precedence, decided once in idbase and printed beside every write: a shelf re-read after a sharpened
# ruling is decided in an R- file, and THAT line is the one stored. The original file is never edited —
# it is the audit trail of what was believed when it was written — so without this the applier would
# store both and the last one to be iterated would win by accident.
_decides, WINNER, SUPERSEDED, DUPES = idbase.scan_decisions(files)
if DUPES:
    for sid, fs in sorted(DUPES.items()):
        print(f"REFUSED: S{sid} is decided in two non-revisit files: {[os.path.basename(f) for f in fs]}")
    raise SystemExit("a shelf decided twice outside a revisit is ambiguous; nothing applied")
item_shelf = {r[0]: r[1] for r in ro.execute(
    "SELECT Id, SeriesId FROM Item WHERE Kind = 0 AND coalesce(IsExcluded,0) = 0 AND SeriesId IS NOT NULL")}

# An issue id implies its series/volume, and SecondaryKey is where every shelf-level rollup in this pass
# reads it from, so the applier resolves it rather than leaving the column NULL. Both lookups are
# local-first: our own cache, then the legs file, then the whole-provider dumps.
cv_issue_volume = {r[0]: str(r[1]) for r in ro.execute(
    "SELECT Id, VolumeId FROM CvIssue WHERE VolumeId IS NOT NULL")}
gcd_issue_series = {r[0]: str(r[1]) for r in ro.execute(
    "SELECT GcdIssueId, GcdSeriesId FROM legs.GcdIssue WHERE GcdSeriesId IS NOT NULL")}


def _fill_from_dumps(wanted_cv, wanted_gcd):
    if wanted_cv:
        ref = idbase.open_cv_ref()
        if ref is not None:
            for chunk in [list(wanted_cv)[i:i + 900] for i in range(0, len(wanted_cv), 900)]:
                q = ",".join("?" * len(chunk))
                for i, v in ref.execute(f"SELECT issueId, volId FROM cv_iss WHERE issueId IN ({q})", chunk):
                    if v is not None:
                        cv_issue_volume[i] = str(v)
    if wanted_gcd:
        dump = idbase.open_gcd_dump()
        if dump is not None:
            for chunk in [list(wanted_gcd)[i:i + 900] for i in range(0, len(wanted_gcd), 900)]:
                q = ",".join("?" * len(chunk))
                for i, s in dump.execute(f"SELECT id, series_id FROM gcd_issue WHERE id IN ({q})", chunk):
                    if s is not None:
                        gcd_issue_series[i] = str(s)

# one cheap sweep so the SecondaryKey lookups above are complete before the first line prints
_want_cv, _want_gcd = set(), set()
for _p in files:
    for _raw in open(_p, encoding="utf-8"):
        _l = _raw.strip()
        if not _l.startswith("I "):
            continue
        for _m in re.finditer(r"\b(cv|gcd)=(\d+)\b", _l.split("|", 1)[0]):
            (_want_cv if _m.group(1) == "cv" else _want_gcd).add(int(_m.group(2)))
_fill_from_dumps(_want_cv - set(cv_issue_volume), _want_gcd - set(gcd_issue_series))

# Rule 10 of the checker stops complaining about a stored-CvVolumeId collision when the partner shelf is
# refused or re-linked — and that is only SOUND if the partner's stored link is actually cleared. Wave 2
# proved the gap: `R 34939` + `F split-needed` (Cosplayers) left cv 72946 on the refused shelf, and the
# resolve fused S4251 into it. So the applier clears a refused shelf's Cv link whenever THIS wave's accepted
# lines claim that volume for a different shelf, whatever flag the refusal happened to carry.
stored_cv = {sid: vol for vol, sid in ()}
stored_cv = {r[0]: r[1] for r in ro.execute(
    f"SELECT Id, CvVolumeId FROM Series WHERE CvVolumeId IS NOT NULL AND Id IN ({idbase.SHELF_SQL})")}
claimed_by = {}
for _p, _rec in _decides.items():
    for _sid, _vol in _rec["cv"].items():
        if WINNER.get(_sid) == _p and _rec["kinds"].get(_sid) == "S":
            claimed_by.setdefault(_vol, set()).add(_sid)

STAMP = time.strftime("%Y%m%d-%H%M%S")
totals = {"keylink": 0, "keylink_cv": 0, "keylink_gcd": 0, "decision": 0, "review": 0, "itemlink": 0,
          "refused": 0, "itemlink_manual_overwrite": 0, "cleared": 0}
keylink_overwrite = Counter()     # previous Status of every SeriesKeyLink row this pass would replace
uninterpretable = []              # lines the applier cannot turn into a write — must be zero before landing
linked_cv = {}                    # cv volume id -> [sid, ...], for the merge-without-merge-with check
plan = []            # (sql, params, undo-record)
refusals = []

for path in files:
    base = os.path.splitext(os.path.basename(path))[0]
    errors = []
    out = check_identity.parse(path, ck, errors)
    print(f"\n== {base}")
    if errors:
        print(f"   REFUSED: check_identity.py reports {len(errors)} failure(s); nothing from this batch is applied")
        for e in errors[:10]:
            print(f"     {e}")
        refusals.append(base)
        continue

    # drop every line this file no longer owns: another file (an R- revisit) decides that shelf
    skipped = sorted({sid for sid in _decides[path]["sids"] if WINNER.get(sid) != path})
    if skipped:
        print(f"   superseded: {len(skipped)} shelf/shelves here are re-decided elsewhere and are NOT applied "
              f"from this file — " + ", ".join(f"S{s}->{os.path.splitext(os.path.basename(WINNER[s]))[0]}" for s in skipped[:8])
              + (" …" if len(skipped) > 8 else ""))
    own = set(_decides[path]["sids"]) - set(skipped)
    out["S"] = [r for r in out["S"] if r[0] in own]
    out["R"] = [r for r in out["R"] if r[0] in own]
    out["F"] = [r for r in out["F"] if r[0] in own or r[0] not in WINNER]
    out["I"] = [r for r in out["I"] if item_shelf.get(r[0]) not in set(skipped)]

    batch_plan = []
    for sid, cvv, gcdv, conf, why in out["S"]:
        c = float(conf)
        ks = sorted(keys.get(sid, ()))
        if not ks:
            uninterpretable.append(f"[{base}] S {sid} ({names.get(sid,'?')}): the shelf has no parsed key, "
                                   f"and SeriesKeyLink is keyed on ParsedKey — nothing to write against")
            continue
        undo = []
        if c >= 0.9:
            for prov, val in ((idbase.P_CV, cvv), (idbase.P_GCD, gcdv)):
                if val is None:
                    continue
                for k in ks:
                    was = existing.get((k, prov))
                    undo.append({"table": "SeriesKeyLink", "key": [k, prov], "was": was})
                    batch_plan.append((
                        """INSERT INTO SeriesKeyLink (ParsedKey, Provider, ProviderKey, Status, Score, AttemptCount, AttemptedAt)
                           VALUES (?,?,?,5,?,coalesce((SELECT AttemptCount FROM SeriesKeyLink WHERE ParsedKey=? AND Provider=?),0),?)
                           ON CONFLICT(ParsedKey, Provider) DO UPDATE SET
                             ProviderKey=excluded.ProviderKey, Status=5, Score=excluded.Score,
                             AttemptedAt=excluded.AttemptedAt, Error=NULL""",
                        (k, prov, int(val), int(round(c * 100)), k, prov, STAMP), undo[-1]))
                    totals["keylink"] += 1
                    totals["keylink_cv" if prov == idbase.P_CV else "keylink_gcd"] += 1
                    if was and was["ProviderKey"] is not None:
                        keylink_overwrite[(prov, was["Status"],
                                           "same id" if was["ProviderKey"] == int(val) else "DIFFERENT id")] += 1
                if prov == idbase.P_CV:
                    linked_cv.setdefault(int(val), set()).add(sid)
                    print(f"   [{base}] link   S{sid:<7} {names.get(sid,'?')[:40]:<40} "
                          f"{'cv' if prov == 0 else 'gcd'}={val} key={k!r} "
                          f"{'(was ' + str(was['ProviderKey']) + ' status ' + str(was['Status']) + ')' if was else '(new row)'}")
        else:
            k = primary.get(sid)
            batch_plan.append(("""INSERT INTO SeriesMatchReview (Scope, Key, State, Note, DecidedBy, DecidedAt)
                                  VALUES ('series', ?, 'review', ?, 'identity-pass', ?)""",
                               (k, f"[{base}] cv={cvv} gcd={gcdv} conf {conf}: {why}", STAMP),
                               {"table": "SeriesMatchReview", "key": [k], "was": None}))
            totals["review"] += 1
            print(f"   [{base}] review S{sid:<6} {names.get(sid,'?')[:40]:<40} cv={cvv} gcd={gcdv} conf {conf} "
                  f"-> SeriesMatchReview(Scope=series, Key={k!r})")

        ev_json = json.dumps({"batch": base, "line": f"S {sid} cv={cvv or '-'} gcd={gcdv or '-'} {conf}",
                              "evidence": why, "keys": ks}, ensure_ascii=False)
        batch_plan.append(("""INSERT INTO SeriesInferenceDecision
                              (SeriesKey, Class, Action, Target, Confidence, EvidenceJson, State, UndoJson, DecidedBy, DecidedAt)
                              VALUES (?, 'identity', ?, ?, ?, ?, ?, ?, 'identity-pass', ?)""",
                           (primary.get(sid), "link" if c >= 0.9 else "review",
                            f"cv:{cvv or '-'} gcd:{gcdv or '-'} S{sid}", conf, ev_json,
                            "Applied" if c >= 0.9 else "Review", json.dumps(undo, ensure_ascii=False), STAMP),
                           {"table": "SeriesInferenceDecision", "key": [primary.get(sid)], "was": None}))
        totals["decision"] += 1
        print(f"     decision SeriesInferenceDecision(Class='identity', Action='{'link' if c >= 0.9 else 'review'}', "
              f"Target='cv:{cvv or '-'} gcd:{gcdv or '-'} S{sid}', Confidence='{conf}', "
              f"State='{'Applied' if c >= 0.9 else 'Review'}', SeriesKey={primary.get(sid)!r})")
        print(f"              EvidenceJson={ev_json[:150]}")
        print(f"              UndoJson={json.dumps(undo, ensure_ascii=False)[:150]}")

    # ── the clear (wave 2's ten wrong merges) ───────────────────────────────────────────────────
    # A reader who writes `R 2156` + `F 2156 wrong-cv-link` has said the stored ComicVine link on that
    # shelf is wrong. The apply used to write NOTHING for an R, so the shelf's parsed key kept cv:3194 —
    # and when another shelf was correctly linked to 3194, BOTH keys resolved to `cv:3194`, the survivor
    # rule preferred the row that already held the key (SeriesResolver.cs, `OrderByDescending(s =>
    # s.CanonicalKey == key)`), and the right run was fused INTO the refused shelf. Ten shelves, wave 2.
    # So a refusal that names the link as wrong must CLEAR it. `SeriesResolver` reads
    # `SeriesKeyLink WHERE ProviderKey IS NOT NULL` regardless of Status, so Status alone is not enough —
    # the ProviderKey itself has to go NULL. Row shape copied from SeriesMismatchService.ClearLinkAsync.
    # GCD is deliberately NOT cleared: Provider=3 rows are inert to the resolver and carry real evidence.
    wrong_link = {sid for sid, fls in _decides[path].get("flags_full", {}).items()
                  for fl in fls if fl.split("=")[0] == "wrong-cv-link"}
    to_clear = {sid for sid, _why in out["R"] if sid in wrong_link}
    to_clear |= {sid for sid, cvv, _g, _c, _w in out["S"] if cvv is None and sid in wrong_link}
    # second arm: a refusal that FREES a volume this wave gives to another shelf, however it was flagged
    freed = set()
    for sid in ({s for s, _w in out["R"]} | {s for s, c, _g, _cf, _w in out["S"] if c is None}):
        vol = stored_cv.get(sid)
        if vol is not None and vol in claimed_by and sid not in claimed_by[vol]:
            freed.add(sid)
    if freed - to_clear:
        print(f"   frees: {sorted(freed - to_clear)} — refused here while this wave gives their stored "
              f"volume to another shelf; cleared so the resolve cannot keep them as survivors")
    to_clear |= freed
    for sid in sorted(to_clear):
        for k in sorted(keys.get(sid, ())):
            was = existing.get((k, idbase.P_CV))
            if not was or was["ProviderKey"] is None:
                continue                      # nothing stored: the refusal has nothing to undo
            undo = {"table": "SeriesKeyLink", "key": [k, idbase.P_CV], "was": was}
            batch_plan.append((
                """UPDATE SeriesKeyLink SET ProviderKey = NULL, Status = 6, Score = NULL, AttemptedAt = ?
                   WHERE ParsedKey = ? AND Provider = 0""",
                (STAMP, k), undo))
            batch_plan.append((
                """INSERT INTO SeriesInferenceDecision
                   (SeriesKey, Class, Action, Target, Confidence, EvidenceJson, State, UndoJson, DecidedBy, DecidedAt)
                   VALUES (?, 'identity', 'clear-link', 'Cv', NULL, ?, 'Applied', ?, 'identity-pass', ?)""",
                (k, json.dumps({"batch": base, "line": f"F {sid} wrong-cv-link",
                                "evidence": "refused shelf whose stored ComicVine link is wrong; cleared so "
                                            "the resolver cannot keep it as a survivor"}, ensure_ascii=False),
                 json.dumps([undo], ensure_ascii=False), STAMP),
                {"table": "SeriesInferenceDecision", "key": [k], "was": None}))
            totals["cleared"] += 1
            totals["decision"] += 1
            print(f"   [{base}] clear  S{sid:<7} {names.get(sid,'?')[:40]:<40} key={k!r} "
                  f"cv {was['ProviderKey']} (status {was['Status']}) -> NULL/Cleared")

    for sid, why in out["R"]:
        batch_plan.append(("""INSERT INTO SeriesInferenceDecision
                              (SeriesKey, Class, Action, Target, Confidence, EvidenceJson, State, UndoJson, DecidedBy, DecidedAt)
                              VALUES (?, 'identity', 'refuse', ?, NULL, ?, 'Refused', '[]', 'identity-pass', ?)""",
                           (primary.get(sid), f"S{sid}",
                            json.dumps({"batch": base, "line": f"R {sid}", "evidence": why}, ensure_ascii=False),
                            STAMP),
                           {"table": "SeriesInferenceDecision", "key": [primary.get(sid)], "was": None}))
        totals["decision"] += 1
        totals["refused"] += 1
        print(f"   [{base}] refuse S{sid:<6} {names.get(sid,'?')[:40]:<40} {why[:70]}")
        print(f"     decision SeriesInferenceDecision(Class='identity', Action='refuse', Target='S{sid}', "
              f"State='Refused', SeriesKey={primary.get(sid)!r}, UndoJson='[]')")

    # An F line is a question for Eric (§7-S). It is recorded as a decision so a flag raised in a batch is
    # countable in the DB and not only in a text file; it touches no ContainmentFlag row, because those are
    # keyed per ITEM and belong to the containment pass.
    for sid, flag, detail in out["F"]:
        batch_plan.append(("""INSERT INTO SeriesInferenceDecision
                              (SeriesKey, Class, Action, Target, Confidence, EvidenceJson, State, UndoJson, DecidedBy, DecidedAt)
                              VALUES (?, 'identity', ?, ?, NULL, ?, 'Flagged', '[]', 'identity-pass', ?)""",
                           (primary.get(sid), f"flag:{flag}", f"S{sid}",
                            json.dumps({"batch": base, "flag": flag, "detail": detail}, ensure_ascii=False), STAMP),
                           {"table": "SeriesInferenceDecision", "key": [primary.get(sid)], "was": None}))
        totals["decision"] += 1
        print(f"   [{base}] flag   S{sid:<7} {flag} — {detail[:70]}")

    for iid, cvi, gcdi, isbn, conf, why in out["I"]:
        if cvi is None and gcdi is None:
            # an ISBN-only I line: real evidence, but ItemProviderLink has no column for it, so it is
            # recorded as a decision and named here rather than silently doing nothing
            uninterpretable.append(f"[{base}] I {iid}: only isbn={isbn} — no provider column to store it in; "
                                   f"the decision row keeps the evidence")
        if iid not in item_shelf:
            uninterpretable.append(f"[{base}] I {iid}: the item is excluded or has no shelf")
        # An `I` id comes in two shapes and they mean different things. An ISSUE id names the exact record
        # (ProviderKey), and its series goes in SecondaryKey so the shelf-level rollups that read
        # SecondaryKey see it. `gcd=s<series>` says only "this book belongs to that run" — which is the
        # honest answer for a trade whose issue record does not exist — so ProviderKey stays NULL and only
        # SecondaryKey is written. A NULL ProviderKey is not a defect here; it is the claim being narrower.
        for prov, val in ((idbase.P_CV, cvi), (idbase.P_GCD, gcdi)):
            if val is None:
                continue
            if prov == idbase.P_GCD and str(val).startswith("s"):
                pk, sk, shape = None, str(val)[1:], f"gcd series {str(val)[1:]} (no issue record)"
            elif prov == idbase.P_GCD:
                pk, sk = str(val), gcd_issue_series.get(int(val))
                shape = f"gcd issue {val}" + (f" in series {sk}" if sk else " (series unknown)")
            else:
                pk, sk = str(val), cv_issue_volume.get(int(val))
                shape = f"cv issue {val}" + (f" in volume {sk}" if sk else " (volume unknown)")
            was = item_links.get((iid, prov))
            # Never clobber an earlier hand-made row in silence: it is recorded in UndoJson either way,
            # and it is called out on the line so the overwrite is a thing someone SAW.
            note = "(new row)"
            if was:
                note = (f"(OVERWRITES a Status=5 Manual row: ProviderKey {was['ProviderKey']}, "
                        f"SecondaryKey {was['SecondaryKey']}, Method {was['Method']})" if was["Status"] == 5
                        else f"(was ProviderKey {was['ProviderKey']} status {was['Status']})")
                if was["Status"] == 5:
                    totals["itemlink_manual_overwrite"] += 1
            batch_plan.append((
                """INSERT INTO ItemProviderLink (ItemId, Provider, ProviderKey, SecondaryKey, Status, Method, Confidence, AttemptCount, AttemptedAt)
                   VALUES (?,?,?,?,5,'identity-read',?,coalesce((SELECT AttemptCount FROM ItemProviderLink WHERE ItemId=? AND Provider=?),0),?)
                   ON CONFLICT(ItemId, Provider) DO UPDATE SET
                     ProviderKey=excluded.ProviderKey, SecondaryKey=excluded.SecondaryKey, Status=5,
                     Method='identity-read', Confidence=excluded.Confidence,
                     AttemptedAt=excluded.AttemptedAt, Error=NULL""",
                (iid, prov, pk, sk, float(conf), iid, prov, STAMP),
                {"table": "ItemProviderLink", "key": [iid, prov], "was": was}))
            totals["itemlink"] += 1
            print(f"   [{base}] item   {iid:<8} {shape} conf {conf} {note}")
        if isbn:
            print(f"   [{base}] note   item {iid} isbn={isbn} — kept in the decision's evidence only "
                  f"(ItemProviderLink has no ISBN column)")
        batch_plan.append(("""INSERT INTO SeriesInferenceDecision
                              (SeriesKey, Class, Action, Target, Confidence, EvidenceJson, State, UndoJson, DecidedBy, DecidedAt)
                              VALUES (NULL, 'identity', 'item-link', ?, ?, ?, 'Applied', '[]', 'identity-pass', ?)""",
                           (f"item:{iid} cv:{cvi or '-'} gcd:{gcdi or '-'} isbn:{isbn or '-'}", conf,
                            json.dumps({"batch": base, "line": f"I {iid}", "evidence": why}, ensure_ascii=False),
                            STAMP),
                           {"table": "SeriesInferenceDecision", "key": [f"item:{iid}"], "was": None}))
        totals["decision"] += 1

    plan.append((base, batch_plan))

STAT = {0: "Pending", 1: "Matched", 2: "NoMatch", 3: "Multiple", 4: "Error", 5: "Manual", 6: "Cleared",
        7: "Skip"}
print("\n" + "=" * 96)
print(f"{len(files)} batch file(s); {len(refusals)} refused ({', '.join(refusals) or 'none'})")
print(f"  SeriesKeyLink rows      : {totals['keylink']:,}   "
      f"(Cv {totals['keylink_cv']:,} · Gcd {totals['keylink_gcd']:,})")
ow = sum(keylink_overwrite.values())
print(f"     of which OVERWRITE an existing row: {ow:,}")
for (prov, st, same), n in sorted(keylink_overwrite.items(), key=lambda x: -x[1]):
    print(f"       {n:>6,}  {'Cv' if prov == 0 else 'Gcd'} row at Status {st} ({STAT.get(st, st)}), {same}")
print(f"  SeriesInferenceDecision : {totals['decision']:,}   (of which refusals {totals['refused']:,})")
print(f"  SeriesMatchReview       : {totals['review']:,}")
print(f"  ItemProviderLink        : {totals['itemlink']:,}   "
      f"(overwriting an existing Status=5 Manual row: {totals['itemlink_manual_overwrite']:,})")
print(f"  SeriesKeyLink CLEARED   : {totals['cleared']:,}   (refused shelves whose stored Cv link the reader called wrong)")

# ── the two things that must be zero before a landing ────────────────────────────────────────────
print(f"\n  lines the applier could not interpret: {len(uninterpretable):,}   <- must be zero")
for u in uninterpretable:
    print(f"       {u}")
shared = {v: sorted(s) for v, s in linked_cv.items() if len(s) > 1}
merge_ok = {sid for path in files for sid, fl in
            ((sid, fl) for sid, fls in _decides[path]["flags"].items() for fl in fls) if fl == "merge-with"}
unflagged = {v: s for v, s in shared.items() if not (set(s) & merge_ok)}
print(f"  cv ids that would be linked on two different shelves with NO merge-with: {len(unflagged):,}"
      f"   <- must be zero  (linking both MERGES them, §6.5)")
for v, s in sorted(unflagged.items()):
    print(f"       cv {v} on shelves {s}")
if shared and not unflagged:
    print(f"       ({len(shared)} shared cv id(s), every one carrying a merge-with flag — deliberate merges)")

# The rebuild names a shelf from CvVolume.Name; without the row it falls back to the raw parsed key
# (§6.6), so this is the population Phase C.1 must fetch before the names settle.
need_fetch = sorted(v for v in linked_cv if v not in ck.cv_volume_rows())
print(f"  linked cv ids with NO CvVolume row (needs-fetch; the shelf keeps its parsed-key name until "
      f"Phase C.1 fetches it): {len(need_fetch):,} of {len(linked_cv):,} distinct volumes")

# ── MERGE EXPOSURE (TOOLS_TODO 7) ────────────────────────────────────────────────────────────────
# Wave 1 landed green and then halted at `audit_containment` on Kick-Ass and Super Friends, because a
# merge does not only move a shelf — it moves that shelf's COLLECTED EDITIONS onto a shelf whose
# containment was already judged. Two things go wrong there, and neither is visible from the identity
# side: the survivor's decision file no longer covers every edition on its shelf (the coverage contract
# fails), and an edition that arrives carrying an unjudged provider span suddenly has that claim armed
# against a run it was never measured against (§14.13, the hull). So the applier names them BEFORE the
# resolve, and `merge_refusals.py` writes the refusals after it.
CONT_DEC = os.path.join(idbase.ROOT, os.pardir, "containment", "decisions")
stored_cv = {}
for cvid, sid in ro.execute(f"SELECT CvVolumeId, Id FROM Series WHERE CvVolumeId IS NOT NULL "
                            f"AND Id IN ({idbase.SHELF_SQL})"):
    stored_cv.setdefault(cvid, set()).add(sid)
groups = {}
for vid, sids in linked_cv.items():
    g = set(sids) | stored_cv.get(vid, set())
    if len(g) > 1:
        groups[vid] = sorted(g)
print(f"\n  MERGE EXPOSURE: {len(groups)} cv volume(s) will hold more than one of today's shelves")
exposed = 0
for vid, g in sorted(groups.items()):
    q = ",".join(str(x) for x in g)
    rows = ro.execute(f"""
        SELECT i.Id, i.SeriesId, substr(i.FileName,1,58),
               (SELECT count(*) FROM CollectedEditionSpan s
                WHERE s.ItemId = i.Id AND s.Source <> 3 AND s.IssueStart IS NOT NULL),
               (SELECT count(*) FROM CollectedEditionSpan s
                WHERE s.ItemId = i.Id AND s.Source = 3)
        FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE i.SeriesId IN ({q}) AND cd.IsCollection = 1
          AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0""").fetchall()
    if not rows:
        continue
    have_file = {s for s in g if os.path.isfile(os.path.join(CONT_DEC, f"S{s}.txt"))}
    risky = [r for r in rows if (r[3] and not r[4]) or (r[1] not in have_file and have_file)]
    if not risky:
        continue
    exposed += len(risky)
    print(f"    cv {vid}: shelves {g}  (containment decision file on: {sorted(have_file) or 'none'})")
    for iid, sid, fn, prov, judged in risky:
        why = []
        if prov and not judged:
            why.append(f"carries {prov} UNJUDGED provider span(s)")
        if sid not in have_file and have_file:
            why.append(f"moves onto a shelf that has a decision file, from S{sid} which has none")
        print(f"       item {iid:<8} S{sid:<7} {fn:<58} — {'; '.join(why)}")
print(f"  {exposed} collected edition(s) exposed — merge_refusals.py writes their refusals after the resolve")

if not APPLY:
    print("\n(dry run — nothing written. Re-run with --apply, and only after backup_live.py)")
    raise SystemExit(1 if refusals else 0)

os.makedirs(idbase.UNDO, exist_ok=True)
w = sqlite3.connect(idbase.HOT)
for base, batch_plan in plan:
    # §6.10: the undo log is flushed per batch, beside the write it records — not at the end of the wave,
    # where an interrupted run leaves writes with no record of what they replaced.
    undo_path = os.path.join(idbase.UNDO, f"{base}-{STAMP}.jsonl")
    with open(undo_path, "w", encoding="utf-8") as u:
        for _sql, _p, rec in batch_plan:
            u.write(json.dumps(rec, ensure_ascii=False) + "\n")
    for sql, params, _rec in batch_plan:
        w.execute(sql, params)
    w.commit()
    print(f"applied {base}: {len(batch_plan)} row(s); undo -> {undo_path}")
w.close()
print("\napplied. Now: books-resolve --series, run_chain3.ps1, then the PLAN §8 checks.")
