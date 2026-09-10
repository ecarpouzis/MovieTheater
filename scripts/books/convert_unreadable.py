#!/usr/bin/env python3
"""
Phase 1 of the unreadable-books conversion: turn every Calibre book the site cannot READ into an EPUB,
into a STAGING directory. Nothing is written into the Calibre library here and nothing on the share is
ever deleted or renamed — `convert_add_formats.py` (phase 2) is what puts the results into Calibre.

WHY THERE ARE TWO PHASES
------------------------
Phase 1 is pure subprocess work over files it only ever READS. Phase 2 is the only half that writes to
the library, and it has to run as a single `calibre-debug` process (two of them deadlock at ~1% CPU and
look like a hung script). Splitting them means the long, parallel, restartable half never holds the
Calibre database open, and the staging directory can be inspected before anything enters the library.

WHAT COUNTS AS UNREADABLE
-------------------------
A book needs an EPUB when Calibre holds NO format this site can read: no EPUB (the prose reader), no
PDF and no CBZ/CBR (real page images on the canvas reader). 35,162 of 136,366 books are in that state
as of 2026-09-07 — 12,833 RARs (87% of them an HTML book plus its images), 6,782 ZIPs (a sample of 200
found all 200 to be EPUBs under the wrong extension), 4,148 MOBI/AZW3, and 11,399 in LIT/TXT/RTF/DOC/
FB2/PRC/AZW/PDB/DOCX/LRF/DJVU/CHM/HTML that the site's scanner never even indexed.

A book that already has a PDF or a comic archive is LEFT ALONE. It reads today, and a PDF converted to
EPUB is a worse book, not a better one.

THE RULES THIS OBEYS
--------------------
  * bounded per call     — `--limit` books per invocation, never "convert everything in one run"
  * observable           — one line per book, then `{processed, converted, failed, skipped, remaining}`
  * resumable+idempotent — a journal row per Calibre book id; a re-run skips what is already done, and
                           a book whose staged file exists is not re-converted
  * caller-driven        — the loop that repeats batches lives in `run_convert.ps1`, not in here
  * non-destructive      — `--apply` is required to write even to STAGING; the default is a dry run,
                           and no source file is ever modified, moved or deleted

USAGE
-----
    python convert_unreadable.py --plan                     # what would be done, over the whole library
    python convert_unreadable.py --apply --limit 50         # one supervised batch
    python convert_unreadable.py --apply --limit 500 --workers 6
    python convert_unreadable.py --report                   # the journal, by status
    python convert_unreadable.py --failures > failures.txt  # every failure with its reason
"""

import argparse
import collections
import concurrent.futures
import functools
import glob
import os
import pickle
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
import zipfile

# ── configuration ────────────────────────────────────────────────────────────────────────────────────

CALIBRE_LIBRARY = r"L:\6 - Books"
EBOOK_CONVERT = r"C:\Program Files\Calibre2\ebook-convert.exe"
SEVEN_ZIP = r"C:\Program Files\7-Zip\7z.exe"
# LibreOffice converts legacy .doc, which Calibre has no reader for. Absent ⇒ those books are journalled
# as `blocked` with a reason, never silently dropped.
# `soffice.com` FIRST: on Windows it is the console entry point that BLOCKS until the conversion is
# done, where `soffice.exe` is a launcher that can return before the file is written.
SOFFICE_CANDIDATES = [
    r"C:\Program Files\LibreOffice\program\soffice.com",
    r"C:\Program Files\LibreOffice\program\soffice.exe",
    r"C:\Program Files (x86)\LibreOffice\program\soffice.com",
    r"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
]

DEFAULT_STAGING = r"F:\Work\MovieTheater\data\books\v2\convert-staging"
DEFAULT_JOURNAL = r"F:\Work\MovieTheater\data\books\v2\convert-journal.db"

# A format the site already reads. A book holding one of these is not our problem.
READABLE = {"EPUB", "PDF", "CBZ", "CBR", "CB7", "CBT"}

# Which source format to convert FROM when a book has several, best first. Ordered by how much
# structure survives the conversion: a real ebook container keeps its chapters and metadata, marked-up
# text keeps its headings, and plain text keeps only its line breaks.
SOURCE_PREFERENCE = [
    "AZW3", "MOBI", "AZW", "PRC", "LIT", "FB2", "PDB", "LRF", "IMP", "TPZ", "CHM",
    "HTMLZ", "HTML", "HTMLL", "HTM", "MHT", "RTF", "DOCX", "DOC", "TXT",
    "ZIP", "RAR", "DJVU",
]

# What to look for INSIDE a .zip/.rar, best first. `.epub` first because 87% of this library's ZIPs are
# whole EPUBs under the wrong extension — those are lifted out as-is, never re-converted.
INNER_PREFERENCE = [
    ".epub", ".azw3", ".mobi", ".prc", ".lit", ".fb2", ".pdb",
    ".html", ".xhtml", ".htm", ".rtf", ".docx", ".doc", ".txt",
    # LAST, and it is not converted — see PASSTHROUGH_INNER. A PDF is skipped when it is the book's own
    # format (the site reads PDFs, and PDF->EPUB is a worse book), but a PDF sealed inside a .rar is
    # unreadable, because the ITEM is the archive. 100 of the 108 "nothing convertible inside" failures
    # were exactly this. Lifting the PDF out gives the canvas reader a real book.
    ".pdf",
]

# Inner files that are STAGED AS THEMSELVES rather than converted, with the Calibre format to add them
# under. Anything not listed here goes through ebook-convert and is staged as an EPUB.
PASSTHROUGH_INNER = {".epub": "EPUB", ".pdf": "PDF"}

# Formats LibreOffice can read that Calibre either cannot or gives up on. RTF is the one that matters:
# 85 books failed with "This RTF file has a feature calibre does not support. Convert it to HTML first",
# and LibreOffice reads every one of them.
LIBREOFFICE_FALLBACK = {".rtf", ".doc", ".docx"}

CONVERT_TIMEOUT_SEC = 900        # CHM books have taken 68 s; a 15 min ceiling is a hang, not a slow book
EXTRACT_TIMEOUT_SEC = 300

# Handed to 7-Zip so an encrypted archive FAILS instead of prompting. It is deliberately not a guess at
# anyone's password: the point is that stdin is closed and a prompt would come back as "Break signaled".
NO_PASSWORD = "-no-password-supplied-"


# ── the journal ──────────────────────────────────────────────────────────────────────────────────────

SCHEMA = """
CREATE TABLE IF NOT EXISTS book (
    calibre_id   INTEGER PRIMARY KEY,
    status       TEXT NOT NULL,          -- pending | converted | added | failed | blocked | skipped
    source_fmt   TEXT,
    source_path  TEXT,
    staged_path  TEXT,
    staged_fmt   TEXT,             -- the Calibre format phase 2 adds it under (default EPUB)
    staged_bytes INTEGER,
    reason       TEXT,
    seconds      REAL,
    updated_at   TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_book_status ON book(status);
"""


def open_journal(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    con = sqlite3.connect(path, timeout=60)
    con.executescript(SCHEMA)
    # Added after the first runs had already journalled tens of thousands of rows, so it is an ALTER
    # rather than a schema bump. A row written before it exists reads back NULL, which phase 2 treats
    # as EPUB — the only format those runs could produce.
    if "staged_fmt" not in {r[1] for r in con.execute("pragma table_info(book)")}:
        con.execute("ALTER TABLE book ADD COLUMN staged_fmt TEXT")
    con.commit()
    return con


def record(con, calibre_id, status, **fields):
    """One book's verdict, written and committed on its own. A kill mid-batch loses at most this book."""
    cols = ["calibre_id", "status", "updated_at"] + list(fields)
    vals = [calibre_id, status, time.strftime("%Y-%m-%dT%H:%M:%S")] + [fields[k] for k in fields]
    con.execute(
        "INSERT OR REPLACE INTO book (%s) VALUES (%s)" % (",".join(cols), ",".join("?" * len(cols))),
        vals)
    con.commit()


# ── what needs doing ─────────────────────────────────────────────────────────────────────────────────

def books_cache_path(journal_path):
    return os.path.join(os.path.dirname(journal_path), "calibre-books-cache.pickle")


@functools.lru_cache(maxsize=1)
def calibre_books(library, cache_path=None):
    """
    Every book with its formats and its on-disk folder, read-only, in one pass. The library is opened
    read-only: this script must never be the reason `metadata.db` is locked.

    Memoized twice over, and the second one is not an optimisation but the difference between a run
    that finishes tonight and one that does not. `metadata.db` is 150 MB on a SMB share and the join
    below walks ~160,000 rows, which costs about four MINUTES; the driver calls this script once per
    batch, so at 500 books a batch the walk was costing more than the conversions did — a measured
    ~3 min of work followed by ~4 min of re-reading the same file. The parsed result is therefore
    cached to a LOCAL file keyed by the database's own size and mtime, so a later invocation reads it
    in under a second and any edit in Calibre invalidates it on its own.
    """
    db = os.path.join(library, "metadata.db")

    stat = os.stat(db)
    key = [stat.st_size, stat.st_mtime_ns]
    if cache_path and os.path.exists(cache_path):
        try:
            with open(cache_path, "rb") as fh:
                cached_key, cached_books = pickle.load(fh)
            if cached_key == key:
                return cached_books
        except Exception:
            pass        # a corrupt or half-written cache is re-read from the share, never trusted
    con = sqlite3.connect(db)
    rows = con.execute("""
        SELECT b.id, b.path, b.title, d.format, d.name,
               (SELECT group_concat(a.name, ' & ') FROM books_authors_link bal
                  JOIN authors a ON a.id = bal.author WHERE bal.book = b.id)
        FROM books b JOIN data d ON d.book = b.id
        ORDER BY b.id
    """).fetchall()
    con.close()

    books = collections.OrderedDict()
    for bid, relpath, title, fmt, name, authors in rows:
        entry = books.setdefault(bid, {"id": bid, "relpath": relpath, "title": title,
                                       "authors": authors, "formats": {}})
        entry["formats"][(fmt or "").upper()] = name

    if cache_path:
        # Written to a temp name and replaced, so a kill mid-write cannot leave a torn cache behind
        # for the next invocation to half-read.
        try:
            tmp_path = cache_path + ".tmp"
            with open(tmp_path, "wb") as fh:
                pickle.dump((key, books), fh, protocol=pickle.HIGHEST_PROTOCOL)
            os.replace(tmp_path, cache_path)
        except Exception:
            pass        # the cache is an accelerator; failing to write one is not a failed run

    return books


def needs_epub(book):
    """A book the site cannot read today. See the module docstring."""
    return not (set(book["formats"]) & READABLE)


def pick_source(book):
    """The best format to convert FROM, or None when nothing here is convertible."""
    for fmt in SOURCE_PREFERENCE:
        if fmt in book["formats"]:
            return fmt
    return None


def source_path(library, book, fmt):
    return os.path.join(library, book["relpath"].replace("/", os.sep),
                        book["formats"][fmt] + "." + fmt.lower())


def pending(con, library, journal_path, limit, include_blocked=False):
    """
    The next `limit` books to work on, in Calibre id order — the SAME order the journal is keyed by, so
    resumption is exact rather than approximate. A book already `converted`, `added` or `skipped` is
    never handed back; a `failed` or `blocked` one is only retried when asked for explicitly, because
    re-running a deterministic failure forever is the shape this codebase refuses.
    """
    done = {r[0] for r in con.execute(
        "SELECT calibre_id FROM book WHERE status IN ('converted','added','skipped')"
        + ("" if include_blocked else " OR status IN ('failed','blocked')"))}

    out = []
    for book in calibre_books(library, books_cache_path(journal_path)).values():
        if book["id"] in done:
            continue
        if not needs_epub(book):
            continue
        out.append(book)
        if limit and len(out) >= limit:
            break
    return out


def remaining_count(con, library, journal_path):
    done = {r[0] for r in con.execute(
        "SELECT calibre_id FROM book WHERE status IN ('converted','added','skipped','failed','blocked')")}
    return sum(1 for b in calibre_books(library, books_cache_path(journal_path)).values() if needs_epub(b) and b["id"] not in done)


# ── conversion ───────────────────────────────────────────────────────────────────────────────────────

def soffice():
    for candidate in SOFFICE_CANDIDATES:
        if os.path.exists(candidate):
            return candidate
    return None


def run(cmd, timeout):
    return subprocess.run(cmd, capture_output=True, text=True, errors="replace", timeout=timeout)


def is_epub_zip(path):
    """The OCF signature — the same test the site's ArchiveFormatSniffer applies."""
    try:
        with zipfile.ZipFile(path) as z:
            names = z.namelist()
            if "mimetype" in names:
                if z.read("mimetype")[:20].decode("ascii", "ignore").strip().startswith("application/epub"):
                    return True
            return "META-INF/container.xml" in names
    except Exception:
        return False


def inner_source(tmpdir):
    """The best convertible file inside an extracted archive: by type first, then by size."""
    for ext in INNER_PREFERENCE:
        hits = [p for p in glob.glob(os.path.join(tmpdir, "**", "*" + ext), recursive=True)
                if os.path.isfile(p)]
        if hits:
            return max(hits, key=os.path.getsize)
    return None


def libreoffice_to_docx(source, tmp):
    """
    Round a document through LibreOffice into a .docx Calibre can read. Returns `(path, None)` or
    `(None, reason)`.

    Used in two places: as the PRE-step for legacy `.doc`, which Calibre has no reader for at all, and
    as the FALLBACK after ebook-convert refuses a document it half-understands — 85 RTF books failed
    with "This RTF file has a feature calibre does not support. Convert it to HTML first", and
    LibreOffice reads every one of them.

    EVERY CALL GETS ITS OWN USER PROFILE. Two soffice processes sharing the default profile do not both
    convert: the second sees the first's lock, exits non-zero having printed NOTHING, and the book fails
    with an empty reason. It works serially and fails under workers, which is exactly the bug that reads
    as "LibreOffice cannot read this file". The profile lives under the book's own temp directory and is
    deleted with it.
    """
    exe = soffice()
    if exe is None:
        return None, "LibreOffice is not installed"

    profile = "-env:UserInstallation=file:///" + os.path.join(tmp, "loprofile").replace("\\", "/")
    outdir = os.path.join(tmp, "lo")
    os.makedirs(outdir, exist_ok=True)
    r = run([exe, profile, "--headless", "--norestore",
             "--convert-to", "docx", "--outdir", outdir, source], CONVERT_TIMEOUT_SEC)
    made = glob.glob(os.path.join(outdir, "*.docx"))
    if r.returncode != 0 or not made:
        return None, "LibreOffice could not read it: " + tail(r.stderr, r.stdout)
    return made[0], None


def staged_file(staging, book_id, fmt):
    return os.path.join(staging, "%d.%s" % (book_id, fmt.lower()))


def already_staged(staging, book_id):
    """The staged file for this book in any format we produce, or None."""
    for fmt in ("EPUB", "PDF"):
        path = staged_file(staging, book_id, fmt)
        if os.path.exists(path) and os.path.getsize(path) > 0:
            return path, fmt
    return None, None


def convert_one(book, library, staging):
    """
    One book, start to finish. Returns `(status, fields)`; raises nothing the caller has to handle —
    a bad file must never stop a run that has 35,162 of them to walk.
    """
    fmt = pick_source(book)
    if fmt is None:
        return "skipped", {"reason": "no convertible format: " + ",".join(sorted(book["formats"]))}

    src = source_path(library, book, fmt)
    if not os.path.exists(src):
        # Calibre's `data` table outlives its files on a few early ids. A phantom row is reported, not
        # guessed at.
        return "failed", {"source_fmt": fmt, "source_path": src, "reason": "source file not on the share"}

    # Idempotent: a batch re-run after a kill finds its own output and does not pay for it twice.
    done_path, done_fmt = already_staged(staging, book["id"])
    if done_path:
        return "converted", {"source_fmt": fmt, "source_path": src, "staged_path": done_path,
                             "staged_fmt": done_fmt, "staged_bytes": os.path.getsize(done_path),
                             "reason": "already staged"}

    out = staged_file(staging, book["id"], "EPUB")
    started = time.time()
    tmp = tempfile.mkdtemp(prefix="bkconv%d_" % book["id"])

    def lifted(path, staged_fmt, why):
        """Stage a file AS ITSELF — no conversion, no re-encoding, no fidelity lost."""
        target = staged_file(staging, book["id"], staged_fmt)
        shutil.copyfile(path, target)
        return "converted", {"source_fmt": fmt, "source_path": src, "staged_path": target,
                             "staged_fmt": staged_fmt, "staged_bytes": os.path.getsize(target),
                             "seconds": time.time() - started, "reason": why}

    try:
        convert_from, note = src, None

        if fmt in ("ZIP", "RAR"):
            # A ZIP that IS an EPUB is COPIED, not converted: re-encoding a perfectly good book through
            # ebook-convert only loses fidelity. This is the single biggest bucket in the library.
            if fmt == "ZIP" and is_epub_zip(src):
                return lifted(src, "EPUB", "epub lifted out of a .zip, unconverted")

            # `-p` with a dummy password is what keeps an ENCRYPTED archive from hanging the run: with
            # no password on the command line 7-Zip PROMPTS, and a prompt against a closed stdin comes
            # back as the useless "Break signaled". With one, an encrypted RAR fails immediately and
            # says so ("Cannot open encrypted archive. Wrong password?"), which is a reportable verdict
            # rather than a stall. The library has a handful of these.
            r = run([SEVEN_ZIP, "x", "-y", "-p" + NO_PASSWORD, "-o" + tmp, src], EXTRACT_TIMEOUT_SEC)
            if r.returncode != 0:
                return "failed", {"source_fmt": fmt, "source_path": src,
                                  "reason": "7z could not extract: " + tail(r.stderr, r.stdout)}
            inner = inner_source(tmp)

            # A wrapped archive — the book is a .rar holding another .rar (27 of the library's
            # "nothing convertible inside" failures were this). Unwrap exactly ONE more level: enough
            # for the way these were packed, and a fixed depth rather than a recursion that a
            # self-containing archive could walk forever.
            if inner is None:
                nested = next((p for p in sorted(glob.glob(os.path.join(tmp, "**", "*"), recursive=True))
                               if os.path.splitext(p)[1].lower() in (".rar", ".zip", ".7z")), None)
                if nested is not None:
                    deeper = os.path.join(tmp, "__unwrapped")
                    r2 = run([SEVEN_ZIP, "x", "-y", "-p" + NO_PASSWORD, "-o" + deeper, nested],
                             EXTRACT_TIMEOUT_SEC)
                    if r2.returncode == 0:
                        inner = inner_source(deeper)
                        if inner is not None:
                            note = "unwrapped a nested " + os.path.splitext(nested)[1].lower()

            if inner is None:
                listing = sorted({os.path.splitext(p)[1].lower()
                                  for p in glob.glob(os.path.join(tmp, "**", "*"), recursive=True)})
                return "failed", {"source_fmt": fmt, "source_path": src,
                                  "reason": "nothing convertible inside: " + ",".join(listing[:8])}

            # An EPUB or a PDF sealed inside an archive comes out AS ITSELF. The site reads both; what
            # made the book unreadable was the .rar/.zip wrapper, not the document. Converting a PDF to
            # EPUB here would be the same mistake as converting a PDF book — a worse book, not a better
            # one — so it is staged as a PDF and added to Calibre under that format.
            inner_ext = os.path.splitext(inner)[1].lower()
            why = "%s lifted out of a .%s, unconverted" % (inner_ext[1:], fmt.lower())
            if note:
                why += " (" + note + ")"
            if inner_ext in PASSTHROUGH_INNER:
                return lifted(inner, PASSTHROUGH_INNER[inner_ext], why)

            convert_from, note = inner, ((note + ", " if note else "") + "via " + inner_ext)

        # Legacy .doc, whether it IS the book's format or was found inside an archive — Calibre has no
        # reader for it either way ("No plugin to handle input format: doc"), so the pre-step sits here
        # rather than in a per-format branch.
        if convert_from.lower().endswith(".doc"):
            made, why = libreoffice_to_docx(convert_from, tmp)
            if made is None:
                status = "blocked" if "not installed" in why else "failed"
                return status, {"source_fmt": fmt, "source_path": src,
                                "reason": "legacy .doc needs LibreOffice: " + why
                                          if status == "blocked" else why}
            convert_from = made
            note = (note + ", " if note else "") + "via .docx"

        # Calibre's title and authors are handed to the conversion rather than left to be guessed from
        # the file name. They become the new EPUB's own metadata AND the text on the cover Calibre
        # generates for a book that has no artwork — which is the cover the site will then show, so it
        # is worth getting right at the source instead of papering over it later.
        meta = []
        if book.get("title"):
            meta += ["--title", book["title"]]
        if book.get("authors"):
            meta += ["--authors", book["authors"]]

        r = run([EBOOK_CONVERT, convert_from, out] + meta, CONVERT_TIMEOUT_SEC)

        # ONE targeted retry, for one known Calibre defect: a long unstructured document (a plain .txt
        # novel with no chapter markup) makes the EPUB output's splitter give up with
        # "Could not find reasonable point at which to split". `--flow-size 0` turns splitting off and
        # the same book converts cleanly. It is not a general "try again" — a retry loop over
        # deterministic failures is the shape this codebase refuses.
        if r.returncode != 0 and "SplitError" in (r.stderr or ""):
            if os.path.exists(out):
                os.remove(out)
            r = run([EBOOK_CONVERT, convert_from, out, "--flow-size", "0"] + meta, CONVERT_TIMEOUT_SEC)
            note = (note + ", " if note else "") + "unsplit"

        # The OTHER targeted retry, and also not a loop: Calibre reads most RTF and then refuses the
        # rest outright ("This RTF file has a feature calibre does not support. Convert it to HTML
        # first"). LibreOffice has no such trouble, so the document is rounded through it and offered
        # to Calibre again as .docx. Tried once, only for the formats LibreOffice actually reads, and
        # only after Calibre has already had its turn.
        if (r.returncode != 0
                and os.path.splitext(convert_from)[1].lower() in LIBREOFFICE_FALLBACK
                and not convert_from.lower().endswith(".docx")):
            made, why = libreoffice_to_docx(convert_from, tmp)
            if made is not None:
                if os.path.exists(out):
                    os.remove(out)
                r = run([EBOOK_CONVERT, made, out] + meta, CONVERT_TIMEOUT_SEC)
                note = (note + ", " if note else "") + "via .docx (calibre refused the original)"

        if r.returncode != 0 or not os.path.exists(out) or os.path.getsize(out) == 0:
            if os.path.exists(out):
                os.remove(out)          # never leave a zero-byte book for phase 2 to add
            return "failed", {"source_fmt": fmt, "source_path": src,
                              "reason": "ebook-convert failed: " + tail(r.stderr, r.stdout)}

        return "converted", {"source_fmt": fmt, "source_path": src, "staged_path": out,
                             "staged_fmt": "EPUB", "staged_bytes": os.path.getsize(out),
                             "seconds": time.time() - started, "reason": note}
    except subprocess.TimeoutExpired:
        return "failed", {"source_fmt": fmt, "source_path": src, "reason": "timed out"}
    except Exception as exc:                                  # noqa: BLE001 - one bad file, not the run
        return "failed", {"source_fmt": fmt, "source_path": src,
                          "reason": "%s: %s" % (type(exc).__name__, exc)}
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def tail(*texts, limit=220):
    """
    The last few lines of the first stream that has any. STDERR is asked first and on purpose: Calibre
    prints its progress to stdout and its traceback to stderr, so reading stdout alone reports
    "Found large tree #0" for a book that actually died of a SplitError three frames later.
    """
    for text in texts:
        lines = (text or "").strip().splitlines()
        if lines:
            return " | ".join(lines[-3:])[:limit]
    return ""


# ── the commands ─────────────────────────────────────────────────────────────────────────────────────

def cmd_plan(args, con):
    books = calibre_books(args.library, books_cache_path(args.journal))
    need = [b for b in books.values() if needs_epub(b)]
    by_source = collections.Counter(pick_source(b) or "(none convertible)" for b in need)
    already = {r[0]: r[1] for r in con.execute("SELECT status, count(*) FROM book GROUP BY status")}

    print("Calibre books                     : %d" % len(books))
    print("Readable today (epub/pdf/cbz/cbr) : %d" % (len(books) - len(need)))
    print("NEED an epub                      : %d" % len(need))
    print()
    print("would convert from:")
    for fmt, n in by_source.most_common():
        note = ""
        if fmt == "DOC" and soffice() is None:
            note = "   <- BLOCKED: LibreOffice not installed"
        if fmt == "ZIP":
            note = "   (mostly whole EPUBs, copied not converted)"
        print("   %-18s %6d%s" % (fmt, n, note))
    if already:
        print()
        print("journal so far:")
        for status, n in sorted(already.items()):
            print("   %-18s %6d" % (status, n))


def cmd_report(args, con):
    rows = list(con.execute(
        "SELECT status, count(*), sum(staged_bytes), avg(seconds) FROM book GROUP BY status ORDER BY 2 DESC"))
    if not rows:
        print("journal is empty")
        return
    for status, n, size, secs in rows:
        print("%-12s %7d   %8.2f GB   avg %5.1fs" % (
            status, n, (size or 0) / 1e9, secs or 0))
    print()
    print("by source format:")
    for fmt, status, n in con.execute(
            "SELECT coalesce(source_fmt,'?'), status, count(*) FROM book GROUP BY 1,2 ORDER BY 1,3 DESC"):
        print("   %-8s %-12s %6d" % (fmt, status, n))


def cmd_failures(args, con):
    for bid, fmt, reason, path in con.execute(
            "SELECT calibre_id, source_fmt, reason, source_path FROM book "
            "WHERE status IN ('failed','blocked') ORDER BY source_fmt, calibre_id"):
        print("%-8d %-6s %-60s %s" % (bid, fmt or "?", (reason or "")[:60], path or ""))


def cmd_run(args, con):
    if not args.apply:
        print("DRY RUN — nothing will be written. Add --apply to convert.")
    os.makedirs(args.staging, exist_ok=True)

    batch = pending(con, args.library, args.journal, args.limit, include_blocked=args.retry_blocked)
    if not batch:
        print("{ processed: 0, converted: 0, failed: 0, skipped: 0, remaining: 0 }   nothing left to do")
        # 2 == "that batch made no progress". Returning 0 here let run_convert.ps1 spin an empty batch
        # forever (102,585 of them, once) instead of stopping when the work ran out.
        return 2

    counts = collections.Counter()
    started = time.time()

    def work(book):
        if not args.apply:
            fmt = pick_source(book)
            return book, ("converted" if fmt else "skipped"), {"source_fmt": fmt, "reason": "dry run"}
        status, fields = convert_one(book, args.library, args.staging)
        return book, status, fields

    # AS COMPLETED, not in order. `ThreadPoolExecutor.map` yields results in SUBMISSION order, so one
    # slow book (a 345 s MOBI is real here) holds back the journal rows and the printed lines for every
    # book that finished behind it — the run looks stalled at 2 books/minute while six workers are
    # busy, and a kill throws away finished-but-unjournalled work. Journalling each book the moment it
    # lands is what makes the progress figures true and the resume tight.
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = [pool.submit(work, book) for book in batch]
        for future in concurrent.futures.as_completed(futures):
            book, status, fields = future.result()
            counts[status] += 1
            if args.apply:
                record(con, book["id"], status, **fields)
            print("%-9s %-8d %-6s %8.1f KB  %s" % (
                status, book["id"], fields.get("source_fmt") or "?",
                (fields.get("staged_bytes") or 0) / 1024, fields.get("reason") or ""))

    remaining = remaining_count(con, args.library, args.journal) if args.apply else "(dry run)"
    print()
    print("{ processed: %d, converted: %d, failed: %d, blocked: %d, skipped: %d, remaining: %s }  %.1fs"
          % (len(batch), counts["converted"], counts["failed"], counts["blocked"], counts["skipped"],
             remaining, time.time() - started))
    # The caller's stop condition: a batch that converted nothing new is the end of the run (or a defect)
    # — either way, stop.
    return 0 if counts["converted"] else 2


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--library", default=CALIBRE_LIBRARY)
    p.add_argument("--staging", default=DEFAULT_STAGING)
    p.add_argument("--journal", default=DEFAULT_JOURNAL)
    p.add_argument("--limit", type=int, default=50, help="books this invocation may touch (0 = no cap)")
    p.add_argument("--workers", type=int, default=4, help="parallel ebook-convert processes")
    p.add_argument("--apply", action="store_true", help="actually convert; without it nothing is written")
    p.add_argument("--retry-blocked", action="store_true", help="re-attempt books that failed or were blocked")
    p.add_argument("--plan", action="store_true", help="what would be done, over the whole library")
    p.add_argument("--report", action="store_true", help="the journal, by status")
    p.add_argument("--failures", action="store_true", help="every failure with its reason")
    args = p.parse_args()

    for exe, what in ((EBOOK_CONVERT, "ebook-convert"), (SEVEN_ZIP, "7z")):
        if not os.path.exists(exe):
            sys.exit("%s not found at %s" % (what, exe))

    con = open_journal(args.journal)
    try:
        if args.plan:
            return cmd_plan(args, con)
        if args.report:
            return cmd_report(args, con)
        if args.failures:
            return cmd_failures(args, con)
        sys.exit(cmd_run(args, con))
    finally:
        con.close()


if __name__ == "__main__":
    main()
