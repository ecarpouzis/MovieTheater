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

print(f"\n{len(failures)} failure(s)")
if not KEEP:
    print(f"(files kept in {OUT} — inspect them, they are the worked examples)")
sys.exit(1 if failures else 0)
