"""Fill CvVolume and CvIssue for the volumes this pass ACCEPTED, out of the offline rip. No API.

    python fill_cv_from_rip.py                       dry run, first chunk
    python fill_cv_from_rip.py --all                 dry run, drive to the end
    python fill_cv_from_rip.py --all --apply         write
    python fill_cv_from_rip.py --after <volId>       resume at a cursor a chunk printed

PLAN Phase C.1 budgeted an API fetch for the 773 volumes we link but never fetched. We already hold them:
`comicdb_comicvine_20260122.db` is a 14.5 GB rip of ComicVine's own responses, keyed by id, and
`cvref.db` indexes every issue by volume. So this is a copy, not a fetch — no rate limit, no key, and no
network call that could return something different tomorrow.

Population (not "every volume in the rip" — that would be 170,260 rows nobody asked for): every `cv=` on an
accepted `S` line, precedence-resolved across all decision files, plus every `needs-fetch cv=<id>` flag.

The three rules that make it safe to re-run:

  * **Idempotent.** Every write is an upsert keyed on the id; running it twice writes the same bytes.
  * **Never overwrite something newer.** A row the site fetched after the rip's `date_last_updated` is
    fresher than the rip and is left exactly alone — the rip is a floor, not an authority.
  * **Chunked, with a cursor.** Bounded work per call, ordered by volume id, printing
    `{processed, remaining, nextCursor, counts}`; `--after` resumes. The driver loop lives in `--all` or in
    the caller, never inside one unbounded statement.

Column semantics are copied from `ProviderScrapers.UpsertVolumeAsync` / the CvIssue upsert and
`ComicVineClient.ParseVolumes/ParseIssues`: publisher is `publisher.name`, the image is `image.medium_url`,
and FetchedAt is set on write.
"""
import json
import os
import sqlite3
import sys
import time

import idbase

APPLY = "--apply" in sys.argv
ALL = "--all" in sys.argv
opt = {}
for k, a in enumerate(sys.argv):
    if a.startswith("--") and k + 1 < len(sys.argv) and not sys.argv[k + 1].startswith("--"):
        opt[a[2:]] = sys.argv[k + 1]
CHUNK = int(opt.get("chunk") or 200)
after = int(opt.get("after") or 0)

ro = idbase.open_hot()
rip = idbase.open_cv_rip()
ref = idbase.open_cv_ref()
if rip is None or ref is None:
    raise SystemExit("the ComicVine rip / cvref are not on disk — nothing to copy from")


# ── the population ───────────────────────────────────────────────────────────────────────────────
def wanted_volumes():
    decides, winner, _sup, _dup = idbase.scan_decisions()
    want = set()
    for path, rec in decides.items():
        for sid, vid in rec["cv"].items():
            if winner.get(sid) == path and rec["kinds"].get(sid) == "S" \
                    and rec["confs"].get(sid) in ("1.0", "0.95", "0.9"):
                want.add(vid)
        for sid, fls in rec.get("flags_full", {}).items():
            if winner.get(sid) != path:
                continue
            for fl in fls:
                if fl.split("=")[0] == "needs-fetch":
                    tail = fl.split("=", 1)[1] if "=" in fl else ""
                    if tail.isdigit():
                        want.add(int(tail))
    # a `needs-fetch` written as `F <sid> needs-fetch | cv=<id> …` puts the id in the detail
    import re
    for path in decides:
        for raw in open(path, encoding="utf-8"):
            if raw.lstrip().startswith("F ") and "needs-fetch" in raw:
                for m in re.finditer(r"cv[=: ](\d+)", raw):
                    want.add(int(m.group(1)))
    return sorted(want)


def rip_row(table_singular, table_plural, vid):
    """The rich record if the rip has it, else the thin one. `cv_volume` carries deck/description;
    `cv_volumes` is the list form and carries neither."""
    for t in (table_singular, table_plural):
        try:
            r = rip.execute(f"SELECT date_last_updated, raw_api_response FROM {t} WHERE id=?", (vid,)).fetchone()
        except sqlite3.OperationalError:
            continue
        if r and r[1]:
            try:
                return r[0], json.loads(r[1])
            except ValueError:
                continue
    return None, None


def img(d):
    i = d.get("image")
    return i.get("medium_url") if isinstance(i, dict) else None


def pub(d):
    p = d.get("publisher")
    return p.get("name") if isinstance(p, dict) else None


def newer_than_rip(existing_fetched, rip_stamp):
    """The site's own fetch wins when it is later than the rip's snapshot of the record. Both are strings;
    ISO-8601 and 'YYYY-MM-DD HH:MM:SS' compare correctly for this purpose once the 'T' is normalised."""
    if not existing_fetched or not rip_stamp:
        return False
    return str(existing_fetched).replace("T", " ")[:19] > str(rip_stamp).replace("T", " ")[:19]


SQL_VOL = """INSERT INTO CvVolume (Id, Name, StartYear, PublisherName, CountOfIssues, Deck, Description,
                                   ImageUrl, SiteDetailUrl, FetchedAt)
             VALUES (?,?,?,?,?,?,?,?,?,?)
             ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name, StartYear=excluded.StartYear,
               PublisherName=excluded.PublisherName, CountOfIssues=excluded.CountOfIssues,
               Deck=excluded.Deck, Description=excluded.Description, ImageUrl=excluded.ImageUrl,
               SiteDetailUrl=excluded.SiteDetailUrl, FetchedAt=excluded.FetchedAt"""
SQL_ISS = """INSERT INTO CvIssue (Id, VolumeId, Name, IssueNumber, CoverDate, StoreDate, Deck,
                                  Description, ImageUrl, SiteDetailUrl, FetchedAt)
             VALUES (?,?,?,?,?,?,?,?,?,?,?)
             ON CONFLICT(Id) DO UPDATE SET VolumeId=excluded.VolumeId, Name=excluded.Name,
               IssueNumber=excluded.IssueNumber, CoverDate=excluded.CoverDate,
               StoreDate=excluded.StoreDate, Deck=excluded.Deck, Description=excluded.Description,
               ImageUrl=excluded.ImageUrl, SiteDetailUrl=excluded.SiteDetailUrl,
               FetchedAt=excluded.FetchedAt"""

vols = wanted_volumes()
todo = [v for v in vols if v > after]
NOW = time.strftime("%Y-%m-%dT%H:%M:%S.000000")

have_vol = {r[0]: r[1] for r in ro.execute("SELECT Id, FetchedAt FROM CvVolume")}
have_iss = {r[0]: r[1] for r in ro.execute("SELECT Id, FetchedAt FROM CvIssue")}

total = {"volumes_written": 0, "volumes_kept_newer": 0, "volumes_not_in_rip": 0,
         "issues_written": 0, "issues_kept_newer": 0, "issues_no_record": 0}
sample = []
w = sqlite3.connect(idbase.HOT) if APPLY else None
print(f"population: {len(vols):,} accepted/needs-fetch volume(s); {len(todo):,} at or past the cursor "
      f"{after}; chunk {CHUNK}{' — DRIVING TO THE END' if ALL else ''}")

# The driver loop lives here rather than inside one unbounded statement: bounded work per chunk, a printed
# cursor, and — when applying — a COMMIT per chunk, so an interruption leaves the chunks that finished
# durably written and `--after <nextCursor>` picks up exactly where it stopped. Accumulating 7,239 volumes'
# worth of issue rows in memory before the first write is how the first version of this ran for ten minutes
# with nothing to show and nothing recoverable.
done, t0 = 0, time.time()
while done < len(todo):
    batch = todo[done:done + CHUNK]
    counts = {k: 0 for k in total}
    vol_rows, iss_rows = [], []
    for vid in batch:
        stamp, d = rip_row("cv_volume", "cv_volumes", vid)
        if d is None:
            counts["volumes_not_in_rip"] += 1
        elif newer_than_rip(have_vol.get(vid), stamp):
            counts["volumes_kept_newer"] += 1
        else:
            vol_rows.append((vid, d.get("name"), d.get("start_year"), pub(d), d.get("count_of_issues"),
                             d.get("deck"), d.get("description"), img(d), d.get("site_detail_url"), NOW))
            counts["volumes_written"] += 1
            if len(sample) < 8:
                sample.append(f'vol {vid} "{d.get("name")}" {d.get("start_year")} {pub(d)} '
                              f'{d.get("count_of_issues")} issues'
                              + ("  (new)" if vid not in have_vol else "  (refreshed)"))
        # every issue of the volume, from cvref's index, hydrated out of the rip
        for (iid,) in ref.execute("SELECT issueId FROM cv_iss WHERE volId=? ORDER BY numKey, issueId", (vid,)):
            istamp, e = rip_row("cv_issue", "cv_issues", iid)
            if e is None:
                counts["issues_no_record"] += 1
                continue
            if newer_than_rip(have_iss.get(iid), istamp):
                counts["issues_kept_newer"] += 1
                continue
            v = e.get("volume")
            iss_rows.append((iid, (v.get("id") if isinstance(v, dict) else None) or vid,
                             e.get("name"), e.get("issue_number"), e.get("cover_date"), e.get("store_date"),
                             e.get("deck"), e.get("description"), img(e), e.get("site_detail_url"), NOW))
            counts["issues_written"] += 1
    if APPLY:
        w.executemany(SQL_VOL, vol_rows)
        w.executemany(SQL_ISS, iss_rows)
        w.commit()
    for k in counts:
        total[k] += counts[k]
    done += len(batch)
    print(json.dumps({"processed": done, "remaining": len(todo) - done,
                      "nextCursor": batch[-1], "elapsed_s": round(time.time() - t0),
                      "counts": counts}), flush=True)
    if not ALL:
        break

print("\nTOTAL " + json.dumps({"processed": done, "remaining": len(todo) - done,
                              "nextCursor": todo[done - 1] if done else after, "counts": total}))
for s in sample:
    print("   " + s)

if not APPLY:
    print("\n(dry run — nothing written. Re-run with --apply, and only after backup_live.py)")
    raise SystemExit(0)
w.close()
print(f"\napplied: {total['volumes_written']:,} CvVolume row(s), {total['issues_written']:,} CvIssue row(s)"
      + (f"; re-run with --after {todo[done-1]} for the rest" if done < len(todo) else "; population drained"))
