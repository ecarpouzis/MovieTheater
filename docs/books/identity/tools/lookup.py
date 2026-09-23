"""Ad-hoc name lookup for a reading worker: the same ComicVine rip and GCD dump the packet consulted,
probed with a spelling of YOUR choosing, exact first and then as a substring.

    python lookup.py "Battle Action" [--year 1977] [--contains]
    python lookup.py --issues <cvVolumeId>          the issue ids / numbers of one CV volume
    python lookup.py --collects <gcdIssueId>        what GCD says a trade collects (notes + gcd_reprint)
    python lookup.py --gcd-issues <gcdSeriesId> [--limit 400] [--variants]   GCD's issue rows of one series (TOOLS_TODO 24)
    python lookup.py --gcd-series "<name>" [--year YYYY] [--contains]   the same, for the series matching a name
    python lookup.py --batch <file>                 many of the above in one call, one query per line

Exact = the normalised name matches (the packet's own rule). --contains = the normalised name CONTAINS the
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
import os
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


def run(argv):
    """One query in command-line shape -> its answer lines."""
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
