"""The coverage contract for a decision file: every shelf in the batch decided exactly once, every id real.

`python check_identity.py <decisions/A-001.txt> [more...]`   (with no arguments, every file in decisions/)

`pass2.py`'s refusal to expand a series unless every collected edition in it was decided exactly once is
what made "I did this series" checkable rather than claimed, and `check_decisions.py` turned that into a
report over all 976 files instead of a stop at the first. This is the same instrument for identity, with
the extra rules PLAN §7-S names — and one of them is not bookkeeping but a safety interlock: two `S` lines
sharing a `cv=` MERGE those two shelves at the next `books-resolve --series` (§6.5), which is right for a
duplicated shelf and catastrophic for two different runs, so it is refused unless one line says so.

Grammar (PLAN §7-S):
    S <sid> cv=<volumeId>|- gcd=<gcdSeriesId>|- <conf> | <evidence>
    R <sid> | <why no identity can be stored>
    F <sid> <flag> | <detail>
    I <itemId> cv=<issueId>|- gcd=<issueId>|- isbn=<isbn>|- <conf> | <evidence>
    N <sid> <note>
"""
import os
import re
import sys

import idbase

MIN_EVIDENCE = 40


class Checker:
    def __init__(self):
        self.con = idbase.open_hot()
        self.shelves = {r[0] for r in self.con.execute(idbase.SHELF_SQL)}
        self.items = None
        self._cv_ok, self._cv_bad = set(), set()
        self._gcd_ok, self._gcd_bad = set(), set()
        self.gcd_dump = idbase.open_gcd_dump()
        self.cvref = idbase.open_cv_ref()
        self.cvrip = None
        # A shelf a landed wave MERGED AWAY is not a bad id — it is a decision that took effect. Without
        # this the checker can never be green again after any wave (wave 1 alone made 36 of them), and a
        # gate that cannot go green is a gate nobody runs. `SeriesMerge` is the resolver's own record, so
        # "merged" is read, not inferred; an id in neither table is still a failure.
        self.merged = {}
        for old, new in self.con.execute(
                "SELECT OldSeriesId, NewSeriesId FROM SeriesMerge ORDER BY MergedAt"):
            self.merged[old] = new
        self.all_series = {r[0] for r in self.con.execute("SELECT Id FROM Series")}
        self.conflated = {r[0] for r in self.con.execute(
            """SELECT SeriesId FROM ContainmentFlag WHERE Flag='conflated-series' AND SeriesId IS NOT NULL
               AND (ReviewState IS NULL OR ReviewState IN ('','Pending','Open'))""")}

    def item_exists(self, iid):
        if self.items is None:
            self.items = {r[0] for r in self.con.execute(
                "SELECT Id FROM Item WHERE Kind = 0 AND coalesce(IsExcluded,0) = 0")}
        return iid in self.items

    def cv_volume_exists(self, vid):
        """Real means: we hold the volume, or the local ComicVine rip does. Nothing is accepted on the
        strength of the reader having typed it."""
        if vid in self._cv_ok:
            return True
        if vid in self._cv_bad:
            return False
        ok = bool(self.con.execute("SELECT 1 FROM CvVolume WHERE Id=?", (vid,)).fetchone())
        if not ok and self.cvref is not None:
            ok = bool(self.cvref.execute("SELECT 1 FROM cv_vol WHERE volId=?", (vid,)).fetchone())
        if not ok:
            if self.cvrip is None:
                self.cvrip = idbase.open_cv_rip() or False
            if self.cvrip:
                for t in ("cv_volume", "cv_volumes"):
                    try:
                        if self.cvrip.execute(f"SELECT 1 FROM {t} WHERE id=?", (vid,)).fetchone():
                            ok = True
                            break
                    except Exception:
                        pass
        if not ok:
            ok = bool(self.con.execute(
                "SELECT 1 FROM legs.LinkCandidates WHERE Scope=1 AND Provider=0 AND CandidatesJson LIKE ? LIMIT 1",
                (f'%"VolumeId":{vid},%',)).fetchone())
        (self._cv_ok if ok else self._cv_bad).add(vid)
        return ok

    def gcd_series_exists(self, gid):
        if gid in self._gcd_ok:
            return True
        if gid in self._gcd_bad:
            return False
        ok = bool(self.con.execute("SELECT 1 FROM legs.GcdSeries WHERE GcdSeriesId=?", (gid,)).fetchone())
        if not ok and self.gcd_dump is not None:
            ok = bool(self.gcd_dump.execute("SELECT 1 FROM gcd_series WHERE id=?", (gid,)).fetchone())
        (self._gcd_ok if ok else self._gcd_bad).add(gid)
        return ok

    def cv_volume_rows(self):
        """The CvVolume ids we actually hold. A linked volume missing from here keeps the shelf on its raw
        parsed-key name after the rebuild (PLAN §6.6) until Phase C.1 fetches it."""
        if getattr(self, "_cv_rows", None) is None:
            self._cv_rows = {r[0] for r in self.con.execute("SELECT Id FROM CvVolume")}
        return self._cv_rows

    def cv_issue_exists(self, iid):
        ok = bool(self.con.execute("SELECT 1 FROM CvIssue WHERE Id=?", (iid,)).fetchone())
        if not ok and self.cvref is not None:
            ok = bool(self.cvref.execute("SELECT 1 FROM cv_iss WHERE issueId=?", (iid,)).fetchone())
        return ok

    def gcd_issue_exists(self, iid):
        ok = bool(self.con.execute("SELECT 1 FROM legs.GcdIssue WHERE GcdIssueId=?", (iid,)).fetchone())
        if not ok and self.gcd_dump is not None:
            ok = bool(self.gcd_dump.execute("SELECT 1 FROM gcd_issue WHERE id=?", (iid,)).fetchone())
        return ok


def ids_path(decision_path):
    """The .ids beside the decision file, else the one in batches/ with the same name. The batch's ids
    file IS the coverage set: without it there is nothing to be complete against."""
    base = os.path.splitext(os.path.basename(decision_path))[0]
    here = os.path.join(os.path.dirname(os.path.abspath(decision_path)), base + ".ids")
    if os.path.exists(here):
        return here
    return os.path.join(idbase.BATCHES, base + ".ids")


def _shelf_state(ck, sid, loc, errors, out):
    """A shelf named by a decision is in one of three states, and only one of them is a defect.

    Merged away by a landed wave -> the decision took effect and the file is now the audit trail of it;
    recorded, never a failure. Still in Series but holding no comic files -> the same thing one step
    further on (its files moved), also recorded. In neither table -> a bad id, which is a failure."""
    if sid in ck.merged:
        out["landed"].append((sid, ck.merged[sid]))
    elif sid in ck.all_series:
        out["landed"].append((sid, None))
    else:
        errors.append(f"{loc}: shelf S{sid} is in neither Series nor SeriesMerge — no such shelf ever")


def _kv(tok):
    k, _, v = tok.partition("=")
    return k, (None if v == "-" else v)


def parse(path, ck, errors):
    """Return {'S':…, 'R':…, 'F':…, 'I':…, 'N':…} and append every failure to `errors`."""
    base = os.path.basename(path)
    ip = ids_path(path)
    if not os.path.exists(ip):
        errors.append(f"{base}: no .ids file for this batch (looked beside it and in batches/)")
        want = None
    else:
        want = [int(x) for x in open(ip, encoding="utf-8").read().split()]

    decided, out = {}, {"S": [], "R": [], "F": [], "I": [], "N": [], "landed": []}
    cv_seen, gcd_seen, merges = {}, {}, set()

    for n, raw in enumerate(open(path, encoding="utf-8"), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        kind, _, rest = line.partition(" ")
        loc = f"{base}:{n}"
        if kind not in ("S", "R", "F", "I", "N"):
            errors.append(f"{loc}: unknown directive '{kind}'")
            continue
        if kind == "N":
            head, _, note = rest.strip().partition(" ")
            if not head.lstrip("S").isdigit():
                errors.append(f"{loc}: N needs a shelf id")
                continue
            out["N"].append((int(head.lstrip("S")), note.strip()))
            continue
        if "|" not in rest:
            errors.append(f"{loc}: no '|' clause — every decision must say what agreed")
            continue
        head, _, why = rest.partition("|")
        head, why = head.split(), why.strip()
        if kind == "F":
            if len(head) < 2:
                errors.append(f"{loc}: F needs a shelf id and a flag")
                continue
            sid, flag = int(head[0].lstrip("S")), head[1].split("=")[0]
            if flag not in idbase.FLAGS:
                errors.append(f"{loc}: unknown flag '{flag}'")
            if sid not in ck.shelves:
                _shelf_state(ck, sid, loc, errors, out)
            out["F"].append((sid, head[1], why))
            if head[1].startswith("merge-with="):
                merges.add(sid)
            continue

        if kind == "R":
            if not head:
                errors.append(f"{loc}: R needs a shelf id")
                continue
            sid = int(head[0].lstrip("S"))
            if sid in decided:
                errors.append(f"{loc}: shelf S{sid} decided twice (line {decided[sid]} already decided it)")
            decided[sid] = n
            if sid not in ck.shelves:
                _shelf_state(ck, sid, loc, errors, out)
            if len(why) < MIN_EVIDENCE:
                errors.append(f"{loc}: evidence clause is {len(why)} characters, minimum {MIN_EVIDENCE} — "
                              f"a refusal must name what was seen and why each candidate was rejected")
            out["R"].append((sid, why))
            continue

        if kind == "S":
            if len(head) < 2:
                errors.append(f"{loc}: S needs a shelf id, cv=, gcd= and a confidence")
                continue
            sid = int(head[0].lstrip("S"))
            kv = dict(_kv(t) for t in head[1:] if "=" in t)
            conf = next((t for t in head[1:] if "=" not in t), None)
            if sid in decided:
                errors.append(f"{loc}: shelf S{sid} decided twice (line {decided[sid]} already decided it)")
            decided[sid] = n
            if sid not in ck.shelves:
                _shelf_state(ck, sid, loc, errors, out)
            cvv, gcdv = kv.get("cv"), kv.get("gcd")
            if cvv is None and gcdv is None:
                errors.append(f"{loc}: S S{sid} names no id — cv= and gcd= are both '-'; an identity needs "
                              f"at least one id, otherwise write R")
            if conf not in idbase.CONFIDENCES:
                errors.append(f"{loc}: bad confidence '{conf}' — must be one of "
                              f"{', '.join(idbase.CONFIDENCES)} (PLAN §4)")
            if len(why) < MIN_EVIDENCE:
                errors.append(f"{loc}: evidence clause is {len(why)} characters, minimum {MIN_EVIDENCE} — "
                              f"name WHAT agreed and the arithmetic that held")
            if sid in ck.conflated:
                errors.append(f"{loc}: S on S{sid}, which carries an OPEN conflated-series flag — a shelf "
                              f"that is several runs has no one identity (PLAN §3.3); write F + R")
            if cvv is not None:
                if not cvv.isdigit() or not ck.cv_volume_exists(int(cvv)):
                    errors.append(f"{loc}: unknown ComicVine volume id {cvv} — not in CvVolume, the local "
                                  f"rip, or any stored candidate")
                else:
                    cv_seen.setdefault(int(cvv), []).append(sid)
            if gcdv is not None:
                if not gcdv.isdigit() or not ck.gcd_series_exists(int(gcdv)):
                    errors.append(f"{loc}: unknown GCD series id {gcdv} — not in legs.GcdSeries or the dump")
                else:
                    gcd_seen.setdefault(int(gcdv), []).append(sid)
            out["S"].append((sid, int(cvv) if cvv and cvv.isdigit() else None,
                             int(gcdv) if gcdv and gcdv.isdigit() else None, conf, why))
            continue

        if kind == "I":
            if len(head) < 2:
                errors.append(f"{loc}: I needs an item id, ids and a confidence")
                continue
            iid = int(head[0])
            kv = dict(_kv(t) for t in head[1:] if "=" in t)
            conf = next((t for t in head[1:] if "=" not in t), None)
            if not ck.item_exists(iid):
                errors.append(f"{loc}: unknown item id {iid}")
            if conf not in idbase.CONFIDENCES:
                errors.append(f"{loc}: bad confidence '{conf}' — must be one of {', '.join(idbase.CONFIDENCES)}")
            if len(why) < MIN_EVIDENCE:
                errors.append(f"{loc}: evidence clause is {len(why)} characters, minimum {MIN_EVIDENCE}")
            kv = {k: (None if v in ("", "-") else v) for k, v in kv.items()}   # '-' means "no id"
            if kv.get("cv"):
                if not kv["cv"].isdigit():
                    errors.append(f"{loc}: I cv= must be a ComicVine ISSUE id or '-', got {kv['cv']}")
                elif not ck.cv_issue_exists(int(kv["cv"])):
                    errors.append(f"{loc}: unknown ComicVine issue id {kv['cv']}")
            if kv.get("gcd"):
                g = kv["gcd"]
                if g.startswith("s") and g[1:].isdigit():          # gcd=s<series id>: only the series is known
                    if not ck.gcd_series_exists(int(g[1:])):
                        errors.append(f"{loc}: unknown GCD series id {g[1:]} in gcd=s{g[1:]}")
                elif not g.isdigit():
                    errors.append(f"{loc}: I gcd= must be a GCD ISSUE id, s<series id>, or '-', got {g}")
                elif not ck.gcd_issue_exists(int(g)):
                    errors.append(f"{loc}: unknown GCD issue id {g}")
            if not any(kv.get(k) for k in ("cv", "gcd", "isbn")):
                errors.append(f"{loc}: I item {iid} names no id")
            out["I"].append((iid, kv.get("cv"), kv.get("gcd"), kv.get("isbn"), conf, why))
            continue

    for vid, sids in cv_seen.items():
        if len(set(sids)) > 1 and not (set(sids) & merges):
            errors.append(f"{base}: shelves {sorted(set(sids))} share cv={vid} with no 'F <sid> merge-with=' "
                          f"line — linking both MERGES them at the next books-resolve --series (PLAN §6.5)")
    for gid, sids in gcd_seen.items():
        if len(set(sids)) > 1 and not (set(sids) & merges):
            errors.append(f"{base}: shelves {sorted(set(sids))} share gcd={gid} with no 'F <sid> merge-with=' line")

    if want is not None:
        alive = [s for s in want if s in ck.shelves]
        missing = [s for s in alive if s not in decided]
        extra = [s for s in decided if s not in set(want)]
        if missing:
            errors.append(f"{base}: {len(missing)} shelf/shelves in the batch have no S or R line: "
                          f"missing shelf {sorted(missing)[:12]}")
        if extra:
            errors.append(f"{base}: {len(extra)} decision(s) for shelves not in this batch: {sorted(extra)[:12]}")
    return out


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    every = "--all" in sys.argv
    # --all turns ON the cross-file checks (duplicate decisions, supersessions). With no paths it also
    # widens the set to every decision file, which is how the lead runs it; with paths it checks exactly
    # those, which is how a wave checks the files it is about to land.
    if args:
        files = [idbase.resolve_decision_file(a) for a in args]
    else:
        files = [os.path.join(idbase.DECISIONS, f) for f in sorted(os.listdir(idbase.DECISIONS))
                 if f.endswith(".txt")] if os.path.isdir(idbase.DECISIONS) else []
    if not files:
        print("no decision files")
        return 0
    ck = Checker()
    total, all_landed = 0, []
    for path in files:
        errors = []
        out = parse(path, ck, errors)
        total += len(errors)
        tag = "FAIL" if errors else " OK "
        rev = "revisit" if idbase.revisit_rank(path) is not None else ""
        landed = f"  {len(out['landed'])} landed" if out["landed"] else ""
        print(f"  {tag} {os.path.basename(path):<16} {len(out['S']):>4} S {len(out['R']):>4} R "
              f"{len(out['F']):>3} F {len(out['I']):>3} I {len(out['N']):>3} N  {rev}{landed}")
        all_landed.extend((os.path.basename(path), sid, new) for sid, new in out["landed"])
        for e in errors:
            print(f"        {e}")

    if every:
        # Deciding a shelf twice is normally the bug the coverage contract exists to catch. Deciding it
        # twice ON PURPOSE — because a ruling was sharpened — is the one legitimate case, and it is
        # legitimate only when the second file announces itself as a revisit in its NAME. So: two tier
        # batches deciding one shelf is a failure; a revisit superseding a tier batch is reported and
        # counted, so the supersession is visible rather than silent.
        _decides, winner, superseded, dupes = idbase.scan_decisions(files)
        print(f"\n  --all: {len(winner):,} shelf/shelves decided across {len(files)} file(s)")
        for sid, files_ in sorted(dupes.items()):
            total += 1
            print(f"        FAIL S{sid} is decided in two non-revisit files: "
                  f"{[os.path.basename(f) for f in files_]} — only an R- file may re-decide a shelf")
        # The per-file shared-cv rule cannot see across batches, and a landing applies every batch at
        # once: two shelves linked to one CV volume MERGE at the next `books-resolve --series` (§6.5)
        # whether the two S lines sit in one file or thirty apart. The first full dry run found exactly
        # one such pair, so this is the cross-file form of the same rule.
        cv_owner, merge_flagged, merge_targets = {}, set(), {}
        for p, rec in _decides.items():
            for sid, fls in rec.get("flags_full", {}).items():
                for fl in fls:
                    if fl.split("=")[0] == "merge-with":
                        merge_flagged.add(sid)
                        tgt = fl.split("=", 1)[1] if "=" in fl else ""
                        if tgt.lstrip("S").isdigit():
                            merge_targets.setdefault(sid, set()).add(int(tgt.lstrip("S")))
        for p, rec in _decides.items():
            for sid, vid in rec["cv"].items():
                # a shelf a landed wave already merged away cannot merge again; re-reporting it would
                # keep every past wave's intended merges on the gate forever
                if winner.get(sid) == p and sid in ck.shelves:
                    cv_owner.setdefault(vid, set()).add(sid)
        for vid, sids in sorted(cv_owner.items()):
            if len(sids) > 1 and not (sids & merge_flagged):
                total += 1
                print(f"        FAIL cv={vid} is linked on shelves {sorted(sids)} across files with no "
                      f"'F <sid> merge-with=' line — the rebuild would MERGE them (PLAN §6.5)")

        # TOOLS_TODO 6. The same merge happens against a shelf NOBODY re-decided: an `S cv=<id>` whose id
        # is already the stored CvVolumeId of a different file-holding shelf lands both on canonical key
        # `cv:<id>` and merges them at the next resolve, exactly as two S lines would. Wave 1 made eight of
        # these; every one was intended, and not one was declared, so the checker could not tell an
        # intended merge from an accident. Now it must be said out loud.
        stored = {}
        for cvid, sid in ck.con.execute(
                f"SELECT CvVolumeId, Id FROM Series WHERE CvVolumeId IS NOT NULL AND Id IN ({idbase.SHELF_SQL})"):
            stored.setdefault(cvid, set()).add(sid)
        # …but only against a partner that will STILL hold that volume at landing. A partner this pass
        # re-links to a different volume, or refuses, has no collision to declare — and readers withheld
        # 11+ ids they had got right because the rule could not see that (TOOLS_TODO 10). The order
        # matters: the resolve reads what the SeriesKeyLink rows say after this wave, not what
        # Series.CvVolumeId says before it.
        decided_cv, refused = {}, set()
        for p, rec in _decides.items():
            for sid in rec["sids"]:
                if winner.get(sid) != p:
                    continue
                if rec["kinds"].get(sid) == "R":
                    refused.add(sid)
                elif sid in rec["cv"]:
                    decided_cv[sid] = rec["cv"][sid]
        for vid, sids in sorted(cv_owner.items()):
            others = stored.get(vid, set()) - sids
            # a partner re-linked elsewhere, or refused, is not a partner
            others = {o for o in others
                      if not (o in refused or (o in decided_cv and decided_cv[o] != vid))}
            if not others:
                continue
            declared = {t for s in sids for t in merge_targets.get(s, set())}
            undeclared = others - declared
            if undeclared:
                total += 1
                print(f"        FAIL cv={vid} on shelf/shelves {sorted(sids)} is ALREADY the stored "
                      f"CvVolumeId of {sorted(undeclared)} — the resolve merges them; declare it with "
                      f"'F {sorted(sids)[0]} merge-with={sorted(undeclared)[0]}' or link a different volume")
        # Rule 1 of the applier CLEARS the stored Cv link of any shelf that is refused (or linked to
        # nothing) while carrying `wrong-cv-link`. That clear is what stops wave 2's fusion, so it must be
        # visible here, beside the collision rule it interacts with, rather than only in the apply log.
        will_clear = []
        for p2, rec in _decides.items():
            wrong = {sid for sid, fls in rec.get("flags_full", {}).items()
                     for fl in fls if fl.split("=")[0] == "wrong-cv-link"}
            for sid in sorted(wrong):
                if winner.get(sid) != p2:
                    continue
                kind, cvv = rec["kinds"].get(sid), rec["cv"].get(sid)
                if kind == "R" or (kind == "S" and cvv is None):
                    will_clear.append((sid, os.path.splitext(os.path.basename(p2))[0]))
        if will_clear:
            print(f"        info: {len(will_clear)} shelf/shelves are refused AND flagged wrong-cv-link — "
                  f"their stored ComicVine link will be CLEARED at apply, which is what frees the volume "
                  f"for the shelf that earned it")
            for sid, b in will_clear[:12]:
                stored = ck.con.execute("SELECT CvVolumeId FROM Series WHERE Id=?", (sid,)).fetchone()
                print(f"          S{sid:<7} {b:<10} stored cv {stored[0] if stored else '?'} -> NULL")
            if len(will_clear) > 12:
                print(f"          ... and {len(will_clear)-12} more")
        if superseded:
            print(f"        info: {len(superseded)} shelf/shelves superseded by a revisit")
            for sid, olds in sorted(superseded.items()):
                print(f"          S{sid:<7} {os.path.basename(winner[sid]):<14} supersedes "
                      f"{', '.join(os.path.basename(o) for o in olds)}")
    if all_landed:
        merged = [(f, sid, new) for f, sid, new in all_landed if new]
        emptied = [(f, sid) for f, sid, new in all_landed if not new]
        print()
        print(f"  info: {len(all_landed)} decision(s) name a shelf that a landed wave has since changed "
              f"— {len(merged)} merged away, {len(emptied)} still present but holding no comic files. "
              f"These decisions took effect; the files are their audit trail.")
        for f, sid, new in merged[:12]:
            print(f"          {f:<14} S{sid} -> merged into S{new}")
        if len(merged) > 12:
            print(f"          ... and {len(merged)-12} more")
    print(f"{len(files)} file(s), {total} failure(s)")
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
