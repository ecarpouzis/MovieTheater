"""The evidence packet for one shelf — everything the reader needs to decide its identity, and nothing pre-decided.

`python identity_packet.py <sid> [sid...] [--out FILE]`

Descended from `export_packets_v3.py`, whose lesson was that reduction throws away the answer: the thing
that settled Baltimore was in the FILENAMES, and v2 had reduced them to bare issue numbers. So every
filename is printed here too, with one exception written into PLAN §7-S and implemented below as the FOLD
RULE — a run of six or more consecutive files differing in exactly one number, with page counts within 2x
of each other, is a ladder, and printing 300 rungs of it teaches the reader nothing that
"Batman 001 … Batman 300 (300 files, pp 22-28)" does not. Collections are never folded: a collected
edition's title is an identity claim, and it is the claim §3.1 tests.

The packet decides nothing. Every arithmetic line (year gap, count ratio, judged-range fit) is a lie
detector for the reader, per PLAN §0 and §4.4 — it is printed, never applied.
"""
import json
import os
import re
import sys
from collections import Counter, defaultdict

import gcdnotes
import idbase
from idbase import Evidence, num, norm_name, short_path

RX_DIGITS = re.compile(r"\d+")
# A 60-trade shelf would otherwise print 60 notes lines; the first twenty show the shape, the rest are a lookup.
GCD_SAYS_CAP = 20


# ── the FOLD RULE ────────────────────────────────────────────────────────────────────────────────
def skeleton(fn):
    """The filename with every digit group REMOVED. Two files are rungs of the same ladder when this is
    identical — every number in the name is free to vary, so `Crisis 017 [1989-04-29]` and
    `Crisis 018 [1989-05-13]` fold (issue number and cover date both move), while
    `Baltimore 016 - The Infernal Train 01 (of 03)` never folds against a differently-titled neighbour,
    because its TEXT differs and that text was the evidence that settled the shelf (export_packets_v3.py)."""
    return RX_DIGITS.sub("", fn or "")


def fold_files(rows):
    """rows: [(itemId, fileName, pages, isCollection)] in shelf order -> printable lines.

    A fold is offered only for >= 6 CONSECUTIVE non-collection files sharing a skeleton whose known page
    counts lie within 2x. First and last print verbatim, so the pattern and its span stay visible.
    Collections always print in full and break a run: a collected edition's title is an identity claim."""
    out, i, n = [], 0, len(rows)
    while i < n:
        if rows[i][3]:                                   # a collection: always in full, and it breaks a run
            out.append(("full", rows[i]))
            i += 1
            continue
        sk = skeleton(rows[i][1])
        j = i + 1
        while j < n and not rows[j][3] and skeleton(rows[j][1]) == sk:
            j += 1
        run = rows[i:j]
        pp = [r[2] for r in run if r[2]]
        if len(run) >= 6 and (not pp or max(pp) <= 2 * min(pp)):
            out.append(("fold", run))
        else:
            out.extend(("full", r) for r in run)
        i = j
    return out


def ladder(nums):
    """The present issue numbers as runs, the shape `brief.py` prints — the fastest way to see a gap,
    a relaunch that restarts at #1, or a number used twice."""
    have = sorted(set(nums))
    runs, s = [], None
    for k, v in enumerate(have):
        if s is None:
            s = v
        if k + 1 == len(have) or have[k + 1] != v + 1:
            runs.append((s, v))
            s = None
    return ", ".join(f"{a:g}-{b:g}" if a != b else f"{a:g}" for a, b in runs)


def _lang_tag(lang, country):
    """Short and silent by default. English-in-the-US is the library's overwhelming case and prints
    nothing; anything else prints, and the country is appended only when it is not simply the language's
    own home (so `fr`/`be` shows as [fr-be] and `de`/`de` as [de])."""
    lang = (lang or "").lower()
    country = (country or "").lower()
    if not lang:
        return f"[?-{country}]" if country else ""
    if lang == "en" and country in ("us", ""):
        return ""
    return f"[{lang}-{country}]" if country and country != lang else f"[{lang}]"


class Ctx:
    """The dump connections and the lookups that need them, opened once per run."""

    def __init__(self, ev):
        self.ev = ev
        self.gcd = idbase.open_gcd_dump()
        self.cvref = idbase.open_cv_ref()
        self.cvrip = None
        self._gcd_index = None
        self._cv_index = None
        # GCD's own statement of what a trade collects (TOOLS_TODO 18 + 21) — one parser, shared with the
        # population contradiction check, so the packet and the count cannot read a note two ways
        self.notes = gcdnotes.Notes(self.gcd, ev.con)
        self._judged = None

    def judged(self):
        """({itemId: (a, b)} Curated spans, {itemId: [(a, b)]} their run rows) — read once per run."""
        if self._judged is None:
            self._judged = gcdnotes.judged_ranges(self.ev.con)
        return self._judged

    # The GCD dump has an index on gcd_series.name, but SQLite will not use a BINARY index for a
    # case-insensitive LIKE, and the dump's spellings do not match ours case for case. gcd_series is
    # 230,057 rows and loads in 0.7 s, so the whole name column is folded in memory once instead.
    def gcd_index(self):
        if self._gcd_index is None:
            self._gcd_index = defaultdict(list)
            self._gcd_tag = {}
            if self.gcd is not None:
                # language and country come from stddata_language / stddata_country — NOT `gcd_language`
                # or `gcd_country`, which do not exist. A reader found that every Le Lombard / Dupuis pair
                # was one French row and one Dutch row of the same album, and the packet could not show it:
                # both printed as the same name, same years, same publisher.
                for row in self.gcd.execute("""
                        SELECT s.id, s.name, s.year_began, s.year_ended, s.issue_count, s.format, p.name,
                               l.code, c.code
                        FROM gcd_series s
                        LEFT JOIN gcd_publisher p ON p.id = s.publisher_id
                        LEFT JOIN stddata_language l ON l.id = s.language_id
                        LEFT JOIN stddata_country c ON c.id = s.country_id
                        WHERE coalesce(s.deleted,0) = 0"""):
                    self._gcd_index[norm_name(row[1])].append(row[:7])
                    self._gcd_tag[row[0]] = _lang_tag(row[7], row[8])
        return self._gcd_index

    def gcd_tag(self, gid):
        """`[fr-be]`, `[nl]`, `[en-gb]` — empty for the ordinary English/US row, so the common case costs
        nothing and the odd one is impossible to miss."""
        self.gcd_index()
        return (self._gcd_tag or {}).get(gid, "")

    # cvref.db IS the usable ComicVine rip lookup: 153,805 volumes with `normName` and an index on it
    # (ix_vol_norm). comicdb_comicvine_20260122.db is 14.5 GB of raw_api_response keyed by id — good for
    # resolving ONE id, useless for a name search — so it is opened only when an id needs a name.
    def cv_index(self):
        if self._cv_index is None:
            self._cv_index = defaultdict(list)
            if self.cvref is not None:
                for row in self.cvref.execute(
                        "SELECT volId, name, normName, year, issueCount, publisherName FROM cv_vol"):
                    self._cv_index[row[2] or norm_name(row[1])].append(row)
        return self._cv_index

    def cv_issue_ids(self, vid, cap=4):
        """The ISSUE ids of a volume, from cvref's `cv_iss` (indexed on volId).

        Readers wrote ~2,000 `I` lines with `cv=-` because the packet exposed only VOLUME ids, and an `I`
        line wants the issue. For a one-issue volume — a trade's or an OGN's own record, which is the case
        an `I` line is usually about — the issue id is the answer and there is exactly one, so it prints
        beside the volume and the reader never has to go looking."""
        if self.cvref is None:
            return []
        return self.cvref.execute(
            "SELECT issueId, number, coverDate FROM cv_iss WHERE volId=? ORDER BY numKey, issueId LIMIT ?",
            (vid, cap)).fetchall()

    def cv_rip_name(self, vid):
        """One volume out of the 14.5 GB rip, by primary key (~30 ms). Used to name a CV id we hold no
        CvVolume row for, and by check_identity.py to prove an id is real."""
        if self.cvrip is None:
            self.cvrip = idbase.open_cv_rip() or False
        if not self.cvrip:
            return None
        for table in ("cv_volume", "cv_volumes"):
            try:
                r = self.cvrip.execute(f"SELECT raw_api_response FROM {table} WHERE id = ?", (vid,)).fetchone()
            except Exception:
                continue
            if r and r[0]:
                try:
                    d = json.loads(r[0])
                except ValueError:
                    continue
                pub = (d.get("publisher") or {}).get("name") if isinstance(d.get("publisher"), dict) else None
                return {"id": vid, "name": d.get("name"), "startYear": d.get("start_year"),
                        "publisher": pub, "countOfIssues": d.get("count_of_issues")}
        return None


def _emb(con, sid):
    vals = defaultdict(Counter)
    for series, vol, count, pub, web in con.execute("""
            SELECT e.Series, e.Volume, e.Count, e.Publisher, e.Web
            FROM ComicEmbedded e JOIN Item i ON i.Id = e.ItemId
            WHERE i.SeriesId = ? AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0""", (sid,)):
        for k, v in (("Series", series), ("Volume", vol), ("Count", count), ("Publisher", pub)):
            if v not in (None, ""):
                vals[k][str(v)] += 1
        if web:
            for m in idbase.RX_CV_WEB.finditer(web):
                vals["Web"][m.group(1)] += 1
    return vals


def packet(sid, ev, ctx):
    con, L = ev.con, []
    s = ev.series.get(sid)
    if s is None:
        return [f"== S{sid}  (no longer a file-holding comic shelf)"]
    tier, reason, detail = ev.tier(sid)
    keys = sorted(ev.keys.get(sid, ()))
    nfiles, ncol = ev.size.get(sid, 0), ev.collections.get(sid, 0)
    years = f"{s['yearStart'] or '?'}-{s['yearEnd'] or '?'}"
    L.append(f"== S{sid} {s['name']}  [tier {tier}] {'· ' + (detail or reason) if tier == 'C' else ''}"
             f"  keys: {' | '.join(keys) or '(none)'}  years {years}  {nfiles} files / {ncol} collections")

    opens = ev.flags.get(sid, ())
    if opens:
        L.append("   flags: " + " · ".join(
            f"{f['flag']} [{f['state'] or 'Pending'}]{' item ' + str(f['itemId']) if f['itemId'] else ''}"
            f"{' — ' + f['detail'][:90] if f['detail'] else ''}" for f in opens))

    rows = con.execute("""
        SELECT i.Id, i.FileName, i.Path, i.PageCount, coalesce(cd.IsCollection,0), cd.IssueNo, cd.VolumeNo,
               cd.Year, cd.Publisher, cd.FormatRaw
        FROM Item i LEFT JOIN ComicDetail cd ON cd.ItemId = i.Id
        WHERE i.SeriesId = ? AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0
        ORDER BY i.Path, i.FileName""", (sid,)).fetchall()

    folders = Counter(short_path(os.path.dirname(r[2] or "")) for r in rows)
    L.append("   folders: " + " · ".join(f"[{n}] {f}" for f, n in folders.most_common()))

    printed = fold_files([(r[0], r[1], r[3], r[4]) for r in rows])
    parts = []
    for kind, payload in printed:
        if kind == "fold":
            pp = [x[2] for x in payload if x[2]]
            span = f", pp {min(pp)}-{max(pp)}" if pp else ""
            parts.append(f"{payload[0][1]} … {payload[-1][1]}  ({len(payload)} files{span})")
        else:
            iid, fn, pc, iscol = payload
            parts.append(f"[{iid}]{' COL' if iscol else ''} {fn} ({pc or '?'}pp)")
    # wrapped, not truncated: a 300-file shelf folds to a handful of rungs, and an unfoldable one is long
    # BECAUSE it is irregular, which is the evidence.
    line, wrapped = "   files:   ", []
    for p in parts:
        if len(line) + len(p) > 200 and line.strip() not in ("files:",):
            wrapped.append(line.rstrip(" |"))
            line = "            "
        line += p + " | "
    wrapped.append(line.rstrip(" |"))
    L.extend(wrapped)

    # ── what the collection itself asserts (PLAN §4.2 — this outranks every leg) ─────────────────
    nums = [num(r[5]) for r in rows if not r[4] and num(r[5]) is not None]
    ours = []
    if nums:
        ours.append("ladder " + ladder(nums))
        dupes = sorted(k for k, v in Counter(nums).items() if v > 1)
        if dupes:
            ours.append(f"{len(dupes)} number(s) name more than one file: "
                        + ", ".join(f"{d:g}" for d in dupes[:12]))
    for iid, a, b, conf, title in con.execute("""
            SELECT s.ItemId, s.IssueStart, s.IssueEnd, s.Confidence, s.EditionTitle
            FROM CollectedEditionSpan s JOIN Item i ON i.Id = s.ItemId
            WHERE i.SeriesId = ? AND s.Source = 3 AND s.IssueStart IS NOT NULL
            ORDER BY s.IssueStart""", (sid,)):
        ours.append(f"judged item {iid} #{a:g}-{b:g} conf {conf if conf is not None else '?'}")
    emb = _emb(con, sid)
    for k in ("Series", "Volume", "Count", "Publisher", "Web"):
        if emb.get(k):
            ours.append(f"ComicInfo {k}: " + ", ".join(f'"{v}"x{n}' for v, n in emb[k].most_common(4)))
    codes = con.execute("""SELECT b.ItemId, b.CodesJson FROM legs.BarcodeScan b JOIN Item i ON i.Id = b.ItemId
                           WHERE i.SeriesId = ? AND b.CodesJson IS NOT NULL AND b.CodesJson NOT IN ('','[]')""",
                        (sid,)).fetchall()
    for iid, js in codes[:4]:
        ours.append(f"barcode item {iid} {js[:60]}")
    if ours:
        L.append("   ours:    " + "; ".join(ours))

    # ── what GCD says each trade collects (TOOLS_TODO 18 + 21). Only books GCD has a statement about print,
    # so a shelf of floppies costs nothing; a contradicted judged range is flagged, never corrected.
    judged, runs = ctx.judged()
    says = []
    for r in rows:
        if not r[4]:
            continue
        got = ctx.notes.book_lines(r[0], judged.get(r[0]), runs.get(r[0], ()), indent="")
        if got:
            says.append(f"[{r[0]}] " + "  |  ".join(got))
    if says:
        L.append("   GCD says: " + says[0])
        L.extend("             " + x for x in says[1:GCD_SAYS_CAP])
        if len(says) > GCD_SAYS_CAP:
            L.append(f"             … {len(says) - GCD_SAYS_CAP} more book(s) with GCD notes (`python gcdnotes.py "
                     f"--issue <id>`)")

    # ── the legs. Every id printed once: a block that only repeats the linked volume taught the reader
    # nothing on 780 of 783 tier-A shelves, so it is reduced to a statement that the lookup AGREED. The
    # moment anything disagrees, the full block comes back — disagreement is the whole signal (§4.3).
    a = ev.arithmetic(sid)
    cv_nums = []
    shown_cv = set()
    perfile = ev.cv_files.get(sid, {})
    linked = s["cvVolumeId"]
    perfile_agrees = bool(perfile) and linked is not None and set(perfile) == {linked}

    if linked:
        shown_cv.add(linked)
        v = ev.cv_volume.get(linked) or ctx.cv_rip_name(linked)
        if v:
            head = (f'vol {linked} "{v["name"]}" {v["startYear"] or "?"} {v["publisher"] or "?"} '
                    f'{v["countOfIssues"] or "?"} issues{_issue_note(linked, v["countOfIssues"], ctx)}')
            if linked not in ev.cv_volume:
                head += "  (NO CvVolume ROW — named from the local rip; Phase C.1 must fetch it)"
        else:
            head = f"vol {linked}  (no CvVolume row and no record in the local rip)"
        span = ev.cv_issue_span.get(linked)
        if span:
            cv_nums = ev.cv_issue_numbers(linked)
            head += f"; cached #{span[1]}-{span[2]} ({span[0]})"
        else:
            head += "; issue list NOT CACHED"
        fit = "none cached"
        if cv_nums:
            js = con.execute("""SELECT s.IssueStart, s.IssueEnd FROM CollectedEditionSpan s JOIN Item i ON i.Id=s.ItemId
                                WHERE i.SeriesId=? AND s.Source=3 AND s.IssueStart IS NOT NULL""", (sid,)).fetchall()
            fit = (f"{sum(1 for x, y in js if any(x <= q <= y for q in cv_nums))} of {len(js)} judged fit"
                   if js else "no judged range")
        head += (f"; yearΔ {a['yearGap'] if a['yearGap'] is not None else '?'}"
                 f"; ratio {a['ourIssueFiles']}/{a['cvCount'] or '?'}"
                 f"{' = %.2f' % a['ratio'] if a['ratio'] is not None else ''}; {fit}")
        if perfile_agrees:
            head += f"; v1 per-file: same volume on {perfile[linked]}/{nfiles} files"
        L.append("   CV linked:      " + head)

    if perfile and not perfile_agrees:
        # the disagreement case, printed in full — a per-file volume that is not the shelf's is either a
        # wrong stored link or a shelf holding more than one run
        shown_cv |= set(perfile)
        L.append("   CV per-file (v1): " + " · ".join(
            _vol_line(vid, ev, ctx) + f" on {k}/{nfiles} files" for vid, k in perfile.most_common(4)))

    # the marker has to name WHICH line already showed the id, or a shelf with no stored volume reads as
    # though it had one
    seen_label = "(= linked) · " if linked else "(= per-file) · "
    cands = [c for c in ev.candidates(sid) if c.get("VolumeId") not in shown_cv]
    cands_cut = len(ev.candidates(sid)) - len(cands)
    hits = _rip_hits(ctx, ev, sid, s, keys, ev.candidates(sid))
    rip = [h for h in hits if h[0][0] not in shown_cv]
    cand_txt = (" · ".join(f'{c.get("VolumeId")} "{c.get("VolumeName")}" {c.get("StartYear") or "?"} '
                           f'{c.get("Publisher") or "?"} {c.get("CountOfIssues") or "?"}'
                           f'{_issue_note(c.get("VolumeId"), c.get("CountOfIssues"), ctx)} s{c.get("Score")}'
                           for c in cands[:6]) if cands else "")
    rip_txt = (" · ".join(f'{h[0]} "{h[1]}" {h[3] or "?"} {h[5] or "?"} {h[4] or "?"}'
                          + _issue_note(h[0], h[4], ctx)
                          + (f" ({via})" if via else "")
                          for h, via in rip[:6]) if rip else "")
    if not cand_txt and not rip_txt and (ev.candidates(sid) or hits):
        # both lookups ran and neither knows a volume that is not already on the page
        L.append("   CV lookups:     the v1 search and the local rip name no volume but the one above")
    else:
        if cand_txt:
            L.append("   CV candidates:  " + (seen_label if cands_cut else "") + cand_txt
                     + "   (v1 search)" + ("" if rip_txt or not hits else "; the local rip found nothing else"))
        if rip_txt:
            L.append("   CV local rip:   " + (seen_label if len(hits) != len(rip) else "") + rip_txt
                     + "   (rip lookup)" + ("" if cand_txt or not ev.candidates(sid)
                                            else "; the v1 search found nothing else"))

    gids = ev.gcd_ids(sid)
    dhits = _dump_hits(ctx, ev, sid, s, keys, ev.candidates(sid))
    dump = [h for h in dhits if h[0][0] not in gids]
    if gids:
        # A reading worker found EVERY round2-folder link in two batches to be wrong: that method copies a
        # folder neighbour's series. When it or round2-series is in play the methods lead the line.
        meth = ev.gcd_methods[sid]
        suspect = [m for m in meth if m in ("round2-folder", "round2-series")]
        mtxt = " · ".join(f"{m}x{n}{' ⚠' if m in ('round2-folder', 'round2-series') else ''}"
                          for m, n in meth.most_common(4))
        series_txt = " · ".join(_gcd_line(g, ev, ctx) + f" on {k}/{nfiles} files"
                                for g, k in gids.most_common(4))
        body = (f"methods {mtxt}  (⚠ copies a folder neighbour's series — verify); {series_txt}"
                if suspect else f"{series_txt}; methods {mtxt}")
        L.append("   GCD per-file:   " + body + ("" if dump else "; the dump names no other series"))
    if dump:
        L.append("   GCD dump:       " + ("(= per-file) · " if len(dhits) != len(dump) else "") + " · ".join(
            f'{h[0]} "{h[1]}"{(" " + ctx.gcd_tag(h[0])) if ctx.gcd_tag(h[0]) else ""} '
            f'{h[2] or "?"}-{h[3] or "?"} {h[6] or "?"} [{h[5] or ""}] {h[4] or "?"}'
            + (f" ({via})" if via else "")
            for h, via in dump[:6]) + "   (dump lookup)")

    bridge = []
    for g, _n in gids.most_common(3):
        for lsid, lname, sup in con.execute(
                "SELECT LocgSeriesId, SeriesName, Support FROM legs.LocgSeriesInference WHERE GcdSeriesId=? "
                "ORDER BY Support DESC LIMIT 2", (g,)):
            bridge.append(f'gcd {g} -> LOCG {lsid} "{lname}" support {sup}')
        # OL below the floor is noise: an isbnSupport of 1 or 2 is one or two editions agreeing, which
        # PLAN §7-G does not treat as corroboration either.
        for wk, sstr, isup in con.execute(
                "SELECT OlWorkKey, SeriesString, IsbnSupport FROM legs.OlSeriesInference WHERE GcdSeriesId=? "
                "AND coalesce(IsbnSupport,0) >= 3 ORDER BY IsbnSupport DESC LIMIT 2", (g,)):
            bridge.append(f'gcd {g} -> OL {wk} "{sstr}" isbnSupport {isup}')
    if bridge:
        L.append("   bridges:        " + " · ".join(bridge))

    ext = []
    if s["muSeriesId"]:
        r = con.execute("SELECT Title, Year, Type FROM MuSeries WHERE Id=?", (s["muSeriesId"],)).fetchone()
        ext.append(f'MU {s["muSeriesId"]} "{r[0] if r else "?"}" {r[1] if r else ""} {r[2] if r else ""}')
    for msid, st, conf in con.execute("SELECT MuSeriesId, Status, Confidence FROM MuSeriesLink WHERE SeriesId=?", (sid,)):
        ext.append(f"MuSeriesLink {msid} status {st} conf {conf}")
    if s["externalWorkId"]:
        r = con.execute("SELECT Title, Provider, ProviderKey FROM ExternalWork WHERE Id=?", (s["externalWorkId"],)).fetchone()
        ext.append(f'ExternalWork {s["externalWorkId"]} "{r[0] if r else "?"}" {r[1] if r else ""} {r[2] if r else ""}')
    if ext:
        L.append("   MU / External:  " + " · ".join(ext))
    return L


def item_packet(sid, ev, ctx, items, decision):
    """One block of an `X-` ITEM batch (TOOLS_TODO 16): the shelf's DECIDED identity, and the books of it
    this batch is asking about, with the per-book candidates a reader needs to write an `I` (the book's own
    record) or a `C` (what it collects).

    The shelf is not re-decided here, so nothing that argues about the shelf's identity is printed: no
    candidate lists, no arithmetic, no lookups. What IS printed is the pool an item-level answer comes out
    of — the linked volume's ISSUE ids, the GCD series' issue rows with their page counts and ISBNs (the
    strongest per-book check there is, ±10pp on 100+ books), the judged ranges, the ComicInfo assertions and
    the barcodes — plus the shelf's own `N` notes, because on a chain-of-minis shelf that is where the reader
    who decided it wrote down which mini is which.

    `decision` = {"line", "cv", "gcd", "conf", "batch", "notes": [...]}
    """
    con, L = ev.con, []
    s = ev.series.get(sid)
    if s is None:
        return [f"== X S{sid}  (no longer a file-holding comic shelf)"]
    years = f"{s['yearStart'] or '?'}-{s['yearEnd'] or '?'}"
    total_books = ev.collections.get(sid, 0)
    L.append(f"== X S{sid} {s['name']}  years {years}  {ev.size.get(sid, 0)} files / {total_books} "
             f"collections  ·  {len(items)} book(s) in this batch")
    L.append(f"   identity: {decision['line'].strip()}   [{decision['batch']}]"
             + (f"  ·  CV {_vol_line(decision['cv'], ev, ctx)}" if decision.get("cv") else "")
             + (f"  ·  GCD {_gcd_line(decision['gcd'], ev, ctx)}" if decision.get("gcd") else ""))
    for note in decision.get("notes", ())[:6]:
        # the minis, named in prose by the shelf's own reader — the `C` lines' raw material
        L.append(f"   N: {note[:300]}")

    if decision.get("cv"):
        iss = ctx.cv_issue_ids(decision["cv"], cap=80)
        if iss:
            L.append("   cv issues: " + " · ".join(
                f"{i}#{n or '?'}" + (f" {(d or '')[:4]}" if d else "") for i, n, d in iss)
                     + ("  …" if len(iss) == 80 else ""))
    if decision.get("gcd") and ctx.gcd is not None:
        rows = ctx.gcd.execute(
            "SELECT id, number, page_count, isbn, title, notes FROM gcd_issue "
            "WHERE series_id = ? AND coalesce(deleted,0) = 0 ORDER BY sort_code, id LIMIT 80",
            (decision["gcd"],)).fetchall()
        if rows:
            L.append("   gcd issues: " + " · ".join(
                f"{r[0]}#{r[1] or '?'}"
                + (f" {int(float(r[2]))}pp" if r[2] not in (None, "") else "")
                + (f" isbn {r[3]}" if r[3] else "")
                + (f' "{r[4][:40]}"' if r[4] else "")
                for r in rows) + ("  …" if len(rows) == 80 else ""))
            # the notes of those same rows (TOOLS_TODO 21): on a collected line each row IS a trade, and its
            # "Collects X #a-b" is the `C` line's range in the run's own numbering
            said = []
            for r in rows:
                for e in gcdnotes.parse_notes(r[5]):
                    said.append(f"{r[0]}#{r[1] or '?'} {e['name'] or '?'}"
                                f"{' [s' + str(e['sid']) + ']' if e['sid'] else ''} {gcdnotes.fmt_ranges(e['ranges'])}")
            if said:
                L.append("   gcd notes:  " + " · ".join(said)[:1600])
        for lsid, lname, sup in con.execute(
                "SELECT LocgSeriesId, SeriesName, Support FROM legs.LocgSeriesInference WHERE GcdSeriesId=? "
                "ORDER BY Support DESC LIMIT 2", (decision["gcd"],)):
            L.append(f'   bridge: gcd {decision["gcd"]} -> LOCG {lsid} "{lname}" support {sup}  '
                     f'(a support count is not a name check)')

    q = ",".join("?" * len(items))
    spans = {r[0]: r[1:] for r in con.execute(
        f"""SELECT ItemId, IssueStart, IssueEnd, Confidence, ProviderRef, EditionTitle
            FROM CollectedEditionSpan WHERE Source = 3 AND ItemId IN ({q})""", list(items))}
    runs = {}
    if con.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='CollectedEditionSpanRun'").fetchone():
        for r in con.execute(f"""SELECT ItemId, Provider, ProviderKey FROM CollectedEditionSpanRun
                                 WHERE Source = 3 AND ItemId IN ({q})""", list(items)):
            runs.setdefault(r[0], []).append(f"{_LEG_OF.get(r[1], r[1])}={r[2]}")
    emb = {r[0]: r[1:] for r in con.execute(
        f"""SELECT ItemId, Series, Volume, Count, Web, Identifier, Notes FROM ComicEmbedded
            WHERE ItemId IN ({q})""", list(items))}
    codes = {r[0]: r[1] for r in con.execute(
        f"""SELECT ItemId, CodesJson FROM legs.BarcodeScan WHERE ItemId IN ({q})
            AND CodesJson IS NOT NULL AND CodesJson NOT IN ('','[]')""", list(items))}
    links = {}
    for r in con.execute(f"""SELECT ItemId, Provider, ProviderKey, SecondaryKey, Status, Method
                             FROM ItemProviderLink WHERE ItemId IN ({q})""", list(items)):
        links.setdefault(r[0], []).append(
            f"{_LEG_OF.get(r[1], r[1])} {r[2] or '-'}/{r[3] or '-'} st{r[4]} {r[5] or '?'}")

    L.append("   books:")
    for iid, fn, path, pages, iscol in con.execute(
            f"""SELECT i.Id, i.FileName, i.Path, coalesce(i.PageCount,0), coalesce(cd.IsCollection,0)
                FROM Item i LEFT JOIN ComicDetail cd ON cd.ItemId = i.Id
                WHERE i.Id IN ({q}) ORDER BY i.Path, i.FileName""", list(items)):
        bits = []
        sp = spans.get(iid)
        if sp and sp[0] is not None:
            bits.append(f"judged #{fmt(sp[0])}-{fmt(sp[1])} conf {sp[2] if sp[2] is not None else '?'} "
                        f"({sp[3] or '?'})")
        elif sp:
            bits.append(f"judged: REFUSED ({sp[3] or '?'})")
        # The marker names the books that OWE a `C`: a judged range on a collected-LINE shelf counts in the
        # run the book collects, never in the line. Printing "NO RUN REF" on every book (X-001..X-003) made the
        # marker noise and the reader wrote one C in 442 books; the population is idbase.item_population's no_c.
        owed = getattr(ctx, "_c_owed", None)
        if owed is None:
            owed = ctx._c_owed = set(idbase.item_population(ev)["no_c"])
        if iid in runs:
            bits.append("runs " + " ".join(runs[iid]))
        elif iid in owed:
            bits.append("C OWED (collected-line shelf: the judged range counts in the run this book collects)")
        e = emb.get(iid)
        if e:
            bits.append("ComicInfo " + " ".join(
                f"{k}={v}" for k, v in zip(("Series", "Volume", "Count", "Web", "Id", "Notes"), e)
                if v not in (None, "")) [:170])
        if iid in codes:
            bits.append(f"barcode {codes[iid][:52]}")
        if iid in links:
            bits.append("linked " + " · ".join(links[iid][:3]))
        L.append(f"     [{iid}]{' COL' if iscol else ''} {fn} ({pages or '?'}pp)  " + "  |  ".join(bits))
        folder = short_path(os.path.dirname(path or ""))
        if folder:
            L.append(f"            {folder}")
        judged, jruns = ctx.judged()
        L.extend(ctx.notes.book_lines(iid, judged.get(iid), jruns.get(iid, ()), indent="            "))
    return L


_LEG_OF = {idbase.P_CV: "cv", idbase.P_EXTERNAL: "ext", idbase.P_LOCG: "locg", idbase.P_GCD: "gcd",
           idbase.P_MU: "mu", idbase.P_BARNEY: "barney", idbase.P_MARVEL: "marvel",
           idbase.P_INDUCKS: "inducks"}


def fmt(x):
    return idbase.fmt_num(x)


def _issue_note(vid, count, ctx):
    """`issue <id>` for a one-issue volume — the id an `I` line actually needs (TOOLS_TODO 12)."""
    if count != 1:
        return ""
    hit = ctx.cv_issue_ids(vid, cap=1)
    return f" issue {hit[0][0]}" if hit else ""


def _vol_line(vid, ev, ctx):
    v = ev.cv_volume.get(vid) or ctx.cv_rip_name(vid)
    if not v:
        return f"vol {vid} (unknown)"
    return (f'vol {vid} "{v["name"]}" {v["startYear"] or "?"} {v["publisher"] or "?"} '
            f'{v["countOfIssues"] or "?"}{_issue_note(vid, v["countOfIssues"], ctx)}')


def _gcd_line(gid, ev, ctx):
    tag = ctx.gcd_tag(gid)
    tag = (" " + tag) if tag else ""
    g = ev.gcd_series.get(gid)
    if not g:
        # legs.GcdSeries holds only the series we matched; anything else is named from the dump
        if ctx.gcd is not None:
            r = ctx.gcd.execute("SELECT id, name, year_began, year_ended, issue_count, format "
                                "FROM gcd_series WHERE id=?", (gid,)).fetchone()
            if r:
                return (f'series {r[0]} "{r[1]}"{tag} {r[2] or "?"}-{r[3] or "?"} '
                        f'[{r[5] or ""}] {r[4] or "?"} (dump only)')
        return f"series {gid} (unknown)"
    return (f'series {gid} "{g["name"]}"{tag} {g["yearBegan"] or "?"}-{g["yearEnded"] or "?"} '
            f'{g["publisher"] or "?"} [{g["format"] or ""}] {g["issueCount"] or "?"}')


RX_TRAIL = re.compile(r"\s*(?:\((?:19|20)\d{2}\)|\bv\d{1,2}\b|\((?:19|20)\d{2}-(?:19|20)?\d{2}\))\s*$", re.I)


def _variants(s):
    """The same title as the shelf spells it, minus the things OUR filing added. `Jughead v2 (1987)` is our
    disambiguator for a second run; no provider calls it that, so a lookup on it finds nothing and the
    packet says "no dump hits" about a series both providers hold. Strips a trailing `(YYYY)` / `vN`
    (repeatedly, for `Jughead v2 (1987)`) and swaps `&` with `and`."""
    out, cur = [], (s or "").strip()
    for _ in range(3):
        nxt = RX_TRAIL.sub("", cur).strip()
        if nxt == cur or not nxt:
            break
        cur = nxt
        out.append(cur)
    for base in [s] + out:
        if not base:
            continue
        if "&" in base:
            out.append(base.replace("&", "and"))
        elif re.search(r"\band\b", base, re.I):
            out.append(re.sub(r"\band\b", "&", base, flags=re.I))
    return [x for x in dict.fromkeys(out) if x and x != s]


def _probe_set(ev, ctx, sid, s, keys, cands, want_gcd_names):
    """Every spelling ON THE PACKET, in the order a reader would try them.

    A tier-B reader found a GCD series on 27 of 450 shelves the packet had called "no dump hits", by
    re-probing with the title the CV record already showed two lines above. The lookup was never wrong
    about the dump — it was only ever asking under our own filing name. Each probe carries a label so an
    extra hit says which spelling found it, and nothing arrives unattributed.

    -> [(probe string, label or "" for the shelf's own spellings)]
    """
    probes = [(s["name"], "")] + [(k, "") for k in keys]
    for v in _variants(s["name"]):
        probes.append((v, f'via our own name without the filing suffix "{v}"'))
    for k in keys:
        for v in _variants(k):
            probes.append((v, f'via the parsed key without the filing suffix "{v}"'))
    # (2) the stored CV volume's name, and the v1 per-file volumes' names — the record already printed
    for vid in ([s["cvVolumeId"]] if s["cvVolumeId"] else []) + [v for v, _ in ev.cv_files.get(sid, Counter()).most_common(2)]:
        v = ev.cv_volume.get(vid) or ctx.cv_rip_name(vid)
        if v and v.get("name"):
            probes.append((v["name"], f'via CV name "{v["name"]}"'))
    # (3) the top three v1 candidates
    for c in cands[:3]:
        if c.get("VolumeName"):
            probes.append((c["VolumeName"], f'via CV candidate name "{c["VolumeName"]}"'))
    # (4) the per-file GCD series' names — for the rip lookup, the other leg's spelling
    if want_gcd_names:
        for gid, _n in ev.gcd_ids(sid).most_common(2):
            g = ev.gcd_series.get(gid)
            nm = g["name"] if g else None
            if nm:
                probes.append((nm, f'via GCD name "{nm}"'))
    out, seen = [], set()
    for p, label in probes:
        k = norm_name(p)
        if k and k not in seen:
            seen.add(k)
            out.append((p, label))
    return out


def _lookup(idx, probes, our_year, year_at, our_name, cap=6):
    """Run every probe, then order, then cap at six.

    The order matters more than the probes do. A shelf called `Beautiful Scars` has a v1 candidate named
    `Scars`, and probing that name finds six unrelated `Scars` series — all of them honestly labelled, all
    of them able to fill the block and push the real hit out. So: the shelf's own spellings first, then how
    much the record's NAME looks like ours, then how close its start year is. Noise still prints (the
    reader must be able to see that the lookup ran and found only this), but it prints last."""
    ours = norm_name(our_name)
    found, seen = [], set()
    for probe, label in probes:
        for r in idx.get(norm_name(probe), ()):
            if r[0] in seen:
                continue
            seen.add(r[0])
            found.append((r, label))

    def rank(item):
        r, label = item
        nm = norm_name(r[1])
        sim = 0 if nm == ours else 1 if (nm and (nm in ours or ours in nm)) else 2
        y = r[year_at] if len(r) > year_at else None
        gap = abs(int(y) - int(our_year)) if (y and our_year) else 9999
        return (1 if label else 0, sim, gap, r[0])

    found.sort(key=rank)
    return found[:cap]


def _rip_hits(ctx, ev, sid, s, keys, cands):
    # cv_vol rows are (volId, name, normName, year, issueCount, publisherName) — start year at index 3
    probes = _probe_set(ev, ctx, sid, s, keys, cands, want_gcd_names=True)
    return _lookup(ctx.cv_index(), probes, s["yearStart"], 3, s["name"])


def _dump_hits(ctx, ev, sid, s, keys, cands):
    # gcd_series rows are (id, name, year_began, year_ended, issue_count, format, publisher) — year at 2
    probes = _probe_set(ev, ctx, sid, s, keys, cands, want_gcd_names=False)
    return _lookup(ctx.gcd_index(), probes, s["yearStart"], 2, s["name"])


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    out = None
    for k, a in enumerate(sys.argv):
        if a == "--out" and k + 1 < len(sys.argv):
            out = sys.argv[k + 1]
            if out in args:
                args.remove(out)
    ev = Evidence()
    ctx = Ctx(ev)
    lines = []
    for sid in (int(a) for a in args):
        lines += packet(sid, ev, ctx) + [""]
    text = "\n".join(lines)
    if out:
        os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
        with open(out, "w", encoding="utf-8") as f:
            f.write(text)
        print(f"{len(args)} packet(s) -> {out}")
    else:
        print(text)


if __name__ == "__main__":
    main()
