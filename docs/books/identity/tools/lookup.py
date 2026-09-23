"""Ad-hoc name lookup for a reading worker: the same ComicVine rip and GCD dump the packet consulted,
probed with a spelling of YOUR choosing, exact first and then as a substring.

    python lookup.py "Battle Action" [--year 1977] [--contains]
    python lookup.py --issues <cvVolumeId>          the issue ids / numbers of one CV volume
    python lookup.py --collects <gcdIssueId>        what GCD says a trade collects (notes + gcd_reprint)
    python lookup.py --gcd-issues <gcdSeriesId> [--limit 400] [--variants]   GCD's issue rows of one series (TOOLS_TODO 24)
    python lookup.py --gcd-series "<name>" [--year YYYY] [--contains]   the same, for the series matching a name
    python lookup.py --batch <file>                 many of the above in one call, one query per line
    python lookup.py --shelf <sid> [--limit 40]     one shelf: keys (verbatim), files, stored cv/gcd, key links,
                                                    open flags (with their ids), the S/R/F lines in force (TOOLS_TODO 34)
    python lookup.py --who-stores cv=<id>|gcd=<id>  every Series row storing it — file-holding or EMPTY — plus the
                                                    keys whose SeriesKeyLink names it, per-file links, S lines (TODO 34)
    python lookup.py --id cv=<vol> cvi=<issue> gcd=<series> gcdi=<issue>   name, publisher, year, count (TODO 36)

Exact = the normalised name matches (the packet's own rule; both indexes are keyed by `idbase.norm_name`, so a
probe keeps its "of" / "the" / "and" exactly as the record's name does — TOOLS_TODO 30). --contains = the normalised name CONTAINS the
probe, capped at 25 hits per source, sorted by year. --year keeps hits within ±1 of the year.
This is a lookup whose results you read; it decides nothing.

`--batch` (TOOLS_TODO 22) exists because the indexes take a few seconds to load and a reader chasing five
spellings of one title paid that, and a tool round-trip, five times. A batch file holds one query per line in
exactly the shape of the command line (`"Gen 13" --year 1994 --contains`, `--issues 4050`, `--collects 2213270`);
blank lines and `#` comments are skipped, and every answer is headed by the query that produced it.

`--gcd-issues` / `--gcd-series` (TOOLS_TODO 24): the R-029 reader had to hand-write read-only SQLite on the GCD
dump to see a trade line's rows — which book is #3, its page count, its ISBN, what its notes say it collects —
because the packet shows the rows of the shelf's OWN series only, and a stamped `GCD says` row
(`⚠ stored GCD row is another book`) is exactly when the reader needs another series' rows. One line per issue:
id, number, on-sale/key date, pages, ISBN, title, and the parsed "Collects …" clause (gcdnotes.py's parser, the
same one the packet uses). Variant rows are hidden unless `--variants` (a 12-issue run carries 39 of them).
"""
import json
import os
import re
import shlex
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

import idbase
from idbase import Evidence, norm_name
from identity_packet import Ctx
import gcdnotes

_ctx = []


def ctx():
    if not _ctx:
        _ctx.append(Ctx(Evidence()))
    return _ctx[0]


def issues(vol):
    """--issues <volId>: the issue ids of one volume (TOOLS_TODO 12). An `I` line names an ISSUE, and the packet
    mostly shows VOLUMES; ~2,000 I lines were written `cv=-` for want of this. cvref's cv_iss is indexed on
    volId, so it is a lookup, not a scan."""
    c, out = ctx(), []
    rows = c.cvref.execute(
        "SELECT issueId, number, name, coverDate, storeDate FROM cv_iss WHERE volId=? ORDER BY numKey, issueId",
        (vol,)).fetchall() if c.cvref is not None else []
    v = c.cv_index()
    meta = next((r for rs in v.values() for r in rs if r[0] == vol), None)
    if meta:
        out.append(f'volume {vol} "{meta[1]}" {meta[3] or "?"} {meta[5] or "?"} — {meta[4] or "?"} issues per the rip')
    out.append(f"{len(rows)} cached issue(s)")
    for iid, num_, name, cd, sd in rows:
        out.append(f"   issue {iid:<9} #{str(num_ or '?'):<8} {cd or sd or '':<12} {(name or '')[:60]}")
    return out


def collects(gid):
    """--collects <gcdIssueId> (TOOLS_TODO 18): GCD's notes clause and its gcd_reprint roll-up for one issue."""
    n = ctx().notes
    row = n.issue(gid)
    if not row:
        return [f"no GCD issue {gid} in the dump"]
    out = [f"GCD issue {gid}: #{row['number']} of {row['series']} ({row['year']}) s{row['seriesId']}, "
           f"{row['pages'] or '?'}pp"]
    parsed = gcdnotes.parse_notes(row["notes"])
    for e in parsed:
        out.append(f"   notes:   {e['name'] or '?'}{' [s' + str(e['sid']) + ']' if e['sid'] else ''} "
                   f"{gcdnotes.fmt_ranges(e['ranges'])}")
    for sid, nm, rg, k in n.reprints(gid):
        out.append(f"   reprint: s{sid} {nm} {gcdnotes.fmt_ranges(rg) if rg else '(unnumbered)'}  [{k} stories]")
    if not parsed and row["notes"]:
        out.append(f"   (notes carry no collect clause): {row['notes'][:200]!r}")
    return out


def gcd_issues(sid, limit=400, variants=False):
    """--gcd-issues <seriesId>: every issue row of one GCD series, in NUMBER order."""
    g = ctx().gcd
    if g is None:
        return ["(no GCD dump on this machine)"]
    s = g.execute("""SELECT s.id, s.name, s.year_began, s.year_ended, s.issue_count, s.format, p.name, l.code
                     FROM gcd_series s LEFT JOIN gcd_publisher p ON p.id = s.publisher_id
                     LEFT JOIN stddata_language l ON l.id = s.language_id WHERE s.id = ?""", (sid,)).fetchone()
    if not s:
        return [f"no GCD series {sid} in the dump"]
    out = [f'GCD series {s[0]} "{s[1]}" {s[2] or "?"}-{s[3] or "?"} {s[6] or "?"} [{s[5] or ""}]'
           f'{" [" + s[7] + "]" if s[7] and s[7] != "en" else ""} — {s[4] or "?"} issues per GCD']
    rows = g.execute("""SELECT id, number, coalesce(nullif(on_sale_date,''), key_date), page_count, isbn, barcode,
                               title, notes, variant_of_id, variant_name
                        FROM gcd_issue WHERE series_id = ? AND coalesce(deleted,0) = 0""", (sid,)).fetchall()
    # numeric order: GCD's sort_code is not reliable on small trade series (Austen's 6 came back #6, #5, #1 …)
    rows.sort(key=lambda r: (idbase.num(r[1]) is None, idbase.num(r[1]) or 0, str(r[1] or ""), r[0]))
    nvar = sum(1 for r in rows if r[8])
    if not variants:
        rows = [r for r in rows if not r[8]]
    out.append(f"{len(rows)} row(s){' (first ' + str(limit) + ' shown)' if len(rows) > limit else ''}"
               + (f"; {nvar} variant row(s) {'shown' if variants else 'hidden (--variants shows them)'}" if nvar else ""))
    for iid, number, date, pages, isbn, barcode, title, notes, var_of, var_name in rows[:limit]:
        parsed = gcdnotes.parse_notes(notes)
        says = "; ".join(f"{e['name'] or '?'} {gcdnotes.fmt_ranges(e['ranges'])}" for e in parsed)
        code = isbn or barcode or ""
        out.append(f"   issue {iid:<9} #{str(number or '?'):<7} {str(date or ''):<10} "
                   f"{(str(int(pages)) + 'pp') if pages else '?pp':>6}  {code[:32]:<32} "
                   f"{(title or '')[:48]}"
                   + (f"  [variant of {var_of}: {var_name or '?'}]" if var_of else "")
                   + (f"\n{'':>21}collects: {says[:200]}" if says else ""))
    return out


def gcd_series_named(text, year=None, contains=False, max_series=3, limit=400, variants=False):
    """--gcd-series "<name>": the series the name matches (the packet's normalized-name rule), then the issue
    rows of the first `max_series` of them, by year."""
    probe = norm_name(text)
    gc = ctx().gcd_index()
    hits = list(gc.get(probe, ()))
    if contains:
        hits += [r for k, rs in gc.items() if probe in k and k != probe for r in rs]
    if year is not None:
        hits = [h for h in hits if h[2] is not None and abs(int(h[2]) - year) <= 1]
    hits = sorted(hits, key=lambda h: (h[2] or 0, h[0]))
    out = [f"GCD dump — {len(hits)} series for '{probe}'" + (f" year {year}±1" if year else "")]
    for h in hits[:25]:
        out.append(f'   {h[0]} "{h[1]}" {h[2] or "?"}-{h[3] or "?"} {h[6] or "?"} [{h[5] or ""}] {h[4] or "?"} issues')
    for h in hits[:max_series]:
        out.append("")
        out += gcd_issues(h[0], limit, variants)
    if len(hits) > max_series:
        out.append(f"\n({len(hits) - max_series} more series matched — `--gcd-issues <id>` for any of them)")
    return out


def names(text, year=None, contains=False):
    c, out = ctx(), []
    probe = norm_name(text)

    def keep(y):
        return year is None or (y is not None and abs(int(y) - year) <= 1)

    cv = c.cv_index()
    hits = list(cv.get(probe, ()))
    if contains:
        hits += [r for k, rs in cv.items() if probe in k and k != probe for r in rs]
    hits = [h for h in hits if keep(h[3])][:25]
    out.append(f"ComicVine rip — {len(hits)} hit(s) for '{probe}'" + (f" year {year}±1" if year else ""))
    for h in sorted(hits, key=lambda h: (h[3] or 0)):
        out.append(f'   {h[0]} "{h[1]}" {h[3] or "?"} {h[5] or "?"} {h[4] or "?"} issues')
    gc = c.gcd_index()
    hits = list(gc.get(probe, ()))
    if contains:
        hits += [r for k, rs in gc.items() if probe in k and k != probe for r in rs]
    hits = [h for h in hits if keep(h[2])][:25]
    out.append(f"GCD dump — {len(hits)} hit(s)")
    for h in sorted(hits, key=lambda h: (h[2] or 0)):
        out.append(f'   {h[0]} "{h[1]}" {h[2] or "?"}-{h[3] or "?"} {h[6] or "?"} [{h[5] or ""}] {h[4] or "?"} issues')
    return out


# ── TOOLS_TODO 34 / 36: shelves and ids, read off books.db (read-only) — no Evidence load ─────────────
# R-032's reader spent ~12 calls building a shelf's keys / stored ids / line in force by hand, and the P-003 and
# P-004 readers each wrote a scratch sqlite helper to see a shelf's ParsedKey verbatim (a `join` key must match
# EXACTLY). These answer from one read-only connection; nothing here loads the name indexes.
_hot, _dec = [], []
LINK_STATUS = {1: "Matched", 5: "Manual", 6: "Cleared"}


def hot():
    if not _hot:
        _hot.append(idbase.open_hot())
    return _hot[0]


def decisions():
    if not _dec:
        _dec.append(idbase.scan_decisions())
    return _dec[0]


def q(s):
    """A key exactly as stored — quoted, so trailing spaces and odd punctuation are visible."""
    return json.dumps(s, ensure_ascii=False)


def shelf_keys(sid):
    c = hot()
    ks = {r[0] for r in c.execute("SELECT ParsedKey FROM SeriesAlias WHERE SeriesId = ? AND ParsedKey IS NOT NULL",
                                  (sid,))}
    own = c.execute("SELECT ParsedKey FROM Series WHERE Id = ?", (sid,)).fetchone()
    if own and own[0]:
        ks.add(own[0])
    return sorted(ks), (own[0] if own else None)


def files_of(sid):
    return hot().execute("""SELECT i.Id, i.FileName, coalesce(i.PageCount,0), coalesce(cd.IsCollection,0)
                            FROM Item i LEFT JOIN ComicDetail cd ON cd.ItemId = i.Id
                            WHERE i.SeriesId = ? AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0
                            ORDER BY i.Path, i.FileName""", (sid,)).fetchall()


def merged_into(sid):
    c, seen = hot(), set()
    while sid not in seen:
        seen.add(sid)
        r = c.execute("SELECT NewSeriesId FROM SeriesMerge WHERE OldSeriesId = ? AND NewSeriesId IS NOT NULL "
                      "ORDER BY MergedAt DESC LIMIT 1", (sid,)).fetchone()
        if not r:
            return sid
        if c.execute("SELECT 1 FROM Series WHERE Id = ?", (r[0],)).fetchone():
            return r[0]
        sid = r[0]
    return sid


def lines_in_force(sid):
    """(file base, [the S/R/F lines for `sid` in the WINNING decision file], [earlier files]) — idbase's precedence."""
    decides, winner, superseded, _d = decisions()
    w = winner.get(sid)
    if not w:
        return None, [], []
    out = []
    for raw in open(w, encoding="utf-8"):
        t = raw.strip()
        m = re.match(r"^([SRF])\s+S?(\d+)\b", t)
        if m and int(m.group(2)) == sid:
            out.append(t)
    return (os.path.splitext(os.path.basename(w))[0], out,
            [os.path.splitext(os.path.basename(f))[0] for f in superseded.get(sid, ())])


def id_info(leg, v):
    """--id: one line naming a provider id — cv (volume), cvi (CV issue), gcd (series), gcdi (GCD issue)."""
    c = ctx_light()
    if leg == "cv":
        r = hot().execute("SELECT Name, StartYear, PublisherName, CountOfIssues FROM CvVolume WHERE Id = ?",
                          (v,)).fetchone()
        held = bool(r)
        if not r and c["cvref"] is not None:
            r = c["cvref"].execute("SELECT name, year, publisherName, issueCount FROM cv_vol WHERE volId = ?",
                                   (v,)).fetchone()
        if not r:
            return f"cv={v}: no such ComicVine volume in CvVolume or cvref"
        n = c["cvref"].execute("SELECT count(*) FROM cv_iss WHERE volId = ?", (v,)).fetchone()[0] \
            if c["cvref"] is not None else "?"
        return (f'cv={v} "{r[0]}" {r[1] or "?"} {r[2] or "?"} — {r[3] or "?"} issues ({n} cached)'
                f'{"" if held else "  [not in CvVolume: named from the rip — F needs-fetch]"}')
    if leg == "cvi":
        r = c["cvref"].execute("SELECT volId, number, name, coverDate FROM cv_iss WHERE issueId = ?",
                               (v,)).fetchone() if c["cvref"] is not None else None
        if not r:
            r = hot().execute("SELECT VolumeId, IssueNumber, NULL, NULL FROM CvIssue WHERE Id = ?", (v,)).fetchone()
        if not r:
            return f"cvi={v}: no such ComicVine issue in cvref or CvIssue"
        return f"cvi={v} #{r[1] or '?'} {r[3] or ''} {(r[2] or '')[:50]} — of {id_info('cv', r[0])}"
    if leg == "gcd":
        g = c["gcd"]
        r = g.execute("""SELECT s.name, s.year_began, s.year_ended, p.name, s.issue_count, s.format
                         FROM gcd_series s LEFT JOIN gcd_publisher p ON p.id = s.publisher_id WHERE s.id = ?""",
                      (v,)).fetchone() if g is not None else None
        if not r:
            r = hot().execute("SELECT Name, YearBegan, YearEnded, Publisher, IssueCount, Format FROM legs.GcdSeries "
                              "WHERE GcdSeriesId = ?", (v,)).fetchone()
        if not r:
            return f"gcd={v}: no such GCD series in the dump or legs.GcdSeries"
        return f'gcd={v} "{r[0]}" {r[1] or "?"}-{r[2] or "?"} {r[3] or "?"} [{r[5] or ""}] — {r[4] or "?"} issues'
    if leg == "gcdi":
        g = c["gcd"]
        r = g.execute("SELECT series_id, number, coalesce(nullif(on_sale_date,''), key_date), page_count, title "
                      "FROM gcd_issue WHERE id = ?", (v,)).fetchone() if g is not None else None
        if not r:
            r = hot().execute("SELECT GcdSeriesId, NULL, NULL, NULL, NULL FROM legs.GcdIssue WHERE GcdIssueId = ?",
                              (v,)).fetchone()
        if not r:
            return f"gcdi={v}: no such GCD issue in the dump or legs.GcdIssue"
        pp = f"{int(r[3])}pp " if r[3] else ""
        return f"gcdi={v} #{r[1] or '?'} {r[2] or ''} {pp}{(r[4] or '')[:40]} — of {id_info('gcd', r[0])}"
    return f"{leg}={v}: unknown kind (cv= cvi= gcd= gcdi=)"


_light = {}


def ctx_light():
    """The dumps alone (no name index): --id must answer in well under a second."""
    if not _light:
        _light["cvref"] = idbase.open_cv_ref()
        _light["gcd"] = idbase.open_gcd_dump()
    return _light


def shelf(sid, limit=40):
    c, out = hot(), []
    row = c.execute("""SELECT Id, coalesce(DisplayNameOverride, Name), CanonicalKey, CvVolumeId, YearStart, YearEnd
                       FROM Series WHERE Id = ?""", (sid,)).fetchone()
    if not row:
        now = merged_into(sid)
        if now != sid:
            out.append(f"S{sid} was merged away by a landed wave -> S{now} (shown below)")
            base, lines, _o = lines_in_force(sid)
            if base:
                out.append(f"   S{sid}'s lines in force: {base}")
                out += [f"      {t[:300]}" for t in lines]
            return out + shelf(now, limit)
        return [f"S{sid}: no such Series row, and SeriesMerge has no record of it"]
    fs = files_of(sid)
    keys, own = shelf_keys(sid)
    out.append(f'S{sid} "{row[1]}"  canonical {row[2]}  years {row[4] or "?"}-{row[5] or "?"}  '
               f'{len(fs)} file(s) / {sum(1 for f in fs if f[3])} collection(s)'
               + ("  — EMPTY: a Series row holding no comic files (still a resolve SURVIVOR for its canonical key)"
                  if not fs else ""))
    out.append(f"   stored cv: {id_info('cv', row[3]) if row[3] else '(none)'}")
    out.append(f"   keys ({len(keys)}), verbatim:")
    for k in keys:
        links = c.execute("SELECT Provider, ProviderKey, Status FROM SeriesKeyLink WHERE ParsedKey = ? "
                          "AND Provider IN (0, 3) ORDER BY Provider", (k,)).fetchall()
        n = c.execute("SELECT count(*) FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id WHERE i.SeriesId = ? "
                      "AND cd.ParsedSeriesKey = ?", (sid, k)).fetchone()[0]
        lk = "; ".join(f"{'cv' if p == 0 else 'gcd'}={pk if pk is not None else '-'} "
                       f"{LINK_STATUS.get(st, 'status ' + str(st))}" for p, pk, st in links) or "no cv/gcd key link"
        out.append(f"      {q(k)}{'  (Series.ParsedKey)' if k == own else ''}  {n} file(s)  [{lk}]")
    per = {}
    for prov, sec, cnt in c.execute("""SELECT l.Provider, l.SecondaryKey, count(*) FROM ItemProviderLink l
                                       JOIN Item i ON i.Id = l.ItemId
                                       WHERE i.SeriesId = ? AND l.Status IN (1, 5) AND l.Provider IN (0, 3)
                                       GROUP BY l.Provider, l.SecondaryKey ORDER BY 3 DESC""", (sid,)):
        per.setdefault("cv" if prov == 0 else "gcd", []).append(f"{sec}x{cnt}")
    for leg in ("cv", "gcd"):
        if per.get(leg):
            out.append(f"   per-file {leg}: {', '.join(per[leg][:8])}{' …' if len(per[leg]) > 8 else ''}")
    for fid, iid, flag, state, detail in c.execute(
            "SELECT Id, ItemId, Flag, ReviewState, Detail FROM ContainmentFlag WHERE SeriesId = ? ORDER BY Id", (sid,)):
        if state in idbase.OPEN_FLAG_STATES:
            out.append(f"   OPEN flag {fid} {flag} (item {iid}): {(detail or '')[:120]}")
    base, lines, older = lines_in_force(sid)
    if base:
        out.append(f"   in force: {base}" + (f"  (supersedes {', '.join(older)})" if older else ""))
        for t in lines:
            out.append(f"      {t[:300]}{' …' if len(t) > 300 else ''}")
    else:
        out.append("   in force: (no decision file names this shelf)")
    for iid, fn, pp, col in fs[:limit]:
        out.append(f"   [{iid}]{' COL' if col else ''} {fn} ({pp or '?'}pp)")
    if len(fs) > limit:
        out.append(f"   … {len(fs) - limit} more file(s) (--limit N)")
    return out


def who_stores(spec):
    """--who-stores cv=<id>|gcd=<id>: every place the id sits — the question behind every undeclared merge."""
    leg, _, v = spec.partition("=")
    if leg not in ("cv", "gcd") or not v.strip().lstrip("s").isdigit():
        return [f"--who-stores wants cv=<volumeId> or gcd=<seriesId>, got {spec!r}"]
    v = int(v.strip().lstrip("s"))
    c, out = hot(), [id_info(leg, v)]
    prov = idbase.P_CV if leg == "cv" else idbase.P_GCD

    def label(sid):
        r = c.execute("SELECT coalesce(DisplayNameOverride, Name) FROM Series WHERE Id = ?", (sid,)).fetchone()
        if not r:
            now = merged_into(sid)
            return f"S{sid} (gone{'; merged into S' + str(now) if now != sid else ''})"
        n = len(files_of(sid))
        return f'S{sid} "{r[0]}" {n}f' + ("  EMPTY" if not n else "")

    if leg == "cv":
        rows = c.execute("SELECT Id FROM Series WHERE CvVolumeId = ? ORDER BY Id", (v,)).fetchall()
        out.append(f"Series.CvVolumeId = {v}: {len(rows)} row(s)")
        for (sid,) in rows:
            out.append(f"   {label(sid)}")
    links = c.execute("""SELECT k.ParsedKey, k.Status,
                                (SELECT Id FROM Series WHERE ParsedKey = k.ParsedKey LIMIT 1),
                                (SELECT SeriesId FROM SeriesAlias WHERE ParsedKey = k.ParsedKey LIMIT 1)
                         FROM SeriesKeyLink k WHERE k.Provider = ? AND k.ProviderKey = ?
                         ORDER BY k.ParsedKey""", (prov, v)).fetchall()
    out.append(f"SeriesKeyLink {leg}={v}: {len(links)} key(s)")
    for k, st, own, al in links:
        sid = own if own is not None else al
        out.append(f"   {q(k)} {LINK_STATUS.get(st, 'status ' + str(st))} -> "
                   + (label(sid) + (" (alias)" if own is None else "") if sid is not None else "(no Series owns this key)"))
    per = c.execute("""SELECT i.SeriesId, count(*) FROM ItemProviderLink l JOIN Item i ON i.Id = l.ItemId
                       WHERE l.Provider = ? AND l.Status IN (1, 5) AND (l.SecondaryKey = ? OR l.SecondaryKey = ?)
                         AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0
                       GROUP BY i.SeriesId ORDER BY 2 DESC""", (prov, str(v), v)).fetchall()
    out.append(f"per-file links {leg}={v}: {sum(n for _s, n in per)} file(s) on {len(per)} shelf/shelves")
    for sid, n in per[:15]:
        out.append(f"   {label(sid)}: {n} file(s)")
    decides, winner, _s, _d = decisions()
    s_lines = []
    for p, rec in decides.items():
        for sid, x in rec[leg].items():
            if x == v:
                s_lines.append((sid, os.path.splitext(os.path.basename(p))[0], winner.get(sid) == p))
    out.append(f"S lines naming {leg}={v}: {len(s_lines)}")
    for sid, b, wins in sorted(s_lines):
        out.append(f"   {label(sid)}  {b}{'' if wins else '  (superseded)'}")
    return out


def run(argv):
    """One query in command-line shape -> its answer lines."""
    limit = int(argv[argv.index("--limit") + 1]) if "--limit" in argv else None
    if "--shelf" in argv:
        return shelf(int(argv[argv.index("--shelf") + 1].lstrip("S")), limit or 40)
    if "--who-stores" in argv:
        return who_stores(argv[argv.index("--who-stores") + 1])
    if "--id" in argv:
        out = []
        for t in argv[argv.index("--id") + 1:]:
            if t.startswith("--"):
                break
            leg, _, v = t.partition("=")
            v = v.lstrip("s")
            out.append(id_info(leg, int(v)) if v.isdigit() else f"{t}: want cv=|cvi=|gcd=|gcdi=<number>")
        return out or ["--id wants cv=<vol> cvi=<issue> gcd=<series> gcdi=<issue>"]
    if "--issues" in argv:
        return issues(int(argv[argv.index("--issues") + 1]))
    if "--collects" in argv:
        return collects(int(argv[argv.index("--collects") + 1]))
    limit = int(argv[argv.index("--limit") + 1]) if "--limit" in argv else 400
    if "--gcd-issues" in argv:
        return gcd_issues(int(argv[argv.index("--gcd-issues") + 1].lstrip("s")), limit, "--variants" in argv)
    year = None
    if "--year" in argv:
        year = int(argv[argv.index("--year") + 1])
    skip = {argv.index(f) + 1 for f in ("--year", "--limit") if f in argv}
    args = [a for k, a in enumerate(argv) if not a.startswith("--") and k not in skip]
    if not args:
        return ["(no name to probe)"]
    if "--gcd-series" in argv:
        return gcd_series_named(args[0], year, "--contains" in argv, limit=limit, variants="--variants" in argv)
    return names(args[0], year, "--contains" in argv)


def main():
    argv = sys.argv[1:]
    if not argv:
        raise SystemExit(__doc__)
    if "--batch" in argv:
        path = argv[argv.index("--batch") + 1]
        if not os.path.isfile(path):
            raise SystemExit(f"no such batch file: {path}")
        n = 0
        for raw in open(path, encoding="utf-8"):
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            n += 1
            print(f"\n>>> {line}")
            try:
                q = shlex.split(line, posix=True)
                print("\n".join(run(q)))
            except (ValueError, IndexError) as e:
                print(f"   (unreadable query: {e})")
        print(f"\n{n} quer{'y' if n == 1 else 'ies'}")
        return
    print("\n".join(run(argv)))


if __name__ == "__main__":
    main()
