"""Prove the two instruments before the pass leans on them: the packet is complete, the checker refuses.

`python selftest.py [--keep]`

Half of this file renders three real packets — a singleton, a mid-sized shelf carrying BOTH a CV volume and
a unanimous GCD series, and a shelf with an open `conflated-series` flag — and asserts what PLAN §7-S says
a packet must contain: every filename, folded only where the FOLD RULE allows, the `ours:` block, the leg
blocks, the arithmetic line.

The other half writes decision files that are wrong ON PURPOSE, one per rule, and asserts that
`check_identity.py` rejects each for the RIGHT reason. A checker that rejects everything is as useless as
one that rejects nothing; the assertion is on the message, not on the exit code. The last file is correct
and must pass.

Everything is written under docs/books/identity/selftest/ and nothing touches books.db.
"""
import os
import re
import subprocess
import sys

# the packets carry 'yearΔ' and '·'; a cp1252 console kills the run on the first one
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

import idbase
import identity_packet
import check_identity
from idbase import Evidence

OUT = os.path.join(idbase.ROOT, "selftest")
os.makedirs(OUT, exist_ok=True)
KEEP = "--keep" in sys.argv

ev = Evidence()
ctx = identity_packet.Ctx(ev)
con = ev.con

failures = []


def check(ok, label, detail=""):
    print(f"  {'pass' if ok else 'FAIL'}  {label}{('  — ' + detail) if detail and not ok else ''}")
    if not ok:
        failures.append(label)


# ── part 1: three packets ────────────────────────────────────────────────────────────────────────
singleton = next(s for s in ev.shelves if ev.size.get(s) == 1 and ev.series[s]["cvVolumeId"])
midsize = next(s for s in ev.shelves
               if 10 <= ev.size.get(s, 0) <= 30 and ev.series[s]["cvVolumeId"] and len(ev.gcd_ids(s)) == 1)
conflated = next(s for s in ev.shelves if ev.conflated_open(s))

print(f"packets: singleton S{singleton}, mid-size S{midsize}, conflated S{conflated}")
for label, sid in (("singleton", singleton), ("midsize", midsize), ("conflated", conflated)):
    lines = identity_packet.packet(sid, ev, ctx)
    text = "\n".join(lines)
    path = os.path.join(OUT, f"packet-{label}-S{sid}.txt")
    with open(path, "w", encoding="utf-8") as f:
        f.write(text + "\n")

    rows = con.execute("""SELECT i.Id, i.FileName, i.PageCount, coalesce(cd.IsCollection,0)
                          FROM Item i LEFT JOIN ComicDetail cd ON cd.ItemId = i.Id
                          WHERE i.SeriesId = ? AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0
                          ORDER BY i.Path, i.FileName""", (sid,)).fetchall()
    folded = identity_packet.fold_files([(r[0], r[1], r[2], r[3]) for r in rows])
    missing, wrongly_folded = [], []
    for kind, payload in folded:
        if kind == "full":
            if payload[1] not in text:
                missing.append(payload[1])
        else:
            if payload[0][1] not in text or payload[-1][1] not in text:
                missing.append(payload[0][1])
            if f"({len(payload)} files" not in text:
                wrongly_folded.append(payload[0][1])
            if len(payload) < 6:
                wrongly_folded.append(payload[0][1])
            if any(x[3] for x in payload):
                wrongly_folded.append("a collection was folded: " + payload[0][1])
    check(not missing, f"S{sid} {label}: every filename present (folded only per the FOLD RULE)",
          f"{len(missing)} missing, e.g. {missing[:2]}")
    check(not wrongly_folded, f"S{sid} {label}: no fold breaks the rule", str(wrongly_folded[:2]))
    check(text.startswith(f"== S{sid} "), f"S{sid} {label}: header names the shelf")
    check("[tier " in lines[0], f"S{sid} {label}: header carries the tier")
    check("   folders: " in text, f"S{sid} {label}: folders block")
    check("   files:   " in text, f"S{sid} {label}: files block")
    check("   ours:    " in text, f"S{sid} {label}: ours block (what the collection itself asserts)")

    if label == "singleton":
        check(len(lines) <= 8, f"S{sid} singleton: packet is {len(lines)} lines (budget 8 after compaction)")
        check("CV linked:" in text, f"S{sid} singleton: CV linked block")
        # the arithmetic no longer has a line of its own — it rides on the CV linked line
        cvline = next(l for l in lines if "CV linked:" in l)
        for piece in ("yearΔ", "ratio "):
            check(piece in cvline, f"S{sid} singleton: '{piece}' is on the CV linked line")
        check(not any(l.strip().startswith("arithmetic:") for l in lines),
              f"S{sid} singleton: no separate arithmetic line")
    if label == "midsize":
        check(len(lines) <= 12, f"S{sid} midsize: packet is {len(lines)} lines (budget 12 after compaction)")
        cvline = next(l for l in lines if "CV linked:" in l)
        for piece in ("cached #", "yearΔ", "ratio ", "judged"):
            check(piece in cvline, f"S{sid} midsize: '{piece}' is on the CV linked line")
        # every per-file v1 link names the stored volume on this shelf, so the block must collapse to a
        # statement of agreement rather than repeat the id
        check("v1 per-file: same volume on" in cvline and "CV per-file (v1)" not in text,
              f"S{sid} midsize: the agreeing v1 per-file block collapsed onto the linked line")
        check("GCD per-file:" in text, f"S{sid} midsize: GCD per-file block")
        check("methods " in text, f"S{sid} midsize: the GCD methods are printed")
        # the reader must still be able to tell the lookups RAN and found nothing new
        check(("(rip lookup)" in text or "(dump lookup)" in text or "CV lookups:" in text
               or "found nothing else" in text or "names no other series" in text),
              f"S{sid} midsize: the local lookups are accounted for either way")
        check("(v1 search)" in text or "CV candidates" not in text,
              f"S{sid} midsize: candidates are marked as v1's search, not as an answer")
        for vid in re.findall(r"\b(\d{3,6})\b", cvline)[:1]:
            others = [l for l in lines if l is not cvline and "CV " in l and f" {vid} " in l]
            check(not others, f"S{sid} midsize: the linked volume id is printed once, not repeated",
                  str(others[:1]))
    if label == "conflated":
        check("   flags: " in text and "conflated-series" in text,
              f"S{sid} conflated: the open flag is on the packet's face")
        check(identity_packet.packet(sid, ev, ctx)[0].find("[tier C]") > 0,
              f"S{sid} conflated: an open flag demotes the shelf to tier C")
    print(f"        -> {path}  ({len(lines)} lines)")

# ── part 1b: the lookups must probe every spelling on the packet, not only our filing name ──────
# S97993 is the reader's example: our shelf is filed `Jughead v2 (1987)`, which no provider calls
# anything, so both lookups returned nothing and the packet said so — about a title both providers hold
# under half a dozen ids. Stripping our own filing suffix is what finds them.
print("\nwider lookup probes")
# S97993 `Jughead v2 (1987)` was the reader's example. A landed wave can rename or re-key any shelf, so the
# subject is CHOSEN by the property under test — a shelf our own filing name finds nothing for — with
# S97993 preferred while it still qualifies. Pinning the id turned a fixed defect into a failing test the
# moment wave 2 re-keyed that shelf.
def _narrow_blind(sid):
    s_ = ev.series[sid]
    probes = [s_["name"]] + sorted(ev.keys.get(sid, ()))
    return not any(ctx.cv_index().get(idbase.norm_name(p)) or ctx.gcd_index().get(idbase.norm_name(p))
                   for p in probes)


PROBE_SID = 97993 if (97993 in ev.shelf_set and _narrow_blind(97993)) else next(
    (sid for sid in ev.shelves
     if _narrow_blind(sid) and identity_packet._variants(ev.series[sid]["name"])
     and identity_packet._dump_hits(ctx, ev, sid, ev.series[sid], sorted(ev.keys.get(sid, ())),
                                    ev.candidates(sid))), None)
if PROBE_SID in ev.shelf_set:
    s = ev.series[PROBE_SID]
    ks = sorted(ev.keys.get(PROBE_SID, ()))
    cands = ev.candidates(PROBE_SID)
    narrow_cv = ctx.cv_index().get(idbase.norm_name(s["name"]), [])
    narrow_gcd = ctx.gcd_index().get(idbase.norm_name(s["name"]), [])
    wide_cv = identity_packet._rip_hits(ctx, ev, PROBE_SID, s, ks, cands)
    wide_gcd = identity_packet._dump_hits(ctx, ev, PROBE_SID, s, ks, cands)
    check(not narrow_cv and not narrow_gcd,
          f"S{PROBE_SID}: the parsed key alone still finds nothing (that is the bug being fixed)")
    check(len(wide_cv) > 0, f"S{PROBE_SID}: the wider probe finds CV records ({len(wide_cv)})")
    check(len(wide_gcd) > 0, f"S{PROBE_SID}: the wider probe finds GCD records ({len(wide_gcd)})")
    check(all(via for _r, via in wide_cv) and all(via for _r, via in wide_gcd),
          f"S{PROBE_SID}: every extra hit says which spelling found it")
    txt = "\n".join(identity_packet.packet(PROBE_SID, ev, ctx))
    check("(via " in txt, f"S{PROBE_SID}: the packet prints the '(via …)' attribution")
    with open(os.path.join(OUT, f"packet-wider-probe-S{PROBE_SID}.txt"), "w", encoding="utf-8") as f:
        f.write(txt + "\n")
    print(f"        cv {[r[0] for r, _v in wide_cv]}  gcd {[r[0] for r, _v in wide_gcd]}")
    # ordering: the shelf's own spellings must come before anything a variant found
    order_ok = [i for i, (_r, via) in enumerate(wide_gcd) if not via]
    check(order_ok == list(range(len(order_ok))),
          f"S{PROBE_SID}: exact-name hits are ordered before the extra probes")

check("Jughead" in identity_packet._variants("Jughead v2 (1987)"),
      "the filing suffix stripper peels 'Jughead v2 (1987)' down to 'Jughead' (via 'Jughead v2')",
      str(identity_packet._variants("Jughead v2 (1987)")))
check("Archie and Me" in identity_packet._variants("Archie & Me"),
      "& swaps to and", str(identity_packet._variants("Archie & Me")))

# ── part 1c: a one-issue CV volume must carry its ISSUE id (TOOLS_TODO 12) ──────────────────────
print("\nissue ids on one-issue volumes")
one = next((sid for sid in ev.shelves
            if ev.series[sid]["cvVolumeId"] in ev.cv_volume
            and ev.cv_volume[ev.series[sid]["cvVolumeId"]]["countOfIssues"] == 1
            and ctx.cv_issue_ids(ev.series[sid]["cvVolumeId"], 1)), None)
if one:
    vid = ev.series[one]["cvVolumeId"]
    want = ctx.cv_issue_ids(vid, 1)[0][0]
    cvline = next(l for l in identity_packet.packet(one, ev, ctx) if "CV linked:" in l)
    check(f"issue {want}" in cvline,
          f"S{one}: the one-issue volume {vid} prints 'issue {want}' — the id an I line needs", cvline[-90:])
    many = next((sid for sid in ev.shelves
                 if ev.series[sid]["cvVolumeId"] in ev.cv_volume
                 and (ev.cv_volume[ev.series[sid]["cvVolumeId"]]["countOfIssues"] or 0) > 1), None)
    if many:
        line2 = next(l for l in identity_packet.packet(many, ev, ctx) if "CV linked:" in l)
        check(" issue " not in line2,
              f"S{many}: a multi-issue volume prints NO issue id (there is no single answer)", line2[-90:])
else:
    check(False, "found no shelf linked to a one-issue volume with a cached issue")

# ── part 1d: the stored-CvVolumeId collision rule must yield to a decided partner (TOOLS_TODO 10) ─
print("\nstored-CvVolumeId collision, narrowed")
pair = next(((sid, ev.series[sid]["cvVolumeId"]) for sid in ev.shelves
             if ev.series[sid]["cvVolumeId"] in ev.cv_volume), None)
if pair:
    partner, vid = pair
    other = next(sid for sid in ev.shelves if sid != partner and ev.keys.get(sid))
    # the volume the partner gets re-linked to must be one NO live shelf stores, or the fixture invents a
    # fresh collision of its own and tests the rule against the wrong thing
    _stored = {ev.series[s]["cvVolumeId"] for s in ev.shelves if ev.series[s]["cvVolumeId"]}
    free_vol = next(v for v in sorted(ev.cv_volume) if v not in _stored)
    LONGE = ("this shelf is the run that volume describes, and the two legs agree on start year and "
             "issue count, so the id belongs here")

    def _pair_files(tag, body, ids):
        with open(os.path.join(OUT, tag + ".ids"), "w", encoding="utf-8") as f:
            f.write("\n".join(str(i) for i in ids) + "\n")
        p = os.path.join(OUT, tag + ".txt")
        with open(p, "w", encoding="utf-8") as f:
            f.write("# selftest: collision fixture\n" + body)
        return p

    # (a) partner NOT decided -> the collision stands
    a = _pair_files("A-910", f"S {other} cv={vid} gcd=- 0.9 | {LONGE}\n", [other])
    r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "check_identity.py"), "--all", a],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    check("ALREADY the stored CvVolumeId" in r.stdout,
          f"an undecided partner S{partner} still raises the collision", r.stdout[-300:])
    # (b) partner re-linked elsewhere in the same wave -> no collision to declare
    b = _pair_files("A-911",
                    f"S {other} cv={vid} gcd=- 0.9 | {LONGE}\n"
                    f"S {partner} cv={free_vol} gcd=- 0.9 | {LONGE}\n",
                    [other, partner])
    r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "check_identity.py"), "--all", b],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    check("ALREADY the stored CvVolumeId" not in r.stdout,
          f"a partner re-linked to a different volume removes the collision", r.stdout[-300:])
    # (c) partner refused -> likewise
    c = _pair_files("A-912",
                    f"S {other} cv={vid} gcd=- 0.9 | {LONGE}\n"
                    f"R {partner} | nothing corroborates any candidate for this shelf and its stored link "
                    f"describes a different comic\n",
                    [other, partner])
    r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "check_identity.py"), "--all", c],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    check("ALREADY the stored CvVolumeId" not in r.stdout,
          f"a refused partner removes the collision", r.stdout[-300:])

# ── part 1e: an R + wrong-cv-link CLEARS the stored link; an R alone does not (wave 2's ten) ────
print()
print("the clear")
_clr = next(((sid, ev.series[sid]["cvVolumeId"]) for sid in ev.shelves
             if ev.series[sid]["cvVolumeId"] and ev.keys.get(sid) and not ev.conflated_open(sid)), None)
if _clr:
    csid, cvol = _clr
    WHY = ("the stored ComicVine link on this shelf describes a different comic and nothing here "
           "corroborates any other candidate, so no identity can be stored")

    NL = "\n"

    def _clear_case(tag, body):
        with open(os.path.join(OUT, tag + ".ids"), "w", encoding="utf-8") as f:
            f.write(str(csid) + NL)
        p2 = os.path.join(OUT, tag + ".txt")
        with open(p2, "w", encoding="utf-8") as f:
            f.write("# selftest: clear fixture" + NL + body)
        return subprocess.run([sys.executable, os.path.join(idbase.HERE, "apply_identity.py"), p2],
                              capture_output=True, text=True, encoding="utf-8", errors="replace").stdout

    out_with = _clear_case("A-920",
                           f"R {csid} | {WHY}" + NL
                           + f"F {csid} wrong-cv-link | the stored cv {cvol} is the record of a "
                             f"different run entirely" + NL)
    check("] clear " in out_with and "-> NULL/Cleared" in out_with,
          f"S{csid}: R + wrong-cv-link produces a clear of the stored cv {cvol}", out_with[-400:])
    check("SeriesKeyLink CLEARED   : 0" not in out_with,
          f"S{csid}: the clear is counted in the summary", out_with[-200:])

    out_without = _clear_case("A-921", f"R {csid} | {WHY}" + NL)
    check("] clear " not in out_without,
          f"S{csid}: a bare R with no wrong-cv-link clears NOTHING — a refusal is not a claim that the "
          f"stored link is wrong", out_without[-400:])

# ── part 2: batches that must be rejected, one rule each ────────────────────────────────────────
print("\nbad batches — each must be rejected for its own rule")

# real ids to build the files out of: nothing here may fail for being unreal
good_a = next(s for s in ev.shelves if ev.series[s]["cvVolumeId"] in ev.cv_volume
              and not ev.conflated_open(s) and ev.keys.get(s))
good_b = next(s for s in ev.shelves if s != good_a and ev.series[s]["cvVolumeId"] in ev.cv_volume
              and ev.series[s]["cvVolumeId"] != ev.series[good_a]["cvVolumeId"]
              and not ev.conflated_open(s) and ev.keys.get(s))
vol_a, vol_b = ev.series[good_a]["cvVolumeId"], ev.series[good_b]["cvVolumeId"]
E = ("CV volume and the GCD series agree on the 1998 start year and the run length, and our "
     "filenames number 1-12 with no gap")
assert len(E) >= 40

CASES = [
    ("bad-missing-shelf", [good_a, good_b],
     f"S {good_a} cv={vol_a} gcd=- 0.9 | {E}\n",
     "have no S or R line"),
    ("bad-no-id", [good_a],
     f"S {good_a} cv=- gcd=- 0.9 | {E}\n",
     "names no id"),
    ("bad-confidence", [good_a],
     f"S {good_a} cv={vol_a} gcd=- 0.85 | {E}\n",
     "bad confidence"),
    ("bad-conflated", [conflated],
     f"S {conflated} cv={vol_a} gcd=- 0.9 | {E}\n",
     # the checker now refuses on `overlap-in-series` too and says so in the same sentence, so the
     # assertion is on the part of the message that is the RULE, not on its current wording
     "OPEN conflated-series"),
    ("bad-shared-cv", [good_a, good_b],
     f"S {good_a} cv={vol_a} gcd=- 0.9 | {E}\nS {good_b} cv={vol_a} gcd=- 0.9 | {E}\n",
     f"share cv={vol_a}"),
    ("bad-short-evidence", [good_a],
     f"S {good_a} cv={vol_a} gcd=- 0.9 | name matches\n",
     "evidence clause is"),
    ("bad-unknown-cv", [good_a],
     f"S {good_a} cv=999999999 gcd=- 0.9 | {E}\n",
     "unknown ComicVine volume id"),
]

ck = check_identity.Checker()
for name, ids, body, expect in CASES:
    with open(os.path.join(OUT, name + ".ids"), "w", encoding="utf-8") as f:
        f.write("\n".join(str(i) for i in ids) + "\n")
    path = os.path.join(OUT, name + ".txt")
    with open(path, "w", encoding="utf-8") as f:
        f.write(f"# selftest: this file is wrong on purpose — {expect}\n" + body)
    errors = []
    check_identity.parse(path, ck, errors)
    hit = [e for e in errors if expect in e]
    check(bool(hit), f"{name}: rejected for '{expect}'",
          f"errors were: {errors or '(none — the checker ACCEPTED a bad batch)'}")
    if hit:
        print(f"        {hit[0]}")

# ── part 3: the batch that must pass ─────────────────────────────────────────────────────────────
print("\ngood batch — must be accepted")
# a third shelf whose files carry a ComicVine ISSUE id we hold, so the good batch exercises the 0.7 review
# sink and the `I` item line as well as the 0.9 link — apply_identity's dry run is read against this file.
row = con.execute("""SELECT i.SeriesId, i.Id, l.ProviderKey FROM ItemProviderLink l JOIN Item i ON i.Id = l.ItemId
                     JOIN CvIssue ci ON ci.Id = cast(l.ProviderKey AS INTEGER)
                     WHERE l.Provider = 0 AND l.Status = 1 AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0
                       AND i.SeriesId IS NOT NULL LIMIT 1""").fetchone()
good_c, item_c, issue_c = (row if row else (None, None, None))
if good_c in (good_a, good_b) or good_c not in ev.shelf_set or ev.conflated_open(good_c or -1):
    good_c = None

good = os.path.join(OUT, "good-batch.txt")
ids = [good_a, good_b] + ([good_c] if good_c else [])
with open(os.path.join(OUT, "good-batch.ids"), "w", encoding="utf-8") as f:
    f.write("\n".join(str(i) for i in ids) + "\n")
with open(good, "w", encoding="utf-8") as f:
    f.write("# selftest: the shape a real decision file takes — one shelf linked, one refused, one held at\n"
            "# 0.7 for the review queue, each clause naming what agreed rather than that something matched.\n"
            f"S {good_a} cv={vol_a} gcd=- 0.9 | the stored CV volume {vol_a} carries our start year and our "
            f"issue count, and the filenames number continuously with no second #1\n"
            f"R {good_b} | CV volume {vol_b} is the only candidate and its issue list stops below the numbers "
            f"we hold, so nothing here is corroborated by a second leg\n")
    if good_c:
        f.write(f"S {good_c} cv={ev.series[good_c]['cvVolumeId'] or '-'} gcd=- 0.7 | one leg only and the "
                f"publisher folder does not corroborate it, so this is held for review rather than linked\n"
                f"I {item_c} cv={issue_c} gcd=- 0.7 | the file's own ComicInfo names this ComicVine issue and "
                f"the page count agrees, but no second source was consulted\n")
errors = []
out = check_identity.parse(good, ck, errors)
check(not errors, "good-batch: accepted with 0 failures", str(errors[:3]))
check(len(out["S"]) == (2 if good_c else 1) and len(out["R"]) == 1,
      "good-batch: parsed every S and R line")

r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "check_identity.py"), good],
                   capture_output=True, text=True, encoding='utf-8', errors='replace')
check(r.returncode == 0, "good-batch: check_identity.py CLI exits 0", r.stdout[-400:])
print("   " + "\n   ".join(r.stdout.strip().splitlines()))

# ── part 4: precedence — an R- file supersedes an earlier decision for the shelves it names ─────
print("\nprecedence — a revisit supersedes, two tier batches do not")


def write_pair(name, body, ids):
    with open(os.path.join(OUT, name + ".ids"), "w", encoding="utf-8") as f:
        f.write("\n".join(str(i) for i in ids) + "\n")
    p = os.path.join(OUT, name + ".txt")
    with open(p, "w", encoding="utf-8") as f:
        f.write(f"# selftest: precedence fixture\n{body}")
    return p


LONG = ("the ruling was sharpened after this shelf was first read, so it is re-decided here and the "
        "original line stays on disk as the audit trail")
a900 = write_pair("A-900", f"S {good_a} cv={vol_a} gcd=- 0.9 | {E}\n", [good_a])
r900 = write_pair("R-900", f"S {good_a} cv={vol_a} gcd=- 0.7 | {LONG}\n", [good_a])
a901 = write_pair("A-901", f"S {good_a} cv={vol_a} gcd=- 0.9 | {E}\n", [good_a])

_d, win, sup, dup = idbase.scan_decisions([a900, r900])
check(win.get(good_a) == r900, f"S{good_a}: the R- file wins over the tier batch",
      str(os.path.basename(win.get(good_a, ""))))
check(sup.get(good_a) == [a900], f"S{good_a}: the tier batch is recorded as superseded, not dropped")
check(not dup, "a revisit is not counted as a duplicate decision")

_d, _w, _s, dup2 = idbase.scan_decisions([a900, a901])
check(good_a in dup2, "two NON-revisit files deciding one shelf IS a duplicate")

r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "check_identity.py"), "--all", a900, a901],
                   capture_output=True, text=True, encoding='utf-8', errors='replace')
check(r.returncode != 0 and "decided in two non-revisit files" in r.stdout,
      "check_identity --all fails on two tier batches deciding one shelf", r.stdout[-300:])

r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "apply_identity.py"), a900, r900],
                   capture_output=True, text=True, encoding='utf-8', errors='replace')
check("superseded:" in r.stdout and "R-900" in r.stdout,
      "apply_identity names the file that superseded the earlier line", r.stdout[-400:])
check("[R-900] review" in r.stdout and "[A-900] link" not in r.stdout,
      "apply_identity applies the revisit's 0.7 review and NOT the superseded 0.9 link", r.stdout[-600:])
check("SeriesKeyLink rows      : 0" in r.stdout,
      "apply_identity writes no link at all once the revisit lowered the confidence", r.stdout[-400:])
print("   " + "\n   ".join(l for l in r.stdout.strip().splitlines() if "==" in l or "superseded" in l
                           or "] link" in l or "] review" in l))

# ── part 5: the `C` line — what a book collects, and OF WHICH RUN (SPAN_RUN_IDS.md) ─────────────
# A `C` line writes a Curated span, and a Curated span is what the file de-duplication acts on, so the
# grammar is tested the same way as the rest: one file that must be accepted and landed (dry), and one
# wrong file per rule.
print("\nthe C line — collects #a-b of which run")
c_sid = c_item = c_vol = None
for _sid, _iid in con.execute(
        """SELECT i.SeriesId, i.Id FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
           WHERE cd.IsCollection = 1 AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0
             AND i.SeriesId IS NOT NULL
             AND NOT EXISTS (SELECT 1 FROM CollectedEditionSpan s WHERE s.ItemId = i.Id AND s.Source = 3)
           LIMIT 4000"""):
    if (_sid in ev.shelf_set and not ev.conflated_open(_sid) and ev.keys.get(_sid)
            and ev.series[_sid]["cvVolumeId"] in ev.cv_volume):
        c_sid, c_item, c_vol = _sid, _iid, ev.series[_sid]["cvVolumeId"]
        break

if c_item is None:
    check(False, "found no collected edition without a Curated span to test the C line on")
else:
    CE = (f"the trade's copyright page names ComicVine volume {c_vol} and the four issues it collects, and "
          f"its page count matches those four at 22pp apiece")
    CWHY = ("CV volume and the GCD series agree on the 1998 start year and the run length, and our "
            "filenames number 1-12 with no gap")

    def c_file(tag, body, ids=None):
        with open(os.path.join(OUT, tag + ".ids"), "w", encoding="utf-8") as f:
            f.write("\n".join(str(i) for i in (ids or [c_sid])) + "\n")
        p = os.path.join(OUT, tag + ".txt")
        with open(p, "w", encoding="utf-8") as f:
            f.write("# selftest: the C line\n" + body)
        return p

    GOODC = (f"S {c_sid} cv={c_vol} gcd=- 0.9 | {CWHY}\n"
             f"C {c_item} cv={c_vol} gcd=- #1-4 0.95 | {CE}\n")
    good_c_path = c_file("C-930", GOODC)
    errors = []
    outc = check_identity.parse(good_c_path, ck, errors)
    check(not errors, f"C-930: a good C line is accepted ({len(outc['C'])} C)", str(errors[:3]))
    check(len(outc["C"]) == 1 and outc["C"][0][0] == c_item and outc["C"][0][1] == {"cv": str(c_vol)}
          and outc["C"][0][2] == 1 and outc["C"][0][3] == 4,
          "C-930: the line parses into (item, {leg: key}, a, b)", str(outc["C"][:1]))
    _d5, _w5, _s5, _dup5 = idbase.scan_decisions([good_c_path])
    check(_d5[good_c_path]["collects"].get(c_item) == [({"cv": str(c_vol)}, 1.0, 4.0, "0.95")],
          "C-930: scan_decisions records it under 'collects' as a LIST (one entry per (leg, run))",
          str(_d5[good_c_path]["collects"]))

    r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "apply_identity.py"), good_c_path],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    check("] collect " in r.stdout and f"#1-4 of cv={c_vol}" in r.stdout,
          "C-930: the apply dry run plans the Curated span and names the run", r.stdout[-500:])
    check("CollectedEditionSpan    : 1 inserted" in r.stdout,
          "C-930: exactly one span is inserted (the book had none)", r.stdout[-700:])
    check("CollectedEditionSpanRun : 1" in r.stdout,
          "C-930: and one run ref goes with it", r.stdout[-700:])
    check("(dry run — nothing written" in r.stdout, "C-930: it is a DRY run", r.stdout[-200:])
    print("   " + "\n   ".join(l for l in r.stdout.splitlines() if "] collect" in l or "CollectedEditionSpan" in l))

    C_CASES = [
        ("bad-c-unknown-id",
         f"S {c_sid} cv={c_vol} gcd=- 0.9 | {CWHY}\nC {c_item} cv=999999999 gcd=- #1-4 0.95 | {CE}\n",
         "unknown ComicVine volume id"),
        ("bad-c-descending",
         f"S {c_sid} cv={c_vol} gcd=- 0.9 | {CWHY}\nC {c_item} cv={c_vol} gcd=- #5-1 0.95 | {CE}\n",
         "is not ascending"),
        ("bad-c-twice",
         f"S {c_sid} cv={c_vol} gcd=- 0.9 | {CWHY}\nC {c_item} cv={c_vol} gcd=- #1-4 0.95 | {CE}\n"
         f"C {c_item} cv={c_vol} gcd=- #5-8 0.95 | {CE}\n",
         "one C line per (book, leg, run)"),
        ("bad-c-no-run",
         f"S {c_sid} cv={c_vol} gcd=- 0.9 | {CWHY}\nC {c_item} cv=- gcd=- #1-4 0.95 | {CE}\n",
         "names no run"),
        ("bad-c-no-range",
         f"S {c_sid} cv={c_vol} gcd=- 0.9 | {CWHY}\nC {c_item} cv={c_vol} gcd=- 0.95 | {CE}\n",
         "no #<a>-<b> range"),
    ]
    for name, body, expect in C_CASES:
        p = c_file(name, body)
        errors = []
        check_identity.parse(p, ck, errors)
        hit = [e for e in errors if expect in e]
        check(bool(hit), f"{name}: rejected for '{expect}'",
              f"errors were: {errors or '(none — the checker ACCEPTED a bad C line)'}")
        if hit:
            print(f"        {hit[0]}")

    # ── part 5b: SEVERAL `C` lines for one book — a trade of two minis, an omnibus (TOOLS_TODO 17) ──
    print("\nseveral C lines for one book — one per (leg, run), each in ITS run's numbering")
    second = vol_b if vol_b != c_vol else vol_a
    CE2 = (f"the second mini this book collects is ComicVine volume {second}, whose five issues are the "
           f"back half of the book by page count and by the copyright page's second block")
    multi = c_file("C-940",
                   f"S {c_sid} cv={c_vol} gcd=- 0.9 | {CWHY}\n"
                   f"C {c_item} cv={c_vol} #1-4 0.95 | {CE}\n"
                   f"C {c_item} cv={second} #1-5 0.9 | {CE2}\n")
    errors = []
    outm = check_identity.parse(multi, ck, errors)
    check(not errors, "C-940: two C lines for one book, on DIFFERENT runs, are accepted", str(errors[:3]))
    check(len(outm["C"]) == 2, "C-940: both lines parse", str(outm["C"]))
    _d6, _w6, _s6, _dup6 = idbase.scan_decisions([multi])
    check(len(_d6[multi]["collects"].get(c_item, [])) == 2,
          "C-940: scan_decisions keeps both", str(_d6[multi]["collects"]))

    r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "apply_identity.py"), multi],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    check("CollectedEditionSpanRun : 2" in r.stdout,
          "C-940: TWO run refs are planned — one per run, each with its own range", r.stdout[-700:])
    check("CollectedEditionSpan    : 1 inserted" in r.stdout,
          "C-940: and ONE span, whose range is the FIRST line's", r.stdout[-700:])
    check("run ref only (a second run of this book)" in r.stdout,
          "C-940: the second line is named as what it is", r.stdout[-900:])
    print("   " + "\n   ".join(l for l in r.stdout.splitlines()
                               if "] collect" in l or "CollectedEditionSpan" in l))

    same_run = c_file("C-941",
                      f"S {c_sid} cv={c_vol} gcd=- 0.9 | {CWHY}\n"
                      f"C {c_item} cv={c_vol} #1-4 0.95 | {CE}\n"
                      f"C {c_item} cv={c_vol} gcd=- #5-8 0.9 | {CE2}\n")
    errors = []
    check_identity.parse(same_run, ck, errors)
    check(any("one C line per (book, leg, run)" in e for e in errors),
          "C-941: the SAME run twice is refused — two answers to one question", str(errors[:3]))

# ── part 6: the ITEM batch (`X-NNN`) — books, not shelves (TOOLS_TODO 16) ───────────────────────
print("\nthe X- item batch — I / C / N over BOOKS on a shelf whose S already stands")
x_item = x_other = None
for _sid, _iid in con.execute(
        """SELECT i.SeriesId, i.Id FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
           WHERE cd.IsCollection = 1 AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0
             AND i.SeriesId IS NOT NULL LIMIT 4000"""):
    if _sid not in ev.shelf_set:
        continue
    if x_item is None:
        x_item = _iid
    elif _iid != x_item:
        x_other = _iid
        break

if x_item is None or x_other is None:
    check(False, "found no two collected editions to build an item batch from")
else:
    XE = ("the book's own ComicVine issue record, named on its copyright page and matching our rip's page "
          "count to within four pages")
    XN = ("no record of this book exists in either catalogue: the dump holds the print line only and the "
          "rip has no volume of this name in any year")

    def x_file(tag, body, ids):
        with open(os.path.join(OUT, tag + ".ids"), "w", encoding="utf-8") as f:
            f.write("\n".join(str(i) for i in ids) + "\n")
        p = os.path.join(OUT, tag + ".txt")
        with open(p, "w", encoding="utf-8") as f:
            f.write("# selftest: an item batch\n" + body)
        return p

    covered = x_file("X-900",
                     f"I {x_item} cv=- gcd=- isbn=9781506713434 0.9 | {XE}\n"
                     f"N {x_other} no-record | {XN}\n",
                     [x_item, x_other])
    errors = []
    outx = check_identity.parse(covered, ck, errors)
    check(not errors, "X-900: an I line and an explicit no-record cover the batch", str(errors[:3]))
    check(outx["kind"] == "X" and outx["no_record"] == {x_other},
          "X-900: it is read AS an item batch and the refusal is recorded", str(outx["kind"]))

    bare = x_file("X-901", f"I {x_item} cv=- gcd=- isbn=9781506713434 0.9 | {XE}\n", [x_item, x_other])
    errors = []
    check_identity.parse(bare, ck, errors)
    check(any("no I line and no" in e and str(x_other) in e for e in errors),
          "X-901: a book left unanswered fails the batch", str(errors[:3]))

    silent = x_file("X-902", f"N {x_other} no-record | {XN}\n", [x_item, x_other])
    errors = []
    check_identity.parse(silent, ck, errors)
    check(any("no I line and no" in e for e in errors),
          "X-902: and a `C`-only or note-only answer is not coverage either", str(errors[:3]))

    short = x_file("X-903", f"I {x_item} cv=- gcd=- isbn=9781506713434 0.9 | {XE}\n"
                            f"N {x_other} no-record | not found\n", [x_item, x_other])
    errors = []
    check_identity.parse(short, ck, errors)
    check(any("no-record needs a reason" in e for e in errors),
          "X-903: a refusal must say what was looked for", str(errors[:3]))

    r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "apply_identity.py"), covered],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    check("] isbn " in r.stdout and "SeriesKeyLink rows      : 0" in r.stdout,
          "X-900: apply lands its item links and touches no shelf", r.stdout[-700:])

# ── part 7: the ledger moved out of the brief, and nothing was lost (TOOLS_TODO 20) ─────────────────
# The brief as it stood BEFORE the move is read back out of git at the last commit that carried the ledger,
# re-cut by the same parser the migration used, and compared entry for entry with LEDGER.md: same count,
# same order, the same text byte for byte. Then every entry must be REACHABLE — carry a tag some shelf can
# produce and match a batch carrying that tag — or be a ruling the brief now carries verbatim.
import ledger

print("\nthe conventions ledger — moved, tagged, every entry reachable")
PRE_MOVE_COMMIT = "68c2f5ad"
entries = ledger.load()
try:
    old = subprocess.run(["git", "-C", idbase.ROOT, "show", f"{PRE_MOVE_COMMIT}:docs/books/identity/READER_BRIEF.md"],
                         capture_output=True, text=True, encoding="utf-8", errors="replace")
    old_text = old.stdout if old.returncode == 0 else None
except OSError:
    old_text = None
if old_text is None:
    check(False, f"the pre-move brief is readable from git ({PRE_MOVE_COMMIT})", "git show failed")
else:
    before = ledger.parse_brief_ledger(old_text)
    check(len(before) == len(entries) and len(before) > 300,
          f"LEDGER.md holds every entry the brief held ({len(before)} before, {len(entries)} after)")
    changed = [k for k, (a, b) in enumerate(zip(before, entries), 1) if a != b["text"]]
    check(not changed, "every entry's text is verbatim, in order", f"differs at L-{changed[:5]}")
brief = open(ledger.BRIEF, encoding="utf-8").read()
check("## Conventions ledger" not in brief and len(brief.encode("utf-8")) < 20000,
      f"the brief no longer carries the ledger ({len(brief.encode('utf-8')):,} bytes)")
vocab = ledger.vocabulary()
untagged = [e["id"] for e in entries if not e["tags"]]
check(not untagged, "every entry carries at least one tag", str(untagged[:5]))
bad_tags = sorted({t for e in entries for t in e["tags"] if t not in vocab and t != "ruling"})
check(not bad_tags, "every tag is one a shelf or a batch kind can produce", str(bad_tags[:8]))
unreachable = []
for e in entries:
    if "ruling" in e["tags"]:
        if e["text"] not in brief:
            unreachable.append(e["id"] + " (ruling not in the brief)")
        continue
    why = ledger.unreachable_reason(e)
    if why:
        unreachable.append(f"{e['id']} ({why})")
check(not unreachable, "every entry is reachable — some shelf could match it, or it is a ruling the brief "
                       "carries verbatim", str(unreachable[:6]))


def _m(tags, text, stags, hay):
    return ledger.match_shelf({"id": "L-000", "tags": set(tags), "text": text}, set(stags), hay.lower())


BATMAN = "- The per-file matcher stamps Batman (1940) across every modern Batman relaunch shelf."
check(_m({"pub:dc", "sig:legacy-stamp"}, BATMAN, {"pub:dc"}, "dc\\batman (2016) | batman") and
      not _m({"pub:dc", "sig:legacy-stamp"}, BATMAN, {"pub:dc"}, "vertigo\\sandman (1989) | the sandman") and
      not _m({"pub:dc", "sig:legacy-stamp"}, BATMAN, {"pub:image", "sig:legacy-stamp"}, "image\\batman | batman"),
      "a big-house entry needs its house AND a keyword of its own on the shelf (Batman, not Sandman)")
check(_m({"pub:dynamite", "sig:trade-link"}, "- Dynamite: the count-1 record a year after every mini.",
         {"pub:dynamite", "sig:trade-link"}, "dynamite\\foo (2012)") and
      not _m({"pub:dynamite", "sig:trade-link"}, "- Dynamite: the count-1 record a year after every mini.",
             {"pub:dynamite", "sig:probe"}, "dynamite\\foo (2012)"),
      "a house-wide lesson about one signal needs that signal (a near-universal one does not count)")
check(_m({"pub:marvel", "folder:variant-covers"}, "x", {"pub:marvel", "folder:variant-covers"}, "") and
      not _m({"pub:marvel", "folder:variant-covers"}, "x", {"pub:marvel"}, ""),
      "a publisher + folder-shape entry needs both")
check(_m({"pub:ac"}, "- AC Comics: folder shape", {"pub:ac"}, "ac comics\\x"),
      "a small house matches on the house alone")
check(_m({"sig:round2"}, "x", {"sig:round2"}, "") and not _m({"ruling", "sig:round2"}, "x", {"sig:round2"}, "")
      and not _m({"tier:A", "pass:item"}, "x", {"tier:A", "pass:item"}, ""),
      "a topic entry matches its signal; a ruling is never re-attached; tier:/pass: alone never qualify")
_tagger = ledger.ShelfTagger(ev)
blk = ledger.block_for(ev, [midsize], "A", tagger=_tagger, entries=entries)
check(blk[0].startswith("## Conventions for this batch") and any(l.startswith("[L-") for l in blk),
      f"a batch block for S{midsize} opens the file and carries tagged entries", blk[0][:120])
_big = [s for s in ev.shelves if ev.series[s]["name"] and _tagger.tags(s) & {"pub:marvel", "pub:dc"}][:150]
blk = ledger.block_for(ev, _big, "A", tagger=_tagger, entries=entries)
_shown = sum(1 for l in blk if l.startswith("[L-"))
_bytes = len("\n".join(blk).split("\n+")[0].encode("utf-8"))
check(_shown <= ledger.CAP_ENTRIES and _bytes <= ledger.CAP_BYTES + 1500 and any(l.startswith("+") for l in blk),
      f"a 150-shelf DC/Marvel block is capped ({_shown} entries, {_bytes:,} bytes) and names the rest at its foot")
_body = "== S1 x\n   files: a\n\n== S2 y\n"
_text = "\n".join(blk) + "\n" + _body
check(ledger.split_block(_text)[1] == _body, "split_block hands back the packet body byte for byte")

# ── part 8: GCD's own "Collects …" notes (TOOLS_TODO 18 + 21) ──────────────────────────────────────────
import gcdnotes

print("\nGCD notes — the parser and the contradiction shapes")
p = gcdnotes.parse_notes("Collects Batman / Superman (DC, 2019 series) #7-15 and Batman / Superman Annual "
                         "(DC, 2020 series) #1.\n\nSecond printing available #2.")
check([e["ranges"] for e in p] == [[(7.0, 15.0)], [(1.0, 1.0)]],
      "two series, two ranges; the printing paragraph is not read", str(p))
check(gcdnotes.parse_notes("Collects [gcd_link_series](3201) #1-5.")[0]["sid"] == 3201,
      "a linked series id is carried")
p1 = gcdnotes.parse_notes("Collects Bloodshot (Valiant, 2019 series) #1-6")
check(gcdnotes.contradiction((1, 6), p1) is None, "a judged range the notes state fits")
check((gcdnotes.contradiction((1, 4), p1) or ("",))[0] == "count", "a shorter judged range is a COUNT contradiction")
check((gcdnotes.contradiction((103, 108), p1) or ("",))[0] == "offset",
      "the same count in other numbers is an OFFSET, not a count")
check(gcdnotes.contradiction((103, 108), p1, runs=[(1, 6)]) is None,
      "an offset the book's run row already states is answered")

# ── part 9: lookup --batch, next_batch --out, triage_09 chunks (TOOLS_TODO 22 / 20 / 23) ──────────────
print("\nthe new drivers — one call for many lookups, emission that emits nothing, a chunked triage")
qf = os.path.join(OUT, "lookup-batch.txt")
with open(qf, "w", encoding="utf-8") as f:
    f.write('# two spellings and an issue list\n"Gen 13" --year 1994\n"Heavy Metal Magazine"\n--collects 2213270\n')
r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "lookup.py"), "--batch", qf],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
check(r.stdout.count(">>> ") == 3 and "3 queries" in r.stdout, "lookup --batch answers every query under its own head",
      r.stdout[-300:])
import hashlib


def _snapshot():
    """state.json's hash and the batches/ listing (name, size) — what a real emission changes."""
    h = hashlib.sha256(open(idbase.STATE, "rb").read()).hexdigest()
    return h, sorted((f, os.path.getsize(os.path.join(idbase.BATCHES, f))) for f in os.listdir(idbase.BATCHES))


# A `--help` probe once emitted the real batch A-035 (the script read every unknown flag as "emit tier A").
# Each of these must write NOTHING to batches/ or state.json.
before = _snapshot()
for argv, want in ((["--help"], "Hand the reader"), ([], "name a mode"), (["--bogus"], "unknown option"),
                   (["--tier", "A", "--dry-run"], "'tier': 'A'")):
    r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "next_batch.py")] + argv,
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    after = _snapshot()
    check(after == before and want in (r.stdout + r.stderr),
          f"next_batch {' '.join(argv) or '(bare)'}: says so and leaves state.json + batches/ untouched",
          (r.stdout + r.stderr)[-300:])
state_before = open(idbase.STATE, "rb").read()
outdir = os.path.join(OUT, "emit")
r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "next_batch.py"), "--revisit", str(midsize),
                    "--out", outdir], capture_output=True, text=True, encoding="utf-8", errors="replace")
emitted = [f for f in os.listdir(outdir) if f.endswith(".txt")] if os.path.isdir(outdir) else []
check(open(idbase.STATE, "rb").read() == state_before and emitted,
      "next_batch --out writes the batch into the scratch dir and leaves state.json alone", r.stdout[-300:])
if emitted:
    head = open(os.path.join(outdir, emitted[0]), encoding="utf-8").readline()
    check(head.startswith("## Conventions for this batch"), "an emitted batch opens with its conventions block", head)
r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "triage_09.py"), "--limit", "40"],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
check("'processed': 40" in r.stdout and "'nextCursor'" in r.stdout and "'remaining'" in r.stdout,
      "triage_09 does a bounded chunk and prints {processed, remaining, nextCursor, counts}", r.stdout[-400:])


# ── part 10: GCD lookups and the stamped-row detector (TOOLS_TODO 24 + 25) ─────────────────────────────
print("\nGCD issue rows on demand, and a stored GCD row that is another book")
r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "lookup.py"), "--gcd-issues", "32012"],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
check("6 row(s)" in r.stdout and "Bright New Mourning" in r.stdout and "0-7851-1060-7" in r.stdout
      and r.stdout.index("Hope") < r.stdout.index("Bright New Mourning"),
      "lookup --gcd-issues lists a series' rows in number order with ISBNs and titles", r.stdout[-400:])
r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "lookup.py"), "--gcd-series", "Dark Knights of Steel",
                    "--limit", "2"], capture_output=True, text=True, encoding="utf-8", errors="replace")
check("series for 'dark knights of steel'" in r.stdout and "collects: Dark Knights of Steel" in r.stdout
      and "variant row(s) hidden" in r.stdout,
      "lookup --gcd-series resolves the name, prints each series' rows and their Collects clause", r.stdout[-400:])
_stamp = {"series": "Harley Quinn", "number": "2", "title": "Friends with Detriments", "pages": 220,
          "keyDate": "2026-03-03"}
_w = gcdnotes.stamp_reasons("Harley Quinn Vol. 01 - Hot in the City (2014) (Digital) (Zone-Empire).cbr", 223, _stamp)
check(sum(w for w, _t in _w) >= gcdnotes.STAMP_SCORE and any("vol 1 vs GCD #2" in t for _w2, t in _w),
      "a Vol. 01 file carrying GCD's Vol. 2 of a 2025 series is a stamped row", str(_w))
_fit = {"series": "Wonder Woman", "number": "3", "title": "The Truth", "pages": 180, "keyDate": "2017-10-00"}
_w = gcdnotes.stamp_reasons("Wonder Woman Vol. 03 - The Truth (2017) (digital) (Son of Ultron-Empire).cbr", 170, _fit)
check(sum(w for w, _t in _w) < gcdnotes.STAMP_SCORE, "the book's own row is not a stamp", str(_w))
_w = gcdnotes.stamp_reasons("Wonder Woman by George Perez Vol. 01 (2016) (digital).cbr", 347,
                            {"series": "Wonder Woman by George Pérez", "number": "1", "title": "", "pages": 356,
                             "keyDate": "2016-10-00"})
check(not _w, "an accent (Pérez / Perez) is not a different name", str(_w))
_w = gcdnotes.stamp_reasons("Green Arrow Vol. 01 - The Death & Life of Oliver Queen (2017) (digital).cbr", 67,
                            {"series": "Green Arrow", "number": "1", "title": "The Death and Life of Oliver Queen",
                             "pages": 164, "keyDate": "2017-03-00"})
check(sum(w for w, _t in _w) < gcdnotes.STAMP_SCORE, "a page gap ALONE (a partial rip) does not mark a row", str(_w))

# ── part 11: the split lane (TOOLS_TODO 27) ───────────────────────────────────────────────────────────
import json
import splitbase

print("\nthe split lane — P- packets, P- decisions, and the checker that stands before books-series-split")
check(splitbase.normalize_key("The Uncanny X-Men") == "uncanny x men"
      and splitbase.normalize_key("  Jim Butcher's The Dresden Files - Storm Front v2 (2009) ")
      == "jim butcher s the dresden files storm front v2 2009"
      and splitbase.normalize_key("Æon ½") == "æon",
      "normalize_key is SeriesResolver.NormalizeKey (one leading 'the', letters + decimal digits only)")
split_pop = splitbase.population(ev)
SPLIT_SID = 9845
check(SPLIT_SID in split_pop, f"S{SPLIT_SID} (Storm Front, R-029) is in the split population",
      f"{len(split_pop)} shelves")
blk = splitbase.packet(SPLIT_SID, ev, split_pop[SPLIT_SID]) if SPLIT_SID in split_pop else []
_items = [r[0] for r in splitbase.shelf_items(con, SPLIT_SID)]
check(blk and "[split]" in blk[0] and any(l.lstrip().startswith("F 9845 split-needed") for l in blk)
      and all(str(i) in "\n".join(blk) for i in _items),
      "the split packet carries the winning F split-needed line and EVERY item id", "\n".join(blk[:4]))
land = splitbase.Landing(con, ev.shelf_set)
_mk = ev.series[midsize]["parsedKey"]
check(land.land("Jim Butcher's The Dresden Files - Storm Front v2 (2009)")[0] == "new"
      and (not _mk or midsize in land.land(_mk)[1]),
      "a fresh key lands NEW; an existing shelf's parsed key lands ON that shelf", f"{_mk!r}")

# the good file — hand-written, the shape a reader writes
P900 = [
    {"shelf": 9845, "split": True, "why": "two co-equal 4-issue runs both numbering #1-4: Storm Front Volume 1 (CV "
     "23697 / GCD 35902, Dabel 2008-2009) stays here with the loose rips; the four 03 Storm Front - Volume 2 files "
     "(CV 27202; GCD 55645 #1 Dabel + 52657 #2-4 Dynamite, 2009-2010) move to a run of their own"},
] + [{"itemId": i, "key": "Jim Butcher's The Dresden Files - Storm Front v2 (2009)",
      "run": {"cv": 27202, "gcd": [55645, 52657]}} for i in (107776, 107777, 107778, 107779)]


def write_split(name, lines, ids=(9845,)):
    p = os.path.join(OUT, name + ".jsonl")
    with open(p, "w", encoding="utf-8") as f:
        for o in lines:
            f.write((o if isinstance(o, str) else json.dumps(o, ensure_ascii=False)) + "\n")
    with open(os.path.join(OUT, name + ".ids"), "w", encoding="utf-8") as f:
        f.write("\n".join(str(s) for s in ids) + "\n")
    return p


def run_split(p, *extra):
    return subprocess.run([sys.executable, os.path.join(idbase.HERE, "check_splits.py"), p] + list(extra),
                          capture_output=True, text=True, encoding="utf-8", errors="replace")


if SPLIT_SID in split_pop:
    p = write_split("P-900", P900)
    r = run_split(p)
    check(r.returncode == 0 and "0 failure(s)" in r.stdout, "P-900 (Storm Front Volume 2 moves out) passes",
          r.stdout[-500:])
    vj = os.path.join(OUT, "P-900.verb.jsonl")
    r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "check_splits.py"), "--project", p, "--out", vj],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    got = [json.loads(x) for x in open(vj, encoding="utf-8")] if os.path.exists(vj) else []
    check(len(got) == 4 and all(set(o) == {"itemId", "key"} for o in got),
          "--project hands books-series-split exactly {itemId, key} per MOVED item (no shelf line)", str(got[:2]))

    other_item = next(iid for iid, sid in con.execute(
        "SELECT i.Id, i.SeriesId FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id WHERE i.SeriesId = ? LIMIT 1",
        (midsize,)))
    bads = [
        ("bad-split-foreign", P900 + [{"itemId": other_item, "key": "Somewhere Else (1999)"}],
         "not a shelf of this batch"),
        ("bad-split-collision", [P900[0], {"itemId": 107776, "key": _mk or "Batman"}] + P900[2:],
         "lands (exact) on live shelf"),
        ("bad-split-twice", P900 + [P900[1]], "moved twice in this file"),
        ("bad-split-empties", [P900[0]] + [{"itemId": i, "key": "Storm Front Everything (2008)"} for i in _items],
         "items move — a split keeps the run that stays"),
        ("bad-split-nocover", P900[1:], "has no shelf line"),
        ("bad-split-spelling", P900[:3] + [dict(P900[3], key="Jim Butcher's the Dresden Files: Storm Front v2 (2009)")],
         "normalize to one canonical key"),
    ]
    for name, lines, want in bads:
        r = run_split(write_split(name, lines))
        check(r.returncode != 0 and want in r.stdout, f"{name}: refused for its own rule ('{want}')",
              r.stdout[-400:])

    # walk-back: the verb's undo CSV, reversed into its own input
    csvp = os.path.join(OUT, "split-undo.csv")
    with open(csvp, "w", encoding="utf-8", newline="") as f:
        f.write("ItemId,PreviousParsedSeriesKey,NewParsedSeriesKey,SeriesIdAtSplit\n"
                "107776,Jim Butcher's The Dresden Files - Storm Front,X v2,9845\n"
                "107776,X v2,X v3,9845\n")
    bj = os.path.join(OUT, "split-back.jsonl")
    subprocess.run([sys.executable, os.path.join(idbase.HERE, "check_splits.py"), "--walkback", csvp, "--out", bj],
                   capture_output=True, text=True)
    back = [json.loads(x) for x in open(bj, encoding="utf-8")] if os.path.exists(bj) else []
    check(back == [{"itemId": 107776, "key": "Jim Butcher's The Dresden Files - Storm Front"}],
          "--walkback restores each item's ORIGINAL key from the undo CSV", str(back))

    # emission: --splits must be able to render into a scratch dir without touching state.json or batches/
    before = _snapshot()
    sdir = os.path.join(OUT, "emit-splits")
    r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "next_batch.py"), "--splits", "--only",
                        str(SPLIT_SID), "--out", sdir], capture_output=True, text=True, encoding="utf-8",
                       errors="replace")
    r2 = subprocess.run([sys.executable, os.path.join(idbase.HERE, "next_batch.py"), "--splits", "--dry-run"],
                        capture_output=True, text=True, encoding="utf-8", errors="replace")
    after = _snapshot()
    body = open(os.path.join(sdir, "P-001.txt"), encoding="utf-8").read() if os.path.exists(
        os.path.join(sdir, "P-001.txt")) else ""
    check(after == before and "'batch': 'P-" in r.stdout and "'batch': 'P-" in r2.stdout
          and body.startswith("## Conventions for this batch") and "== S9845" in body,
          "next_batch --splits (--out / --dry-run) renders P- batches and leaves state.json + batches/ untouched",
          (r.stdout + r.stderr + r2.stderr)[-400:])

# ── part 12: before the big split shelves (TOOLS_TODO 28 a-f) ─────────────────────────────────────────
import re as _re

print("\nthe split lane, round 2 — nearby, missed joins, group/range moves, landed files, both halves, stale flags")
XMR, XMR_JOIN = 22296, 94820          # X-Men: Red (2018 Howard + 2022 Ewing); S94820 = the 2022 run's own shelf
_before12 = _snapshot()


def _cs(*argv):
    return subprocess.run([sys.executable, os.path.join(idbase.HERE, "check_splits.py")] + [str(x) for x in argv],
                          capture_output=True, text=True, encoding="utf-8", errors="replace")


if XMR in split_pop:
    # Rendered against C-037, the decision these fixtures were written from: wave 17's R-031 re-decided S22296
    # and its F line now NAMES S94820 ("JOIN S94820"), which rightly takes it off nearby: and silences the WARN.
    _c037 = os.path.join(idbase.DECISIONS, "C-037.txt")
    xblk = splitbase.packet(XMR, ev, {"file": _c037, "kind": "R"} if os.path.exists(_c037) else split_pop[XMR])
    near = [l for l in xblk if l.lstrip().startswith("nearby:")]
    check(any(f"S{XMR_JOIN} " in l and "shares cv=142134" in l for l in near),
          f"(a) S{XMR}'s packet names S{XMR_JOIN} on its nearby: line (it holds the F line's CV 142134 as its S)",
          "\n".join(near) or "\n".join(xblk[:8]))
    # the packet's own G1 ids, read off the TEXT the reader sees — the expansion must reproduce exactly these
    g1, on = [], False
    for l in xblk:
        s = l.strip()
        if s.startswith("G1 "):
            on = True
            continue
        if on and _re.match(r"G\d+ ", s):
            break
        if on:
            g1 += [int(x) for x in _re.findall(r"(?:^|[\s\[])(\d{4,7})(?=\]| #)", s)]
    SH = {"shelf": XMR, "split": True, "why": "two X-Men: Red runs share the bare title: the 2018 Howard run (CV "
          "108548 / GCD 120699) stays; the 2022 Ewing run (CV 142134 / GCD 183739) moves"}
    RUN = {"cv": 142134, "gcd": 183739}
    rng = {"range": "#5-6", "match": "X-Men Red", "folder": "Judgement Day", "run": RUN}
    ids1 = (XMR,)
    p = write_split("P-910", [SH, {"group": "G1", "files": len(g1), "key": "X-Men Red v2 (2022)", "run": RUN},
                              dict(rng, key="X-Men Red v2 (2022)")], ids1)
    r = _cs(p)
    check(r.returncode == 0 and "0 failure(s)" in r.stdout,
          f"(c) P-910's group + range lines pass (S{XMR_JOIN} is named by R-031's F line since wave 17)",
          r.stdout[-600:])
    # (b) the missed-join WARN, on a shelf whose F line names no join: Storm Front S9845 stating a run whose cv
    # is S9439's S identity (Infinity, CV 66319) — and the same move by S9439's own key is REFUSED unapproved
    if SPLIT_SID in split_pop and 9439 in ev.series:
        r = _cs(write_split("P-912", [P900[0]] + [dict(o, run={"cv": 66319}) for o in P900[1:]]))
        check(r.returncode == 0 and "0 failure(s)" in r.stdout and "MISSED JOIN" in r.stdout
              and "live shelf S9439" in r.stdout,
              "(b) a run whose cv is S9439's S identity passes with a WARN naming the probable missed join",
              r.stdout[-600:])
        _ik = ev.series[9439]["parsedKey"] or "Infinity"
        r = _cs(write_split("bad-join-unapproved", [P900[0]] + [dict(o, key=_ik) for o in P900[1:]]))
        check(r.returncode != 0 and "no `join` approves" in r.stdout,
              "bad-join-unapproved: refused for its own rule ('no `join` approves')", r.stdout[-400:])
    vj = os.path.join(OUT, "P-910.verb.jsonl")
    r = _cs("--project", p, "--out", vj)
    got = [json.loads(x) for x in open(vj, encoding="utf-8")] if os.path.exists(vj) else []
    check(len(g1) == 16 and [o["itemId"] for o in got] == g1 + [118589, 118590]
          and all(o == {"itemId": o["itemId"], "key": "X-Men Red v2 (2022)"} for o in got),
          "(c) --project expands the G1 line into exactly the 16 item ids the packet prints under G1, and the "
          "#5-6 range (folder 'Judgement Day') into items 118589 + 118590", f"G1 {g1}; got {[o['itemId'] for o in got]}")
    jk = "X-Men Red v2 (2022) (Krakoa)"
    r = _cs(write_split("P-911", [dict(SH, join=[XMR_JOIN]), {"group": "G1", "key": jk, "run": RUN},
                                  dict(rng, key=jk)], ids1))
    check(r.returncode == 0 and "0 failure(s), 0 warning(s)" in r.stdout and "1 approved join(s)" in r.stdout,
          f"(b) the same run JOINING S{XMR_JOIN} by its key passes clean with a lead-approved `join`", r.stdout[-400:])
    bads12 = [
        ("bad-range-ambiguous", [SH, dict(rng, key="X-Men Red v2 (2022)", folder=None)], ids1, "AMBIGUOUS"),
        ("bad-range-nothing", [SH, {"range": "#900", "match": "X-Men Red", "key": "X-Men Red v2 (2022)"}], ids1,
         "matches no numbered issue file"),
        ("bad-group-unknown", [SH, {"group": "G99", "key": "X-Men Red v2 (2022)"}], ids1, "is none of them"),
        ("bad-group-drifted", [SH, {"group": "G1", "files": 15, "key": "X-Men Red v2 (2022)"}], ids1,
         "the shelf changed since the packet"),
        ("bad-group-twoshelves", [SH, P900[0], {"group": "G1", "key": "X-Men Red v2 (2022)"}] + P900[1:],
         (XMR, 9845), "ambiguous; add \"from\""),
        ("bad-group-overlap", [SH, {"group": "G1", "key": "X-Men Red v2 (2022)"},
                               {"itemId": g1[0] if g1 else 0, "key": "X-Men Red v2 (2022)"}], ids1, "moved twice"),
    ]
    for name, lines, ids, want in bads12:
        lines = [{k: v for k, v in o.items() if v is not None} if isinstance(o, dict) else o for o in lines]
        r = _cs(write_split(name, lines, ids))
        check(r.returncode != 0 and want in r.stdout, f"{name}: refused for its own rule ('{want}')", r.stdout[-400:])
    r = _cs("--project", write_split("bad-project", [SH, {"group": "G99", "key": "X"}], ids1),
            "--out", os.path.join(OUT, "bad-project.verb.jsonl"))
    check(r.returncode != 0 and not os.path.exists(os.path.join(OUT, "bad-project.verb.jsonl")),
          "--project writes NOTHING when a group / range line does not expand", r.stdout[-300:])
else:
    check(False, f"S{XMR} is in the split population (the round-2 fixtures need it)")

# a LANDED file is checked as landed (the pre-landing rules are false of it by construction)
r = _cs("P-001")
check(r.returncode == 0 and "LANDED" in r.stdout and "0 failure(s)" in r.stdout,
      "P-001 (landed as wave 14) passes as LANDED — every item carries its key and left its shelf", r.stdout[-300:])
r = _cs("P-001", "--unlanded")
check(r.returncode != 0 and "must not land a file twice" in r.stdout,
      "--unlanded (split_land.ps1) refuses a file that already landed", r.stdout[-300:])

# (e) both halves go to the next R- batch, with the split's answer in the packet
sheet = os.path.join(OUT, "landed-P-001.tsv")
r = _cs("--landed", "P-001", "--out", sheet)
rows = [l.rstrip("\n").split("\t") for l in open(sheet, encoding="utf-8") if not l.startswith("#")] \
    if os.path.exists(sheet) else []
new = [c for c in rows if len(c) > 4 and c[4] == "new"]
kept = [c for c in rows if len(c) > 4 and c[4] == "kept"]
check(len(new) == 30 and kept and any(c[0] == "S121" and "S102454" in c[1] for c in kept),
      "(e) --landed lists the 30 new shelves AND the kept halves (S121 kept, its Maps sourcebook -> S102454)",
      f"{len(new)} new, {len(kept)} kept; {kept[:1]}")
two = os.path.join(OUT, "landed-two.tsv")
with open(two, "w", encoding="utf-8") as f:
    f.write("\n".join("\t".join(c) for c in rows if c[0] in ("S102454", "S121")) + "\n")
rdir = os.path.join(OUT, "emit-revisit-split")
r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "next_batch.py"), "--revisit-file", two, "--out", rdir],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
body = "".join(open(os.path.join(rdir, f), encoding="utf-8").read() for f in os.listdir(rdir) if f.endswith(".txt")) \
    if os.path.isdir(rdir) else ""
check('split run: "3W3M - Sourcebook 02 - Maps (2024)" run={"cv": 165815}' in body
      and "split: the KEPT half of the P-001 split" in body,
      "(e) --revisit-file carries the sheet into the packets: `split run:` on the new shelf, `split:` on the kept half",
      body[-500:] or r.stdout[-300:])

# (f) the stale-flag report: read-only, chunked, and it finds the one R-030 had to work around
r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "stale_flags_after_split.py"), "--from", "P-001",
                    "--include-dismissed", "--all"], capture_output=True, text=True, encoding="utf-8", errors="replace")
check(r.returncode == 0 and "S102475" in r.stdout and "flag 60 conflated-series" in r.stdout
      and "'nextCursor'" in r.stdout and "READ-ONLY" in r.stdout,
      "(f) stale_flags_after_split finds flag 60 on S102475 (Dead Body Road: Bad Blood, alone after P-001)",
      r.stdout[-400:])
r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "stale_flags_after_split.py"), "--population",
                    "--limit", "5"], capture_output=True, text=True, encoding="utf-8", errors="replace")
check(r.returncode == 0 and "'processed': 5" in r.stdout and "'remaining'" in r.stdout,
      "(f) the population mode does a bounded chunk and prints {processed, remaining, nextCursor, counts}",
      r.stdout[-300:])
check(_snapshot() == _before12, "part 12 wrote nothing to state.json or batches/")

# ── part 13: after P-002 / wave 17 (TOOLS_TODO 29, 30, 31) ────────────────────────────────────────────
print("\nafter P-002 — R/N ids and collected runs in nearby:, pending_join, lookup's 'of', the refused-partner merge")
_before13 = _snapshot()
LONG13 = ("this shelf is the run that volume describes, and the two legs agree on start year and issue count, "
          "so the id belongs here")

# 29a: ids and shelves named only in the decided R clause / the N lines are probed (P-002 missed S102444, S9439)
if 1527 in ev.series and 9439 in ev.series and 102444 in ev.series:
    fx = os.path.join(OUT, "C-929.txt")
    with open(fx, "w", encoding="utf-8") as f:
        f.write("# selftest: a split shelf whose R clause names what its F line does not\n"
                "R 1527 | the 1046pp HC collects the 2013 Infinity event, CV 66319; the stored serial link is the "
                "one sibling shelf S4251 holds\n"
                "F 1527 split-needed | proposed runs: the 2000 mini stays, the HC moves\n")
    b29 = splitbase.packet(1527, ev, {"file": fx, "kind": "R"})
    near29 = " ".join(l for l in b29 if l.lstrip().startswith("nearby:"))
    check("S9439 " in near29 and "cv=66319 (R clause)" in near29,
          "29a: an id named only in the R clause (CV 66319) puts S9439 on nearby:", near29[:400])
    check("S102444 " in near29 and "named S4251" in near29 and "(now S102444)" in near29,
          "29a: a shelf the R clause names (S4251) is followed through landed merges to S102444", near29[:400])
    # 29b: a trade's COLLECTED runs print beside its own record, and are probed
    b29 = splitbase.packet(9439, ev, {"file": fx, "kind": "R"})
    txt29 = "\n".join(b29)
    check("own record cv 71516 / gcd s177302" in txt29 and "COLLECTS: gcd=75977 Infinity (2013) #1-6" in txt29,
          "29b: the Infinity HC (item 117741) prints its own record AND the run it collects (gcd_reprint)",
          txt29[-600:])
    _own = splitbase.near_index(ev).s_ids.get(("gcd", 70263), set()) - {9439}
    near29 = " ".join(l for l in b29 if l.lstrip().startswith("nearby:"))
    check(not _own or any(f"S{o} " in near29 for o in _own) and "collected by item 117741" in near29,
          "29b: a run the HC collects (Avengers 2013, gcd 70263) brings its shelf onto nearby:", near29[:400])
else:
    check(False, "29: S1527 / S9439 / S102444 are live (the fixtures need them)")

# 29c: pending_join — accepted, moves nothing, and must name a live shelf other than itself
if SPLIT_SID in split_pop:
    r = run_split(write_split("P-929", [dict(P900[0], pending_join=[9439])] + P900[1:]))
    check(r.returncode == 0 and "0 failure(s)" in r.stdout, "29c: a shelf line with pending_join passes", r.stdout[-300:])
    for name, pj in (("bad-pj-self", [SPLIT_SID]), ("bad-pj-dead", [999999991]), ("bad-pj-shape", "9439")):
        r = run_split(write_split(name, [dict(P900[0], pending_join=pj)] + P900[1:]))
        check(r.returncode != 0 and "`pending_join`" in r.stdout, f"29c: {name} refused", r.stdout[-300:])
# 29d: --landed carries it onto the kept half's sheet row, and --revisit-file prints the merge-with prompt
pjdir = os.path.join(OUT, "pj")
os.makedirs(pjdir, exist_ok=True)
pjp = os.path.join(pjdir, "P-001.jsonl")          # the name must stay P-001: the undo CSV is found by it
with open(pjp, "w", encoding="utf-8") as f:
    for raw in open(os.path.join(idbase.DECISIONS, "P-001.jsonl"), encoding="utf-8"):
        o = json.loads(raw) if raw.strip() else None
        if o and o.get("shelf") == 121:
            o["pending_join"] = [9439]
        if o:
            f.write(json.dumps(o, ensure_ascii=False) + "\n")
sheet = os.path.join(OUT, "landed-pj.tsv")
r = _cs("--landed", pjp, "--out", sheet)
rows = [l.rstrip("\n").split("\t") for l in open(sheet, encoding="utf-8") if not l.startswith("#")] \
    if os.path.exists(sheet) else []
k121 = [c for c in rows if c[0] == "S121" and len(c) > 4 and c[4] == "kept"]
check(k121 and k121[0][-1] == "pending_join=9439", "29d: --landed writes pending_join onto S121's kept row", str(k121))
with open(os.path.join(OUT, "landed-pj-one.tsv"), "w", encoding="utf-8") as f:
    f.write("\t".join(k121[0]) + "\n" if k121 else "")
rdir = os.path.join(OUT, "emit-revisit-pj")
r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "next_batch.py"), "--revisit-file",
                    os.path.join(OUT, "landed-pj-one.tsv"), "--out", rdir],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
body = "".join(open(os.path.join(rdir, f), encoding="utf-8").read() for f in os.listdir(rdir) if f.endswith(".txt")) \
    if os.path.isdir(rdir) else ""
check("merge-with candidate: S9439" in body and "F 121 merge-with=9439" in body,
      "29d: the kept half's R packet carries `merge-with candidate: S9439`", body[-400:] or r.stdout[-300:])

# 30: CV probes keep "of" — cvref stores 'legion monsters', and the index is now keyed the way probes are
qf = os.path.join(OUT, "lookup-of.txt")
with open(qf, "w", encoding="utf-8") as f:
    f.write('"Legion of Monsters"\n"Heart of Darkness"\n')
r = subprocess.run([sys.executable, os.path.join(idbase.HERE, "lookup.py"), "--batch", qf],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
check('22154 "Legion of Monsters"' in r.stdout and '43345 "Legion of Monsters"' in r.stdout,
      "30: lookup 'Legion of Monsters' finds the CV volumes (was 0 hits)", r.stdout[:400])
check('45960 "Heart of Darkness"' in r.stdout and '71507 "Heart of Darkness"' in r.stdout,
      "30: lookup 'Heart of Darkness' finds the CV volumes (was 0 hits)", r.stdout[-400:])

# 31: an S cv= equal to a live REFUSED shelf's stored cv / Matched key link is an undeclared merge
_d13, _w13, _s13, _dd13 = idbase.scan_decisions()


def _refused_ok(s):
    rec = _d13[_w13[s]]
    return (s in ev.shelf_set and rec["kinds"].get(s) == "R"
            and "wrong-cv-link" not in rec["flags"].get(s, ()))


P31 = 47160 if 47160 in _w13 and _refused_ok(47160) and ev.series[47160]["cvVolumeId"] else \
    next((s for s in sorted(_w13) if _refused_ok(s) and ev.series[s]["cvVolumeId"]), None)
OTHER31 = 1527


def _ci(tag, body, ids):
    with open(os.path.join(OUT, tag + ".ids"), "w", encoding="utf-8") as f:
        f.write("\n".join(str(i) for i in ids) + "\n")
    p = os.path.join(OUT, tag + ".txt")
    with open(p, "w", encoding="utf-8") as f:
        f.write("# selftest: refused-partner fixture (TOOLS_TODO 31)\n" + body)
    return subprocess.run([sys.executable, os.path.join(idbase.HERE, "check_identity.py"), "--all", p],
                          capture_output=True, text=True, encoding="utf-8", errors="replace")


if P31 and OTHER31 in ev.series:
    V31 = ev.series[P31]["cvVolumeId"]
    msg = f"of REFUSED shelf S{P31}"
    r = _ci("R-930", f"S {OTHER31} cv={V31} gcd=- 0.9 | {LONG13}\n", [OTHER31])
    check(r.returncode != 0 and msg in r.stdout and "stored CvVolumeId" in r.stdout,
          f"31a: S{OTHER31} cv={V31} = the stored cv of refused S{P31} FAILS as an undeclared merge", r.stdout[-500:])
    r = _ci("R-931", f"S {OTHER31} cv={V31} gcd=- 0.9 | {LONG13}\n"
                     f"F {OTHER31} merge-with={P31} | the refused shelf holds the floppies of this same run\n", [OTHER31])
    check(msg not in r.stdout, "31b: declared with F merge-with=, it passes the rule", r.stdout[-400:])
    r = _ci("R-932", f"S {OTHER31} cv={V31} gcd=- 0.9 | {LONG13}\n"
                     f"R {P31} | refused again in the same file, so the apply's frees arm clears its stored link\n",
            [OTHER31, P31])
    check(msg not in r.stdout, "31c: the partner refused in the SAME file (cleared at apply) is not a partner",
          r.stdout[-400:])
    if P31 == 47160 and 15478 not in ev.shelf_set:
        r = _ci("R-933", f"S 15478 cv={V31} gcd=12538 0.95 | {LONG13}\n", [15478])
        check(msg not in r.stdout and "LANDED S line(s) matched a refused shelf" in r.stdout and "S15478" in r.stdout,
              "31d: wave 17's own S15478 line (merged away since) is reported as landed history, not a failure",
              r.stdout[-500:])
    # the key-link arm: a refused shelf whose parsed key still carries a Matched/Manual cv link to W
    _kl = con.execute("""SELECT a.SeriesId, k.ProviderKey FROM SeriesKeyLink k
                         JOIN SeriesAlias a ON a.ParsedKey = k.ParsedKey
                         WHERE k.Provider = 0 AND k.Status IN (1, 5) AND k.ProviderKey IS NOT NULL""").fetchall()
    _kl = next(((s, int(w)) for s, w in _kl if s in _w13 and s != OTHER31 and _refused_ok(s)
                and ev.series[s]["cvVolumeId"] != int(w)), None) if _kl else None
    if _kl:
        r = _ci("R-934", f"S {OTHER31} cv={_kl[1]} gcd=- 0.9 | {LONG13}\n", [OTHER31])
        check(f"of REFUSED shelf S{_kl[0]}" in r.stdout and "SeriesKeyLink on its key" in r.stdout,
              f"31e: cv={_kl[1]} on a Matched key link of refused S{_kl[0]} (stored cv differs) FAILS too",
              r.stdout[-500:])
    else:
        print("  (31e skipped: no refused shelf carries a key link that differs from its stored cv today)")
else:
    check(False, "31: a live refused shelf with a stored cv exists (the fixtures need one)")
check(_snapshot() == _before13, "part 13 wrote nothing to state.json or batches/")

# ── part 14: after R-032 / waves 19-21 (TOOLS_TODO 33, 34, 35, 36, 37) ───────────────────────────────────
print("\nafter R-032 — split pairs kept together, --shelf / --who-stores / --id, empty partners, one-wave stale flags")
_before14 = _snapshot()


def _py(tool, *argv):
    return subprocess.run([sys.executable, os.path.join(idbase.HERE, tool)] + [str(x) for x in argv],
                          capture_output=True, text=True, encoding="utf-8", errors="replace")


# 33a: --landed rows carry pair=<origin sid>; the kept half is its own pair and lists the runs that left it
sh3, sh4 = os.path.join(OUT, "landed-P-003.tsv"), os.path.join(OUT, "landed-P-004.tsv")
_cs("--landed", "P-003", "--out", sh3)
_cs("--landed", "P-004", "--out", sh4)


def _rows(p):
    return [l.rstrip("\n").split("\t") for l in open(p, encoding="utf-8") if l.startswith("S")] if os.path.exists(p) else []


def _kv(c):
    return dict(x.split("=", 1) for x in c[5:] if "=" in x)


r4 = _rows(sh4)
astro_new = [c for c in r4 if c[4] == "new" and _kv(c).get("pair") == "1431"]
astro_kept = [c for c in r4 if c[0] == "S1431" and c[4] == "kept"]
check(len(astro_new) >= 3 and astro_kept and _kv(astro_kept[0]).get("pair") == "1431"
      and "25753" in _kv(astro_kept[0]).get("moved_cv", "").split(","),
      "33a: --landed P-004 pairs Astro City's new shelves with S1431 (pair=1431) and lists moved_cv 25753 on the "
      "kept row", f"{len(astro_new)} new; kept {astro_kept[:1]}")
check(all("pair" in _kv(c) for c in r4), "33a: every P-004 sheet row carries a pair=", "")
# the sheets + extra rows go in ONE call, cut by a small line budget into several R- batches, no group cut
extra = os.path.join(OUT, "extra-rows.txt")
with open(extra, "w", encoding="utf-8") as f:
    f.write(f"S{good_a}\textra row: a shelf with no pair\n")
edir = os.path.join(OUT, "emit-pairs")
if os.path.isdir(edir):
    for f_ in os.listdir(edir):
        os.remove(os.path.join(edir, f_))
r = _py("next_batch.py", "--revisit-file", extra, sh3, sh4, "--lines", "500", "--out", edir)
ids_files = sorted(f for f in os.listdir(edir) if f.endswith(".ids")) if os.path.isdir(edir) else []
where = {}
for f_ in ids_files:
    for x in open(os.path.join(edir, f_), encoding="utf-8").read().split():
        where[int(x)] = f_
groups33 = {}
for c in _rows(sh3) + _rows(sh4):
    p = _kv(c).get("pair")
    if p:
        groups33.setdefault(int(p), set()).add(int(c[0][1:]))
cut33 = [g for g, m in groups33.items() if len({where[s] for s in m if s in where}) > 1]
check(len(ids_files) >= 3 and not cut33 and good_a in where and "'groups cut': 0" in r.stdout,
      "33a: --revisit-file takes two sheets + an extra-rows file, cuts them into several R- batches at --lines 500, "
      "and no pair group straddles two batches", f"{len(ids_files)} batches; cut {cut33[:5]}; {r.stdout[-300:]}")
# 33b: a kept half still storing a moved run's cv is told so on its packet
KB = next((s for s in (64503, good_b) if s in ev.series and ev.series[s]["cvVolumeId"]), None)
if KB:
    kv_ = ev.series[KB]["cvVolumeId"]
    kb_sheet = os.path.join(OUT, "landed-kept-fixture.tsv")
    with open(kb_sheet, "w", encoding="utf-8") as f:
        f.write(f"S{KB}\tmoved out -> (fixture)\trun=-\tP-999.jsonl\tkept\tpair={KB}\tmoved_cv={kv_}\n")
    kdir = os.path.join(OUT, "emit-kept")
    r = _py("next_batch.py", "--revisit-file", kb_sheet, "--out", kdir)
    body = "".join(open(os.path.join(kdir, f_), encoding="utf-8").read() for f_ in os.listdir(kdir)
                   if f_.endswith(".txt")) if os.path.isdir(kdir) else ""
    check(f"stored cv {kv_} = the moved run's" in body and f"F {KB} wrong-cv-link" in body,
          f"33b: the kept half S{KB} (stored cv {kv_} = a moved run's) carries the wrong-cv-link prompt",
          body[:300] or r.stdout[-300:])

# 34: lookup --shelf / --who-stores; 36: --id, and ParsedKeys verbatim on nearby: / named:
r = _py("lookup.py", "--shelf", 1431, "--limit", 3)
_k1431 = sorted(ev.keys.get(1431, ()))
check(r.returncode == 0 and all(json.dumps(k, ensure_ascii=False) in r.stdout for k in _k1431)
      and "stored cv:" in r.stdout and "in force:" in r.stdout,
      "34: lookup --shelf 1431 prints every key quoted verbatim, the stored cv and the lines in force", r.stdout[:400])
_empty = con.execute(f"""SELECT Id, CvVolumeId FROM Series WHERE CvVolumeId IS NOT NULL
                         AND coalesce(CanonicalKey,'') NOT LIKE 'book:%' AND Id NOT IN ({idbase.SHELF_SQL})
                         ORDER BY Id LIMIT 1""").fetchone()
if _empty:
    r = _py("lookup.py", "--who-stores", f"cv={_empty[1]}")
    check(r.returncode == 0 and f"S{_empty[0]} " in r.stdout and "EMPTY" in r.stdout,
          f"34: lookup --who-stores cv={_empty[1]} lists the EMPTY Series row S{_empty[0]} storing it", r.stdout[:400])
r = _py("lookup.py", "--id", "gcd=12140", "cv=25753")
check('gcd=12140 "Astro City Special"' in r.stdout and "cv=25753 " in r.stdout,
      "36: lookup --id names a GCD series and a CV volume in one call", r.stdout[:300])
if XMR in split_pop and XMR_JOIN in ev.series:
    _c037 = os.path.join(idbase.DECISIONS, "C-037.txt")
    xb = splitbase.packet(XMR, ev, {"file": _c037, "kind": "R"} if os.path.exists(_c037) else split_pop[XMR])
    nl = " ".join(l for l in xb if l.lstrip().startswith("nearby:"))
    _kj = sorted(ev.keys.get(XMR_JOIN, ()))[:4]
    check(_kj and all(json.dumps(k, ensure_ascii=False) in nl for k in _kj),
          f"36: nearby: prints S{XMR_JOIN}'s ParsedKey(s) verbatim, quoted", nl[:400])
if 1527 in ev.series:
    fx36 = os.path.join(OUT, "C-936.txt")
    with open(fx36, "w", encoding="utf-8") as f:
        f.write("R 1527 | fixture: two runs on one shelf, the HC must JOIN the event's shelf by its exact key\n"
                "F 1527 split-needed | the HC moves to S9439 (join)\n")
    b36 = splitbase.packet(1527, ev, {"file": fx36, "kind": "R"})
    named = [l for l in b36 if l.lstrip().startswith("named: S9439")]
    check(named and all(json.dumps(k, ensure_ascii=False) in named[0] for k in ev.keys.get(9439, ())),
          "36: named: prints S9439's ParsedKey(s) verbatim, quoted", "\n".join(named) or "\n".join(b36[:6]))

# 35: a stored CvVolumeId on an EMPTY live Series row is a merge partner (wave 19: S96256 -> empty S64503)
if _empty and OTHER31 in ev.series:
    msg35 = f"EMPTY Series row (no files; it survives the resolve) S{_empty[0]}"
    r = _ci("R-935", f"S {OTHER31} cv={_empty[1]} gcd=- 0.9 | {LONG13}\n", [OTHER31])
    check(r.returncode != 0 and msg35 in r.stdout,
          f"35: S{OTHER31} cv={_empty[1]} = the stored cv of EMPTY S{_empty[0]} FAILS as an undeclared merge",
          r.stdout[-500:])
    r = _ci("R-936", f"S {OTHER31} cv={_empty[1]} gcd=- 0.9 | {LONG13}\n"
                     f"F {OTHER31} merge-with={_empty[0]} | the empty row is this run's own old shelf\n", [OTHER31])
    check(msg35 not in r.stdout, "35: declared with F merge-with=, it passes", r.stdout[-400:])

# 37: one-wave stale flags — the checker, the gate, and the dismissal against a COPY of the flag table
_open = [r_[0] for r_ in con.execute(
    "SELECT Id FROM ContainmentFlag WHERE SeriesId = ? AND Flag IN ('conflated-series','overlap-in-series') "
    "AND (ReviewState IS NULL OR ReviewState IN ('','Pending','Open')) ORDER BY Id", (conflated,))]
_other = con.execute("SELECT Id FROM ContainmentFlag WHERE SeriesId <> ? AND Flag = 'conflated-series' "
                     "AND (ReviewState IS NULL OR ReviewState IN ('','Pending','Open')) LIMIT 1", (conflated,)).fetchone()
EV37 = ("every file on this shelf is one run: one folder, one ladder #1-12, one publisher and one start year; the "
        "flag's second run is a duplicate rip")


def _w37(name, body):
    with open(os.path.join(OUT, name + ".ids"), "w", encoding="utf-8") as f:
        f.write(f"{conflated}\n")
    p = os.path.join(OUT, name + ".txt")
    with open(p, "w", encoding="utf-8") as f:
        f.write("# selftest: stale-flag fixture (TOOLS_TODO 37)\n" + body)
    errs = []
    check_identity.parse(p, ck37, errs)
    return p, errs


ck37 = check_identity.Checker()
S37 = f"S {conflated} cv={vol_a} gcd=- 0.9 | {E}\n"
claims37 = "".join(f"F {conflated} stale-flag={fid} | {EV37}\n" for fid in _open)
p_ok, errs = _w37("R-937", S37 + claims37)
check(_open and not errs, f"37: an S on conflated S{conflated} passes when EVERY open flag ({_open}) is claimed stale",
      str(errs))
if len(_open) > 1:
    _p, errs = _w37("bad-stale-partial", S37 + f"F {conflated} stale-flag={_open[0]} | {EV37}\n")
    check(any("OPEN conflated-series" in e and f"flag {_open[1]}" in e for e in errs),
          "37: claiming only some of the open flags still refuses the S, naming the unclaimed one", str(errs))
if _other:
    _p, errs = _w37("bad-stale-other", S37 + claims37 + f"F {conflated} stale-flag={_other[0]} | {EV37}\n")
    check(any("not on S" in e for e in errs), "37: a claim naming ANOTHER shelf's open flag is refused", str(errs))
_p, errs = _w37("bad-stale-short", S37 + "".join(f"F {conflated} stale-flag={fid} | stale\n" for fid in _open))
check(any("stale-flag claim needs evidence" in e for e in errs), "37: a claim needs >= 40 chars of evidence", str(errs))
# the gate and the dismissal, against a COPY of the flag table — never the live DB
import sqlite3 as _sq
cp37 = os.path.join(OUT, "flags-copy.db")
if os.path.exists(cp37):
    os.remove(cp37)
_c = _sq.connect(cp37)
_cols = [r_[1] for r_ in con.execute("PRAGMA table_info(ContainmentFlag)")]
_c.execute(f"CREATE TABLE ContainmentFlag ({', '.join(_cols)})")
_c.executemany(f"INSERT INTO ContainmentFlag VALUES ({','.join('?' * len(_cols))})",
               con.execute("SELECT * FROM ContainmentFlag").fetchall())
_c.commit()
_c.close()
appr = os.path.join(OUT, "stale-approved.txt")
with open(appr, "w", encoding="utf-8") as f:
    f.write("# selftest: nothing approved yet\n")
r = _py("stale_flags.py", "--gate", p_ok, "--approved", appr, "--db", cp37)
check(r.returncode != 0 and "NOT in the approved list" in r.stdout and "STOP" in r.stdout,
      "37: the gate STOPS on an unapproved claim (wave_land would halt before the backup)", r.stdout[-400:])
r = _py("stale_flags.py", "--dismiss", p_ok, "--approved", appr, "--db", cp37, "--apply", "--undo-dir",
        os.path.join(OUT, "undo37"))
_st = _sq.connect(cp37).execute(f"SELECT count(*) FROM ContainmentFlag WHERE Id IN ({','.join('?' * len(_open))}) "
                                 "AND ReviewState = 'Dismissed'", _open).fetchone()[0] if _open else 0
check(r.returncode != 0 and _st == 0, "37: --dismiss --apply refuses too, and writes nothing, while unapproved",
      r.stdout[-300:])
with open(appr, "w", encoding="utf-8") as f:
    f.write("# selftest: the lead read the shelf's files\n" + "".join(f"{fid}  S{conflated}\n" for fid in _open))
r = _py("stale_flags.py", "--gate", p_ok, "--approved", appr, "--db", cp37)
check(r.returncode == 0 and "APPROVED" in r.stdout, "37: the gate passes once every claimed flag is approved",
      r.stdout[-300:])
r = _py("stale_flags.py", "--dismiss", p_ok, "--approved", appr, "--db", cp37, "--apply", "--undo-dir",
        os.path.join(OUT, "undo37"))
_rows37 = _sq.connect(cp37).execute(
    f"SELECT ReviewState, Note, DecidedBy FROM ContainmentFlag WHERE Id IN ({','.join('?' * len(_open))})",
    _open).fetchall() if _open else []
check(r.returncode == 0 and _rows37 and all(s == "Dismissed" and EV37 in n and "lead-verified (R-937)" in n
                                            and b == "identity-pass" for s, n, b in _rows37),
      "37: --dismiss --apply dismisses exactly the approved flags on the COPY (Note = evidence + lead-verified)",
      f"{_rows37} {r.stdout[-300:]}")
_live = con.execute(f"SELECT count(*) FROM ContainmentFlag WHERE Id IN ({','.join('?' * len(_open))}) "
                    "AND (ReviewState IS NULL OR ReviewState IN ('','Pending','Open'))", _open).fetchone()[0] \
    if _open else -1
check(_live == len(_open), "37: the LIVE flags are untouched (the fixture wrote only its copy)", f"{_live}")
r = _py("stale_flags.py", "--dismiss", p_ok, "--approved", appr, "--db", cp37, "--apply", "--undo-dir",
        os.path.join(OUT, "undo37"))
check(r.returncode == 0 and "already Dismissed" in r.stdout and "nothing to dismiss" in r.stdout,
      "37: a re-run is idempotent (already Dismissed, nothing written twice)", r.stdout[-300:])
# ...and the S the claim lets stand is what apply_identity (dry run) would land in the same wave
r = _py("apply_identity.py", p_ok)
check(r.returncode == 0 and "0 refused" in r.stdout and re.search(rf"link\s+S{conflated}\s", r.stdout) and "stale-flag=" in r.stdout,
      "37: apply_identity's dry run accepts the S + stale-flag file (S and dismissal land in one wave)",
      (r.stdout + r.stderr)[-400:])
check(_snapshot() == _before14, "part 14 wrote nothing to state.json or batches/")

print(f"\n{len(failures)} failure(s)")
if not KEEP:
    print(f"(files kept in {OUT} — inspect them, they are the worked examples)")
sys.exit(1 if failures else 0)
