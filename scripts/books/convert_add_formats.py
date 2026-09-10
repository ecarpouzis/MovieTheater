#!/usr/bin/env python3
"""
Phase 2 of the unreadable-books conversion: take the EPUBs `convert_unreadable.py` staged and ADD them
to their Calibre books as an extra format.

    "C:\\Program Files\\Calibre2\\calibre-debug.exe" -e convert_add_formats.py -- --apply --limit 200

RUN IT THROUGH calibre-debug, NOT a plain python. It needs Calibre's own `calibre.db` API, and only
ONE calibre-debug process may run at a time — two deadlock at ~1 % CPU and look like a hung script.
Check `tasklist | findstr calibre` before starting.

WHAT IT DOES AND DOES NOT DO
----------------------------
It calls `cache.add_format(book_id, 'EPUB', <staged file>, replace=False)`. That COPIES the staged file
into the book's existing folder and writes one `data` row. It does not rename the folder, does not
touch the book's metadata, and — because `replace=False` — never overwrites a format that is already
there. **The original unreadable file is left exactly where it is.** Nothing is deleted from the share
by this script or by any part of this conversion.

`DB.PATH_LIMIT = 56` is set before the library is opened. `add_format` does not re-compose a folder
name, but the library has been at 56 since 2026-08-30 and anything that opens it at Calibre's default
40 risks re-truncating a book it touches. Setting it is free; not setting it is a trap.

AFTERWARDS
----------
The catalog still points every one of these items at the OLD file. Run, in this order:

    books-import-calibre --apply --reset     # re-points each item at its new EPUB (the "upgraded" count)
    books-resolve --series
    books-thumbs                             # real covers replace the generated title cards

`books-import-calibre` is what turns "the book now has an EPUB" into "the site opens the EPUB": it
ranks a Calibre book's formats and moves the EXISTING item onto the best one, so the reading position,
marks, insights and series link all survive. Without it a later `books-scan` would index the new EPUB
as a SECOND item and split the book in half.

THE RULES THIS OBEYS
--------------------
  * bounded per call     — `--limit` books per invocation
  * observable           — a line per book, then `{processed, added, failed, remaining}`
  * resumable+idempotent — the journal's `added` rows are never revisited, and a book that already has
                           an EPUB format is recorded as `added` without being touched
  * caller-driven        — the repeat loop is `run_convert.ps1`'s, not this script's
  * non-destructive      — `--apply` is required; the default lists what it would add and stops
"""

import argparse
import os
import sqlite3
import sys
import time

DEFAULT_LIBRARY = r"L:\6 - Books"
DEFAULT_JOURNAL = r"F:\Work\MovieTheater\data\books\v2\convert-journal.db"

# The proven ceiling for this library (2026-08-30). Must be set BEFORE the database is constructed:
# `PATH_LIMIT` is a class attribute read through `self`.
PATH_LIMIT = 56


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--library", default=DEFAULT_LIBRARY)
    p.add_argument("--journal", default=DEFAULT_JOURNAL)
    p.add_argument("--limit", type=int, default=200, help="books this invocation may add (0 = no cap)")
    p.add_argument("--apply", action="store_true", help="actually add; without it nothing is written")
    args = p.parse_args()

    from calibre.db.backend import DB
    from calibre.db.cache import Cache

    DB.PATH_LIMIT = PATH_LIMIT

    con = sqlite3.connect(args.journal, timeout=60)
    # `staged_fmt` is NULL for rows written before the column existed; those runs could only produce
    # EPUBs, so NULL means EPUB. A PDF lifted out of a .rar is added as a PDF — the site reads it on the
    # canvas, and re-encoding it to EPUB would be a worse book.
    todo = con.execute(
        "SELECT calibre_id, staged_path, staged_bytes, coalesce(staged_fmt,'EPUB') FROM book "
        "WHERE status = 'converted' AND staged_path IS NOT NULL ORDER BY calibre_id"
        + ("" if args.limit == 0 else " LIMIT %d" % args.limit)).fetchall()
    remaining_before = con.execute(
        "SELECT count(*) FROM book WHERE status = 'converted'").fetchone()[0]

    if not todo:
        print("{ processed: 0, added: 0, failed: 0, remaining: 0 }   nothing staged to add")
        # 2 == "no progress", the code run_convert.ps1 stops on. Returning 0 here would spin the driver
        # on empty batches forever, as it did for 102,585 of them in phase 1 on 2026-09-08.
        return 2

    if not args.apply:
        print("DRY RUN — nothing will be written. Add --apply to add these formats.")
        for bid, path, size, sfmt in todo:
            print("would add %-4s to book %-8d  %8.1f KB  %s" % (sfmt, bid, (size or 0) / 1024, path))
        print()
        print("{ processed: %d, added: 0, failed: 0, remaining: %d }" % (len(todo), remaining_before))
        return 0

    backend = DB(args.library)
    cache = Cache(backend)
    cache.init()

    added = failed = 0
    started = time.time()
    try:
        for bid, path, size, sfmt in todo:
            try:
                if bid not in cache.all_book_ids():
                    mark(con, bid, "failed", "book id is gone from Calibre")
                    failed += 1
                    print("gone      %-8d %s" % (bid, path))
                    continue
                if not os.path.exists(path):
                    mark(con, bid, "failed", "staged file is missing")
                    failed += 1
                    print("no-file   %-8d %s" % (bid, path))
                    continue

                # Already there ⇒ nothing to do, and the journal says so. This is what makes a re-run
                # after a kill free rather than a second copy attempt.
                if sfmt.upper() in {f.upper() for f in cache.formats(bid)}:
                    mark(con, bid, "added", "already had a " + sfmt)
                    added += 1
                    print("present   %-8d (already had a %s)" % (bid, sfmt))
                    continue

                with open(path, "rb") as stream:
                    # replace=False: never overwrite a format that exists. run_hooks=False: STORE the
                    # file, do not let conversion plugins re-process it on the way in.
                    ok = cache.add_format(bid, sfmt.upper(), stream, replace=False, run_hooks=False)
                if not ok:
                    mark(con, bid, "failed", "calibre refused add_format")
                    failed += 1
                    print("refused   %-8d %s" % (bid, path))
                    continue

                mark(con, bid, "added", None)
                added += 1
                print("added     %-8d %-4s %8.1f KB" % (bid, sfmt, (size or 0) / 1024))
            except Exception as exc:                          # noqa: BLE001 - one book, not the run
                mark(con, bid, "failed", "%s: %s" % (type(exc).__name__, exc))
                failed += 1
                print("error     %-8d %s: %s" % (bid, type(exc).__name__, exc))
    finally:
        cache.close()
        con.close()

    print()
    print("{ processed: %d, added: %d, failed: %d, remaining: %d }  %.1fs"
          % (len(todo), added, failed, remaining_before - added - failed, time.time() - started))
    # A batch that added nothing is the end of the run (or a defect) — either way the caller stops.
    return 0 if added else 2


def mark(con, calibre_id, status, reason):
    """Committed per book, so a kill anywhere costs at most the book in flight."""
    con.execute(
        "UPDATE book SET status = ?, reason = coalesce(?, reason), updated_at = ? WHERE calibre_id = ?",
        (status, reason, time.strftime("%Y-%m-%dT%H:%M:%S"), calibre_id))
    con.commit()


if __name__ == "__main__":
    sys.exit(main())
