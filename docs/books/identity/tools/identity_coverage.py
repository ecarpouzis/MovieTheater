"""Every file-holding comic shelf, and every comic file, partitioned. No unaccounted bucket.

`coverage_ledger.py` replaced "976 decision files, 0 failing" — a count of decisions — with a partition of
the LIBRARY, because only a partition can be wrong in a way you notice. This is that instrument for
identity: it regenerates PLAN §2 from the live DB and then forces the tier assignment of §7-S to sum to
the shelf population and to the item population. If either sum is short, the script fails, because a shelf
that belongs to no tier is a shelf no batch will ever emit and no reader will ever see.

`python identity_coverage.py [--verbose]`
"""
import os
import sys
from collections import Counter

import idbase
from idbase import Evidence

VERBOSE = "--verbose" in sys.argv
SECOND_READ = "--second-read" in sys.argv

ev = Evidence()
con = ev.con
sh = ev.shelf_set

if SECOND_READ:
    # The S.2 population: every shelf whose first reading left something open. Printed as sids in the
    # shape `next_batch.py --revisit-file` reads (first column = sid), so the list IS the input to the
    # revisit, with the reason kept beside it for whoever reads the batch.
    decides, winner, superseded, dupes = idbase.scan_decisions()
    rows = []
    for sid in sorted(sh):
        w = winner.get(sid)
        if not w:
            continue
        rec, why = decides[w], []
        kind, conf = rec["kinds"].get(sid), rec["confs"].get(sid)
        flags = rec["flags"].get(sid, [])
        if kind == "R":
            why.append("refused")
        elif conf == "0.7":
            why.append("0.7 review")
        elif conf == "0.9":
            why.append("0.9 one leg")
        if flags:
            why.append("flags " + ",".join(sorted(set(flags))))
        if kind == "S" and conf == "0.95" and len(ev.keys.get(sid, ())) >= 2:
            why.append(f"0.95 on a shelf with {len(ev.keys.get(sid, ()))} parsed keys")
        if kind == "S" and ev.numbered.get(sid, 0) == 0:
            why.append("trade-only shelf (no numbered issue file)")
        if why:
            rows.append((sid, os.path.splitext(os.path.basename(w))[0], "; ".join(why)))
    print(f"# second-read population: {len(rows)} shelf/shelves, from {len(winner)} decided")
    print("# <sid> <batch> <why>   — feed to: next_batch.py --revisit-file <this file>")
    for sid, batch, why in rows:
        print(f"{sid:<7} {batch:<8} {ev.series[sid]['name'][:44]}: {why}")
    sys.exit(0)

print(f"{len(sh):,} file-holding comic shelves   ({ev.total_items:,} comic items, not excluded)")
print()

# ── §2 the population ────────────────────────────────────────────────────────────────────────────
buckets = {"1 file": 0, "2-5 files": 0, "6-20": 0, "21-100": 0, ">100": 0}
for sid in sh:
    n = ev.size.get(sid, 0)
    k = ("1 file" if n == 1 else "2-5 files" if n <= 5 else "6-20" if n <= 20 else
         "21-100" if n <= 100 else ">100")
    buckets[k] += 1
print("shelf size:  " + " · ".join(f"{k} {v:,}" for k, v in buckets.items()))
print()

# ── §2 what each shelf already carries (the six rows the tiers are cut from) ─────────────────────
ROWS = ["CV volume set  + one GCD series", "CV volume set  + NO GCD", "CV volume set  + SEVERAL GCD",
        "no CV          + one GCD series", "no CV          + SEVERAL GCD", "no CV          + no GCD"]
rows, files = {r: 0 for r in ROWS}, {r: 0 for r in ROWS}
for sid in sh:
    has_cv = ev.series[sid]["cvVolumeId"] is not None
    g = len(ev.gcd_ids(sid))
    k = (ROWS[0] if has_cv and g == 1 else ROWS[1] if has_cv and g == 0 else ROWS[2] if has_cv else
         ROWS[3] if g == 1 else ROWS[4] if g > 1 else ROWS[5])
    rows[k] += 1
    files[k] += ev.size.get(sid, 0)
print(f"{'':<32}{'shelves':>9}{'files':>9}     (PLAN §2)")
PLAN2 = {ROWS[0]: (5051, 25527), ROWS[1]: (7621, 15944), ROWS[2]: (914, 22915),
         ROWS[3]: (1782, 15155), ROWS[4]: (561, 24940), ROWS[5]: (4456, 13876)}
for r in ROWS:
    ps, pf = PLAN2[r]
    d = "" if (rows[r], files[r]) == (ps, pf) else f"   <- plan said {ps:,} / {pf:,}"
    print(f"  {r:<30}{rows[r]:>9,}{files[r]:>9,}{d}")
print(f"  {'total':<30}{sum(rows.values()):>9,}{sum(files.values()):>9,}")
print()

noleg = [s for s in sh if ev.series[s]["cvVolumeId"] is None and not ev.gcd_ids(s)]
unan = [s for s in noleg if len(ev.cv_files.get(s, {})) == 1]
several = [s for s in noleg if len(ev.cv_files.get(s, {})) > 1]
none_at_all = [s for s in noleg if not ev.cv_files.get(s)]
print(f"  of the {len(noleg):,} with no CV and no GCD: {len(unan):,} carry a UNANIMOUS v1 per-file CV volume, "
      f"{len(several):,} carry several,")
print(f"      and {len(none_at_all):,} ({sum(ev.size.get(s,0) for s in none_at_all):,} files) have NO LEG AT ALL "
      f"(plan said 3,852 unanimous / 2,323 no-leg — 5,446 files)")
print()

# ── §2 series-level identity columns and links ───────────────────────────────────────────────────
cvid = {s: ev.series[s]["cvVolumeId"] for s in sh if ev.series[s]["cvVolumeId"] is not None}
missing_row = [s for s, v in cvid.items() if v not in ev.cv_volume]
cached = {v for v in ev.cv_issue_span}
print(f"Series.CvVolumeId ....... {len(cvid):,} of {len(sh):,} shelves   "
      f"({len(cvid)-len(missing_row):,} have a CvVolume row; {len(missing_row):,} point at a volume we never fetched)")
n_iss = con.execute("SELECT count(*) FROM CvIssue").fetchone()[0]
print(f"CvVolume rows ........... {len(ev.cv_volume):,};  CvIssue lists cached for {len(cached):,} volumes "
      f"({n_iss:,} issues); {len({v for v in cvid.values() if v in cached}):,} linked shelves have theirs")
print(f"Series.TitleId .......... {sum(1 for s in sh if ev.series[s]['titleId']):,} file-holding "
      f"({100.0*sum(1 for s in sh if ev.series[s]['titleId'])/len(sh):.0f}%)")
print(f"Series.MuSeriesId ....... {sum(1 for s in sh if ev.series[s]['muSeriesId']):,};  "
      f"MuSeriesLink {con.execute('SELECT count(*) FROM MuSeriesLink').fetchone()[0]:,};  "
      f"Series.ExternalWorkId {sum(1 for s in sh if ev.series[s]['externalWorkId']):,}")
STAT = {0: "Pending", 1: "Matched", 2: "NoMatch", 3: "Multiple", 4: "Error", 5: "Manual", 6: "Cleared", 7: "Skip"}
PROV = {0: "Cv", 1: "External", 2: "Locg", 3: "Gcd", 4: "Mu", 5: "Barney"}
skl = {}
for p, st, n in con.execute("SELECT Provider, Status, count(*) FROM SeriesKeyLink GROUP BY 1,2"):
    skl.setdefault(p, []).append(f"{STAT.get(st, st)} {n:,}")
for p in sorted(skl):
    print(f"SeriesKeyLink {PROV.get(p, p):<9} " + " · ".join(skl[p]))
print()

# ── §2 per-file provider links ───────────────────────────────────────────────────────────────────
tot = con.execute("SELECT count(*) FROM ItemProviderLink").fetchone()[0]
print(f"Per-file provider links (ItemProviderLink, {tot:,} rows)")
for p, n in con.execute("SELECT Provider, count(*) FROM ItemProviderLink WHERE Status=1 GROUP BY 1 ORDER BY 2 DESC"):
    print(f"  {PROV.get(p, p):<9}({p}) {n:>8,} Matched")
agree = con.execute("""SELECT count(*) FROM ItemProviderLink l JOIN Item i ON i.Id=l.ItemId
                       JOIN Series s ON s.Id=i.SeriesId
                       WHERE l.Provider=0 AND l.Status=1 AND s.CvVolumeId IS NOT NULL
                         AND l.SecondaryKey = cast(s.CvVolumeId AS TEXT)""").fetchone()[0]
disagree = con.execute("""SELECT count(*) FROM ItemProviderLink l JOIN Item i ON i.Id=l.ItemId
                          JOIN Series s ON s.Id=i.SeriesId
                          WHERE l.Provider=0 AND l.Status=1 AND s.CvVolumeId IS NOT NULL
                            AND l.SecondaryKey IS NOT NULL
                            AND l.SecondaryKey <> cast(s.CvVolumeId AS TEXT)""").fetchone()[0]
noshelf = con.execute("""SELECT count(*) FROM ItemProviderLink l JOIN Item i ON i.Id=l.ItemId
                         LEFT JOIN Series s ON s.Id=i.SeriesId
                         WHERE l.Provider=0 AND l.Status=1 AND (s.CvVolumeId IS NULL)""").fetchone()[0]
print(f"  Cv per-file vs the shelf: {agree:,} agree · {disagree:,} disagree · {noshelf:,} on shelves with no CvVolumeId")
meth = con.execute("SELECT Method, count(*) FROM ItemProviderLink WHERE Provider=3 AND Status=1 GROUP BY 1 ORDER BY 2 DESC")
print("  Gcd methods: " + " · ".join(f"{m or '?'} {n:,}" for m, n in meth))
print()

# ── §2 the collection's own assertions ───────────────────────────────────────────────────────────
emb = con.execute("SELECT count(*) FROM ComicEmbedded").fetchone()[0]
web = con.execute("SELECT count(*) FROM ComicEmbedded WHERE Web IS NOT NULL AND Web <> ''").fetchone()[0]
cvweb = con.execute("SELECT count(*) FROM ComicEmbedded WHERE Web LIKE '%comicvine%'").fetchone()[0]
cvweb_sh = con.execute("""SELECT count(DISTINCT i.SeriesId) FROM ComicEmbedded e JOIN Item i ON i.Id=e.ItemId
                          WHERE e.Web LIKE '%comicvine%'""").fetchone()[0]
print(f"ComicEmbedded.Web ....... {web:,} of {emb:,} ComicInfo rows carry a URL; {cvweb:,} are ComicVine issue "
      f"URLs, on {cvweb_sh:,} shelves")
bs = con.execute("SELECT count(*) FROM legs.BarcodeScan WHERE CodesJson IS NOT NULL AND CodesJson NOT IN ('','[]')").fetchone()[0]
print(f"BarcodeScan (legs) ...... {bs:,} items with codes read off the page")
jr, tomb = con.execute("""SELECT sum(CASE WHEN IssueStart IS NOT NULL THEN 1 ELSE 0 END),
                                 sum(CASE WHEN IssueStart IS NULL THEN 1 ELSE 0 END)
                          FROM CollectedEditionSpan WHERE Source=3""").fetchone()
gold = con.execute("""SELECT count(*) FROM CollectedEditionSpan
                      WHERE Source=3 AND IssueStart IS NOT NULL AND Confidence >= 0.95""").fetchone()[0]
jr_sh = con.execute("""SELECT count(DISTINCT i.SeriesId) FROM CollectedEditionSpan s JOIN Item i ON i.Id=s.ItemId
                       WHERE s.Source=3 AND s.IssueStart IS NOT NULL""").fetchone()[0]
print(f"CollectedEditionSpan(3) . {jr:,} judged ranges on {jr_sh:,} shelves ({gold:,} at confidence >= 0.95); "
      f"{tomb:,} tombstones")
fl = con.execute("SELECT Flag, count(*), sum(CASE WHEN ReviewState IS NULL OR ReviewState IN ('','Pending','Open') THEN 1 ELSE 0 END) FROM ContainmentFlag GROUP BY 1 ORDER BY 2 DESC")
print("ContainmentFlag ......... " + " · ".join(f"{f} {n} ({o} open)" for f, n, o in fl))
print()

# ── the legs file ────────────────────────────────────────────────────────────────────────────────
lc = {}
for scope, prov, n in con.execute("SELECT Scope, Provider, count(*) FROM legs.LinkCandidates GROUP BY 1,2"):
    lc[(scope, prov)] = n
print("LinkCandidates: " + " · ".join(
    f"Scope {s} {PROV.get(p, p)} {n:,}" for (s, p), n in sorted(lc.items())))
cvkeys = {r[0] for r in con.execute("SELECT Key FROM legs.LinkCandidates WHERE Scope=1 AND Provider=0")}
allkeys = {k for v in ev.keys.values() for k in v}
withc = [s for s in sh if ev.keys.get(s, set()) & cvkeys]
print(f"   {len(cvkeys & allkeys):,} of our {len(allkeys):,} distinct shelf parsed keys have Cv candidates; "
      f"{len(withc):,} shelves reach candidates through at least one key, "
      f"{sum(1 for s in withc if ev.series[s]['cvVolumeId'] is None):,} of those have no CvVolumeId")
for t, label in (("GcdSeries", "GcdSeries"), ("GcdIssue", "GcdIssue"), ("LocgSeriesInference", "LocgSeriesInference"),
                 ("OlSeriesInference", "OlSeriesInference"), ("CvVolumeRaw", "CvVolumeRaw"),
                 ("CvVolumeDescription", "CvVolumeDescription"), ("LocgContainment", "LocgContainment"),
                 ("ProviderResponseCache", "ProviderResponseCache")):
    n = con.execute(f"SELECT count(*) FROM legs.{t}").fetchone()[0]
    print(f"   legs.{label:<22} {n:>10,}")
locg10 = con.execute("SELECT count(*) FROM legs.LocgSeriesInference WHERE Support >= 10").fetchone()[0]
ol5 = con.execute("SELECT count(*) FROM legs.OlSeriesInference WHERE IsbnSupport >= 5").fetchone()[0]
print(f"   bridges above the floor: LOCG Support>=10 {locg10:,} · OL IsbnSupport>=5 {ol5:,}")
print()

# ── THE TIER PARTITION — the number this whole file exists to force ──────────────────────────────
print("=" * 100)
print("Tier partition (PLAN §7-S), with the demotion that put each shelf where it is")
tiers, tier_files, why = {}, {}, {}
for sid in sh:
    t, reason, _detail = ev.tier(sid)
    tiers[t] = tiers.get(t, 0) + 1
    tier_files[t] = tier_files.get(t, 0) + ev.size.get(sid, 0)
    why.setdefault(t, {})
    why[t][reason] = why[t].get(reason, 0) + 1
EST = {"A": 4500, "B": 12000, "C": 2000, "D": 2300}
CEIL = {"A": 150, "B": 100, "C": 60, "D": 120}
print("(batches are sized by packet LINES — ~1,400 each, shelf ceiling per tier — so the batch count below")
print(" is only the floor a full ceiling would give; next_batch.py prints the real lines per batch.)")
for t in ("A", "B", "C", "D"):
    n, f = tiers.get(t, 0), tier_files.get(t, 0)
    print(f"  {t}  {n:>7,} shelves {f:>9,} files    ceiling {CEIL[t]:>3}/batch  ->  >= {-(-n // CEIL[t]):>3} batches"
          f"      (lead's estimate {EST[t]:,})")
    for reason, k in sorted(why.get(t, {}).items(), key=lambda x: -x[1]):
        print(f"          {k:>7,}  {reason}")
shelf_sum = sum(tiers.values())
print(f"  {'':<3}{shelf_sum:>7,} shelves accounted for; population {len(sh):,}; "
      f"UNACCOUNTED {len(sh)-shelf_sum:,}")

print()
print("Item partition — every comic item, not excluded")
item_sum = sum(tier_files.values()) + ev.orphan_items
for t in ("A", "B", "C", "D"):
    print(f"  {tier_files.get(t,0):>9,}  on a tier-{t} shelf")
print(f"  {ev.orphan_items:>9,}  on no comic shelf at all (SeriesId NULL, or a novel shelf's stray)")
print(f"  {'-'*9}")
print(f"  {item_sum:>9,}  accounted for; population {ev.total_items:,}; "
      f"UNACCOUNTED {ev.total_items-item_sum:,}")

# ── THE PROGRESS READOUT (PLAN §12): what has actually been DECIDED, over the population ─────────
# "batches done" is the number that misleads, for the same reason "976 decision files" did: it counts
# work, not shelves. This is the partition PLAN §7-S's acceptance is phrased over, and it has an
# `undecided` bucket that must fall to zero — nothing else in this file can tell you that.
decides, winner, superseded, dupes = idbase.scan_decisions()
DEC_ORDER = ["accepted 1.0  (the collection asserts it and a leg agrees)",
             "accepted 0.95 (two independent legs)",
             "accepted 0.9  (one leg + our own naming/years/count)",
             "in review 0.7 (SeriesMatchReview, not linked)",
             "refused, flagged split-needed",
             "refused, other reason",
             "UNDECIDED"]


def bucket(sid):
    w = winner.get(sid)
    if w is None:
        return DEC_ORDER[6]
    rec = decides[w]
    if rec["kinds"].get(sid) == "R":
        return DEC_ORDER[4] if "split-needed" in rec["flags"].get(sid, ()) else DEC_ORDER[5]
    conf = rec["confs"].get(sid)
    return {"1.0": DEC_ORDER[0], "0.95": DEC_ORDER[1], "0.9": DEC_ORDER[2],
            "0.7": DEC_ORDER[3]}.get(conf, DEC_ORDER[6])


print()
print("=" * 100)
print(f"Decided partition — {len(decides)} decision file(s), "
      f"{sum(1 for f in decides if decides[f]['rank'] is not None)} of them revisits")
dec, dec_by_tier = Counter(), {}
for sid in sh:
    b = bucket(sid)
    dec[b] += 1
    dec_by_tier.setdefault(ev.tier(sid)[0], Counter())[b] += 1
for b in DEC_ORDER:
    per = "  ".join(f"{t} {dec_by_tier.get(t, Counter())[b]:>5,}" for t in ("A", "B", "C", "D"))
    print(f"  {dec[b]:>8,}  {b:<52} {per}")
print(f"  {'-'*8}")
dec_sum = sum(dec.values())
print(f"  {dec_sum:>8,}  accounted for; population {len(sh):,}; UNACCOUNTED {len(sh)-dec_sum:,}")
accepted = sum(dec[b] for b in DEC_ORDER[:3])
two_leg = dec[DEC_ORDER[0]] + dec[DEC_ORDER[1]]
print(f"\n  headline (PLAN §7-S): {two_leg:,} shelf/shelves rest on two independent legs or on the "
      f"collection's own assertion; {accepted:,} linked in all")
if superseded:
    print(f"  {len(superseded)} shelf/shelves re-decided by a revisit; the superseded lines stay on disk")
if dupes:
    print(f"  ⚠ {len(dupes)} shelf/shelves decided in two NON-revisit files — run check_identity.py --all")
extra_flag = sum(1 for sid in sh
                 if winner.get(sid) and decides[winner[sid]]["kinds"].get(sid) == "S"
                 and "split-needed" in decides[winner[sid]]["flags"].get(sid, ()))
if extra_flag:
    print(f"  lead: {extra_flag} accepted shelf/shelves ALSO carry a split-needed flag — the run was "
          f"identified and a colliding sub-run named beside it")

# ── the ITEM-level decided partition ─────────────────────────────────────────────────────────────
# A shelf being identified does not mean its FILES are. The question Eric asked — "which books on an
# accepted shelf still have no record of their own?" — is invisible in the shelf partition above, so it
# gets its own, over the same 118,440 items.
print()
print("=" * 100)
print("Item partition by what is DECIDED about each file")
accepted_sh = {sid for sid in sh if winner.get(sid)
               and decides[winner[sid]]["kinds"].get(sid) == "S"
               and decides[winner[sid]]["confs"].get(sid) in ("1.0", "0.95", "0.9")}
refused_sh = {sid for sid in sh if winner.get(sid) and decides[winner[sid]]["kinds"].get(sid) == "R"}
review_sh = {sid for sid in sh if winner.get(sid)
             and decides[winner[sid]]["kinds"].get(sid) == "S"
             and decides[winner[sid]]["confs"].get(sid) == "0.7"}

# every item carrying an `I` line, read off the decision files with the same precedence
item_lines = set()
for p, rec in decides.items():
    for raw in open(p, encoding="utf-8"):
        line = raw.strip()
        if line.startswith("I "):
            head = line.split("|", 1)[0].split()
            if len(head) > 1 and head[1].isdigit():
                item_lines.add(int(head[1]))

# the cached issue coordinates of every accepted shelf's linked volume / series
cached = {}
for sid in accepted_sh:
    ok = set()
    vid = ev.series[sid]["cvVolumeId"]
    if vid:
        ok |= set(ev.cv_issue_numbers(vid))
    cached[sid] = ok
gcd_nums = {}
for sid, gid in ((s, ev.gcd_verdict(s)[0]) for s in accepted_sh):
    if gid:
        gcd_nums.setdefault(gid, None)
if gcd_nums:
    q = ",".join("?" * len(gcd_nums))
    got = {}
    for gid, n in con.execute(f"SELECT GcdSeriesId, Number FROM legs.GcdIssue WHERE GcdSeriesId IN ({q})",
                              list(gcd_nums)):
        x = idbase.num(n)
        if x is not None:
            got.setdefault(gid, set()).add(x)
    for sid in accepted_sh:
        gid = ev.gcd_verdict(sid)[0]
        if gid and gid in got:
            cached[sid] |= got[gid]

ITEMS = ["issue file on an accepted run, its number IS in the cached CV/GCD list",
         "issue file on an accepted run, its number is NOT in any cached list",
         "book (collection) with an I line of its own",
         "book (collection) on an accepted shelf with NO I line",
         "file on a shelf held for review at 0.7",
         "file on a REFUSED shelf",
         "file on an undecided shelf (or no shelf at all)"]
ic = Counter()
for iid, sid, iscol, ino in con.execute("""
        SELECT i.Id, i.SeriesId, coalesce(cd.IsCollection,0), cd.IssueNo
        FROM Item i LEFT JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0"""):
    if iid in item_lines:
        ic[ITEMS[2]] += 1
    elif sid in accepted_sh:
        if iscol:
            ic[ITEMS[3]] += 1
        else:
            x = idbase.num(ino)
            ic[ITEMS[0] if (x is not None and x in cached.get(sid, ())) else ITEMS[1]] += 1
    elif sid in review_sh:
        ic[ITEMS[4]] += 1
    elif sid in refused_sh:
        ic[ITEMS[5]] += 1
    else:
        ic[ITEMS[6]] += 1
for k in ITEMS:
    print(f"  {ic[k]:>9,}  {k}")
print(f"  {'-'*9}")
ic_sum = sum(ic.values())
print(f"  {ic_sum:>9,}  accounted for; population {ev.total_items:,}; "
      f"UNACCOUNTED {ev.total_items - ic_sum:,}")

bad = ((len(sh) - shelf_sum) or (ev.total_items - item_sum) or (len(sh) - dec_sum)
       or (ev.total_items - ic_sum))
if bad:
    print("\nFAIL: the partition does not sum. A shelf or a file that belongs to no tier is one no batch "
          "will emit and no reader will see.")
else:
    print("\nboth partitions sum")
sys.exit(1 if bad else 0)
