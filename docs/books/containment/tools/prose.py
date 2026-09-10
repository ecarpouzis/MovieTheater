"""What LOCG's own page says this book IS and what it collects — offline, from the cached HTML.

`python prose.py <seriesId> [seriesId...]`     one line per collected edition on the shelf
`python prose.py --items=123,456`              the same for named items
`python prose.py --audit --half=b`             the whole queue: every LOCG-asserted edition, is the page
                                               an EDITION page or a single COMIC page?

Three things come off the page and each is stated by LOCG about itself:

  * `<title>` — the record's identity ("The Unbeatable Squirrel Girl #11 Reviews")
  * the format line — "Trade Paperback - 280 pages - $24.99" or "Comic - 28 pages - $3.99"
  * the description's collects/reprints sentence — PLAN.md §7's only source of NON-contiguity

A 128pp trade whose LOCG record is a 28-page *Comic* is a mis-link, and every span derived from it is
about a different object. That is checkable without believing anything LOCG says about issues.

§3.2's shell trap is honoured: a page under 8 KB is reported as a shell — unknown, never silence.
"""
import html
import os
import re
import sqlite3
import subprocess
import sys

HOT = r"F:\Work\MovieTheater\data\books\v2\books.db"
CACHE = r"F:\Work\MovieTheater\data\books\archive\locg_cache\detail_html"
HERE = os.path.dirname(os.path.abspath(__file__))
RX_DESC = re.compile(r'class="[^"]*listing-description[^"]*"[^>]*>(.*?)</div>', re.S)
RX_FMT = re.compile(r'copy-small font-italic"[^>]*>(.*?)</div>', re.S)
RX_TITLE = re.compile(r"<title>(.*?)</title>", re.S)
RX_TAG = re.compile(r"<[^>]+>")
_cache = {}


def flat(s):
    return re.sub(r"\s+", " ", RX_TAG.sub(" ", html.unescape(s))).strip()


def page(locg_id):
    """(title, format, pages, collects-sentence) — or a marker string in title for a page we cannot read."""
    if locg_id in _cache:
        return _cache[locg_id]
    p = os.path.join(CACHE, f"{locg_id}.html")
    if not os.path.exists(p):
        out = ("(no cached page)", "", None, "")
    elif os.path.getsize(p) < 8192:
        out = ("(SHELL page - unknown)", "", None, "")
    else:
        h = open(p, encoding="utf-8", errors="replace").read()
        t = RX_TITLE.search(h)
        title = flat(t.group(1)).removesuffix(" Reviews") if t else ""
        f = RX_FMT.search(h)
        fmt = flat(f.group(1)) if f else ""
        pm = re.search(r"(\d+)\s*pages", fmt)
        d = RX_DESC.search(h)
        col = ""
        if d:
            txt = flat(d.group(1))
            hits = [s.strip() for s in re.split(r"(?<=[.!?])\s+", txt)
                    if re.search(r"(?i)\b(collect|reprint)", s)]
            col = " | ".join(hits)
        out = (title, fmt.split("\u2022")[0].split("·")[0].strip() or fmt, int(pm.group(1)) if pm else None, col)
    _cache[locg_id] = out
    return out


con = sqlite3.connect(f"file:{HOT}?mode=ro", uri=True)


def _cli():


    def locg_ids(iid):
        ids = [str(r[0]).strip() for r in con.execute(
            "SELECT ProviderKey FROM ItemProviderLink WHERE ItemId=? AND Provider=2 AND ProviderKey IS NOT NULL", (iid,))]
        ids += [str(r[0]).strip() for r in con.execute(
            "SELECT ProviderRef FROM CollectedEditionSpan WHERE ItemId=? AND Source=0 AND ProviderRef IS NOT NULL", (iid,))]
        seen, out = set(), []
        for x in ids:
            if x and x not in seen:
                seen.add(x)
                out.append(x)
        return out


    def editions(sid):
        return con.execute("""SELECT i.Id, i.FileName, i.PageCount FROM Item i JOIN ComicDetail cd ON cd.ItemId=i.Id
                              WHERE i.SeriesId=? AND cd.IsCollection=1 AND coalesce(i.IsExcluded,0)=0
                              ORDER BY cd.VolumeNo, i.FileName""", (sid,)).fetchall()


    if "--audit" in sys.argv:
        half = next((a.split("=", 1)[1] for a in sys.argv if a.startswith("--half=")), "b")
        out = subprocess.run([sys.executable, os.path.join(HERE, "risk_queue.py"), f"--half={half}"],
                             capture_output=True, text=True).stdout
        sids = [int(m.group(1)) for m in re.finditer(r"^  S(\d+)", out, re.M)]
        n = comic = shell = nolink = edition = 0
        bad = []
        for sid in sids:
            for iid, fn, pc in editions(sid):
                ids = locg_ids(iid)
                if not ids:
                    nolink += 1
                    continue
                n += 1
                title, fmt, lpages, col = page(ids[0])
                if title.startswith("(SHELL"):
                    shell += 1
                elif title.startswith("(no cached"):
                    nolink += 1
                elif re.match(r"(?i)^comic\b", fmt):
                    comic += 1
                    bad.append((sid, iid, pc, fn, ids[0], title, fmt))
                else:
                    edition += 1
        print(f"{n} collected editions carry a LOCG id: {edition} point at an EDITION page, "
              f"{comic} point at a single COMIC page, {shell} at a shell, {nolink} unresolvable/absent")
        print("\n-- the mis-links: a collected edition whose LOCG record is a single comic --")
        for sid, iid, pc, fn, lid, title, fmt in bad:
            print(f"  S{sid:<7} [{iid:>7}] {str(pc):>5}pp  locg {lid:<9} {fmt:<28} {title[:44]:<44} {fn[:52]}")
        return

    rows = []
    for a in sys.argv[1:]:
        if a.startswith("--items"):
            ids = [int(x) for x in a.split("=", 1)[1].split(",")]
            rows += con.execute(f"SELECT Id, FileName, PageCount FROM Item WHERE Id IN ({','.join(map(str, ids))})").fetchall()
        elif not a.startswith("--"):
            rows += editions(int(a))

    for iid, fn, pc in rows:
        print(f"[{iid:>7}] {str(pc):>5}pp {fn[:88]}")
        ids = locg_ids(iid)
        if not ids:
            print("          (no LOCG id)")
        for x in ids:
            title, fmt, lpages, col = page(x)
            print(f"          locg {x:<9} {fmt:<26} {title[:58]}")
            if col:
                print(f"                    COLLECTS: {col[:260]}")


if __name__ == "__main__":
    _cli()
