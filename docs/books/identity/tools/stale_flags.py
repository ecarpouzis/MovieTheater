"""Stale conflated / overlap flags land in ONE wave (TOOLS_TODO 37): the reader's claim, the lead's approval, the
dismissal — right before the S that the claim lets stand is applied.

`python stale_flags.py --gate <batch> [...] [--approved FILE] [--db PATH]`
    Print every `F <sid> stale-flag=<flagId> | evidence` claim in the named decision files, with the flag it names
    and the shelf's file count, and exit 1 (STOP) unless every claim on a still-OPEN flag is listed in
    `identity/stale-approved.txt`. Read-only.
`python stale_flags.py --dismiss <batch> [...] [--approved FILE] [--db PATH] [--undo-dir DIR] [--apply]`
    The gate, then — for exactly the approved claims whose flag is still open — ContainmentFlag.ReviewState =
    'Dismissed', Note = the reader's evidence + ' | lead-verified (<batch>)', DecidedBy = 'identity-pass',
    DecidedAt = now. Dry run by default (prints the plan, writes nothing). `--apply` writes, in one transaction,
    and first writes an undo journal (`<undo-dir>/wave-stale-flags-<stamp>.jsonl`: every row as it was).

Why a separate step at all. Before this, a reader who found an open flag stale had to write R + `F conflated-series`,
the lead verified and dismissed it by hand, and a LATER revisit landed the S — three waves for one shelf (R-031 ->
R-034 -> R-036). Now the S rides with the claim: check_identity accepts an S on a flagged shelf only when the same
file claims every open flag on it stale, and the landing (wave_land.ps1) STOPS until the lead has looked at the
shelf's files and listed each flag id here. What is never done: dismissing a flag nobody approved, or one on a
shelf other than the one the claim names.

`stale-approved.txt`: one flag id per line (`662` or `662  S94684  checked the four books` — the first token is
the id; `#` starts a comment). The file is the lead's, never a reader's.

Idempotent: a flag already dismissed is reported and skipped, so a re-run after an interruption changes nothing
twice. `--db` exists for the selftest, which runs this against a COPY of the flag table — never the live DB.
"""
import datetime
import json
import os
import re
import sqlite3
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

import idbase

RX_CLAIM = re.compile(r"^F\s+S?(\d+)\s+stale-flag=(\S+)\s*\|(.*)$")


def claims(paths):
    """[(file base, line no, sid, flagId|None, raw id text, evidence)] for every stale-flag line in `paths`."""
    out = []
    for p in paths:
        base = os.path.splitext(os.path.basename(p))[0]
        for n, raw in enumerate(open(p, encoding="utf-8"), 1):
            m = RX_CLAIM.match(raw.strip())
            if m:
                fid = int(m.group(2)) if m.group(2).isdigit() else None
                out.append((base, n, int(m.group(1)), fid, m.group(2), m.group(3).strip()))
    return out


def approved_ids(path):
    ids = set()
    if path and os.path.exists(path):
        for raw in open(path, encoding="utf-8"):
            t = raw.split("#", 1)[0].strip()
            if t and t.split()[0].isdigit():
                ids.add(int(t.split()[0]))
    return ids


def open_db(db, write=False):
    if db:
        return sqlite3.connect(db) if write else sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    return idbase.open_hot(write=write)


def gate(paths, approved_path, db=None, con=None):
    """-> (ok, plan): plan = [(base, sid, fid, evidence)] of approved claims on OPEN flags (what --dismiss writes).
    Prints every claim. ok is False when any claim names a missing flag, a flag on another shelf, or an OPEN
    flag the lead has not approved."""
    con = con or open_db(db)
    ok_ids = approved_ids(approved_path)
    cl = claims(paths)
    has_items = bool(con.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='Item'").fetchone())
    plan, bad = [], []
    print(f"stale-flag claims: {len(cl)} in {len(paths)} file(s); approved list {approved_path} "
          f"({len(ok_ids)} id(s){'' if os.path.exists(approved_path or '') else ' — FILE MISSING'})")
    for base, n, sid, fid, raw, why in cl:
        loc = f"{base}:{n} S{sid}"
        row = con.execute("SELECT SeriesId, Flag, ReviewState, Detail FROM ContainmentFlag WHERE Id = ?",
                          (fid,)).fetchone() if fid is not None else None
        if row is None:
            bad.append(f"{loc}: stale-flag={raw} names no ContainmentFlag row")
            continue
        fsid, flag, state, detail = row
        is_open = state in idbase.OPEN_FLAG_STATES
        files = con.execute("SELECT count(*) FROM Item WHERE SeriesId = ? AND Kind = 0 AND coalesce(IsExcluded,0) = 0",
                            (sid,)).fetchone()[0] if has_items else "?"
        print(f"  {loc} ({files} files)  flag {fid} {flag} [{state or 'Pending'}] on S{fsid}: {(detail or '')[:100]}")
        print(f"      claim: {why[:240]}")
        if flag not in idbase.STALE_KINDS:
            bad.append(f"{loc}: flag {fid} is '{flag}', not {' / '.join(idbase.STALE_KINDS)}")
        elif not is_open:
            print(f"      already {state} — nothing to dismiss")
        elif fsid != sid:
            bad.append(f"{loc}: flag {fid} sits on S{fsid}, not S{sid} — a claim may dismiss only its own shelf's flag")
        elif fid not in ok_ids:
            bad.append(f"{loc}: flag {fid} is NOT in the approved list — the lead reads S{sid}'s files, then adds "
                       f"'{fid}' to {os.path.basename(approved_path or idbase.STALE_APPROVED)}")
        else:
            plan.append((base, sid, fid, why))
            print("      APPROVED — dismissed right before apply")
    for b in bad:
        print(f"  STOP {b}")
    print(f"{len(plan)} approved claim(s) to dismiss, {len(bad)} blocking")
    return (not bad), plan


def dismiss(paths, approved_path, db=None, apply=False, undo_dir=None, con=None):
    ok, plan = gate(paths, approved_path, db, con=con)
    if not ok:
        print("STOP: unapproved or invalid stale-flag claims — nothing dismissed")
        return 1
    if not plan:
        print("nothing to dismiss")
        return 0
    now = datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z")
    for base, sid, fid, why in plan:
        print(f"  {'dismiss' if apply else 'would dismiss'} flag {fid} on S{sid}: Note = "
              f"{(why + ' | lead-verified (' + base + ')')[:160]!r}")
    if not apply:
        print("(dry run — --apply writes)")
        return 0
    w = con or open_db(db, write=True)
    before = [dict(zip(("Id", "SeriesId", "ReviewState", "Note", "DecidedBy", "DecidedAt"), r)) for r in w.execute(
        f"SELECT Id, SeriesId, ReviewState, Note, DecidedBy, DecidedAt FROM ContainmentFlag WHERE Id IN "
        f"({','.join('?' * len(plan))})", [p[2] for p in plan])]
    ud = undo_dir or idbase.UNDO
    os.makedirs(ud, exist_ok=True)
    undo = os.path.join(ud, f"wave-stale-flags-{datetime.datetime.now().strftime('%Y%m%d-%H%M%S')}.jsonl")
    with open(undo, "w", encoding="utf-8") as f:
        for r in before:
            f.write(json.dumps({"table": "ContainmentFlag", "was": r}, ensure_ascii=False) + "\n")
    n = 0
    try:
        for base, sid, fid, why in plan:
            n += w.execute("UPDATE ContainmentFlag SET ReviewState = 'Dismissed', Note = ?, DecidedBy = 'identity-pass', "
                           "DecidedAt = ? WHERE Id = ? AND SeriesId = ? "
                           "AND (ReviewState IS NULL OR ReviewState IN ('', 'Pending', 'Open'))",
                           (f"{why} | lead-verified ({base})", now, fid, sid)).rowcount
        w.commit()
    except Exception:
        w.rollback()
        raise
    print(f"dismissed {n} flag(s); undo journal {undo}")
    return 0 if n == len(plan) else 1


def main():
    a = sys.argv[1:]

    def opt(name):
        return a[a.index(name) + 1] if name in a and a.index(name) + 1 < len(a) else None

    vals = {opt(x) for x in ("--approved", "--db", "--undo-dir") if opt(x)}
    names = [x for x in a if not x.startswith("--") and x not in vals]
    if not names or not ({"--gate", "--dismiss"} & set(a)):
        raise SystemExit(__doc__)
    paths = [idbase.resolve_decision_file(x) for x in names]
    approved = opt("--approved") or idbase.STALE_APPROVED
    if "--dismiss" in a:
        return dismiss(paths, approved, opt("--db"), "--apply" in a, opt("--undo-dir"))
    ok, _plan = gate(paths, approved, opt("--db"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
