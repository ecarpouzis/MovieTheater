# The unreadable books — diagnosis, conversion, runbook

*Measured 2026-09-07 against `\\Library\Public\6 - Books` (Calibre 9.9, 136,366 books) and
`data/books/v2/books.db` (245,071 items, 125,262 of them books).*

## What was actually wrong

Six separate problems wearing one costume ("books with no cover that will not open"):

| # | Bucket | Count | The defect |
|---|---|---|---|
| A | `.zip` books | 6,768 | **They are real EPUBs under the wrong extension** — a 200-file sample found the OCF signature in all 200. `ArchiveFormatSniffer` had always routed them correctly for pages and thumbnails, but four places asked `Item.Extension` instead: `ReadPage.tsx` opened the CANVAS reader (which looks inside an EPUB for image pages, finds none), `EpubController` 404'd, the grid cover came from spine page 0, and the EPUB resource route 404'd every image a chapter referenced. |
| B | `.rar` books | 12,804 | ~87% an HTML book plus its images, 6.5% TXT, 2.5% RTF/DOC. No reader can present that, so all 12,804 carry `PageCount 0`, 1,319 carry "No decodable page found in archive", and every one shows a generated title card. **Not one has a sibling readable format.** |
| C | `.mobi` + `.azw3` | 4,240 | `MobiArchiveReader` greeks the text into pseudo-pages. It renders; it is not reading. |
| D | Invisible to the site | 11,433 | `LibraryScanner.SupportedExtensions` accepts ten extensions. Calibre holds 11,433 books whose only formats are LIT (4,271), TXT (3,305), RTF (1,830), DOC (751), FB2, PRC, AZW, PDB, DOCX, LRF, DJVU, CHM, HTML. The catalog has never seen them. |
| E | `.epub` flagged broken | 1,119 | Mostly parser strictness, not corruption. |
| F | comics | 37 | 24 `.7z` with thumbnail errors, 12 `.cbr` "no decodable page", 1 `.cbz`. Unrelated tail. |

Under the rule "a book needs an EPUB when Calibre holds no format this site reads" (no EPUB, no PDF,
no CBZ/CBR), **35,162 of 136,366 books qualify**.

## The parser was the deeper half

Routing the `.zip` books to the EPUB reader is necessary and **not sufficient**. Measured over 1,200
books, counting how many VersOne hands back a non-empty reading order:

| sample | `RELAXED` (what the site used) | `IGNORE_ALL_ERRORS` |
|---|---|---|
| 400 healthy `.epub` | 397 | 400 |
| 400 flagged broken | 298 | **355** |
| 400 `.zip` (all real EPUBs) | **0** | **392** |

Every one of those `.zip` books declares a `toc.ncx` it does not ship, and the strict parse refuses the
whole book over a missing table of contents. Not one book in the 1,200 came back with an EMPTY reading
order under any preset, so the permissive parse is not buying "readable" by quietly returning nothing.

`EpubArchiveReader` (covers, pages, embedded metadata) was separately using the *default* preset, which
is stricter still — so a book could have a cover and still refuse to open, or open and have no cover.
Both halves now share `Archives/EpubParsing.cs`.

## The changes

**Read side**

- `Archives/EpubParsing.cs` (new) — the one parser-options choice, with the measurement above, plus
  `ReadOrThrow`/`ReadOrThrowAsync` so a null parse becomes the failure every caller already handles.
- `ArchiveFormatSniffer.ReaderFormatFor(isBook, path, ext, ticks)` — the memoized "which reader
  surface", sniffed for a BOOK with a generic container extension and taken on trust for a comic.
- `ItemDetail.ReaderFormat` → `readerFormat` in the item payload; `ReadPage.tsx` reads it instead of
  `summary.extension`. `EpubController` and both media-plane EPUB routes in `HostEndpoints` ask it too.

**Catalog side — one Calibre book stays ONE item**

Conversion adds an EPUB *beside* the original (nothing is ever deleted from the share), so a book ends
up with two files and the scanner would index both — 28,500 duplicate rows, each pair splitting one
book's reading position, marks, insights and series link. `CalibreImportService` now:

- ranks a book's formats (`FormatRank`: epub → pdf → comic archives → azw3 → mobi → zip → rar → rest)
  and orders `ResolvePaths` by it;
- **upgrades** an item whose current format ranks strictly worse than the best the book now has,
  re-using the existing re-path machinery (so a duplicate row a scan already made is folded in);
- refuses the upgrade when the better file is not on the share (a phantom Calibre format row), counted
  as `upgrades-missing`;
- **drops the item's cached thumbnail** on an upgrade, because `books-thumbs` skips any item that
  already has one and the title card would otherwise outlive the conversion that gave it a real cover.

A settled library takes none of these branches. A dry run over the WHOLE library (136,366 Calibre
books, 29 batches, 2026-09-07) reported:

```
matched 124761, unmatched 11606, repathed 0, duplicates-merged 0, collisions 0,
upgraded 27, upgrades-missing 0, retired 763
```

`repathed 0` is the regression guard — the change is inert on books that are already right.
`upgraded 27` is the branch firing on the books that already hold a better format than the item sits
on, before a single conversion has run. `unmatched 11606` is bucket D: those books have no catalog item
to match yet.

> ⚠ **`retired 763`.** That is the pre-existing retirement sweep, not part of this work: a full
> `books-import-calibre --apply` marks missing every item whose Calibre entry is gone. It runs only on
> the TERMINAL batch, it is well under the 20 % refusal guard, and marking is not deleting — but it is
> 763 items leaving browse, and it should be a decision, not a surprise. `--max-batches` stops short of
> it.

**Why the scanner was NOT widened.** Accepting `.lit`/`.txt`/`.rtf`/… would manufacture 11,433 catalog
rows with no reader — the exact complaint being fixed — and each would then compete with its own
converted EPUB. Those books become visible *as their EPUB*, which is one row and a readable one.

## The conversion

Two phases, because only the second writes to Calibre and it must be the sole `calibre-debug` process.

| | script | what it does |
|---|---|---|
| 1 | `scripts/books/convert_unreadable.py` | Reads the share, writes EPUBs to a **staging** directory. Parallel, restartable, touches nothing in Calibre. RAR/ZIP are extracted (a ZIP that IS an EPUB, and an EPUB found inside a RAR, are **copied, never re-converted**); legacy `.doc` goes through LibreOffice first, wherever it is found; everything else goes straight through `ebook-convert`. |
| 2 | `scripts/books/convert_add_formats.py` | Under `calibre-debug`: `cache.add_format(book, 'EPUB', file, replace=False)`. Sets `DB.PATH_LIMIT = 56` first. Never renames a folder, never overwrites an existing format, never deletes the original. |

`scripts/books/run_convert.ps1` is the driver — the repeat loop lives there, with a no-progress break;
both scripts do a bounded batch, print `{processed, converted, failed, remaining}`, journal one row per
Calibre book id in `convert-journal.db`, and require `--apply` to write anything.

### Measured on real files

150 books, 6 workers, **39 s** (~0.26 s/book) — 140 converted, 2 failed, 8 blocked on `.doc`.
At that rate the full 35,162 is roughly **2–3 hours**. Verified with the site's own reader stack:
of 142 staged EPUBs, **142 open with a non-empty spine and a real cover**.

Per-format spot checks: LIT 0.4 s · AZW3 1.0 s · TXT 0.6–2.9 s · RTF 1.6 s · PDB 1.0 s · FB2 0.7 s ·
PRC 1.3 s · LRF 0.5 s · DJVU 0.7 s · CHM 25–68 s.

Two defects found and handled rather than papered over:

- **`SplitError`** — a long unstructured document (a plain `.txt` novel) makes the EPUB splitter give
  up. One targeted retry with `--flow-size 0` converts it cleanly. Not a general retry loop.
- **A PDF sealed inside a `.rar`** — 100 of the 108 "nothing convertible inside" failures held a PDF.
  A PDF is skipped when it is the book's OWN format (the site reads it, and PDF→EPUB is a worse book),
  but inside an archive it is unreadable, because the item IS the archive. It is now lifted out and
  staged **as a PDF**, added to Calibre under that format — the journal carries a `staged_fmt` so
  phase 2 adds each file as what it actually is.
- **Calibre refuses some RTF outright** — "This RTF file has a feature calibre does not support.
  Convert it to HTML first" (85 books). LibreOffice reads all of them, so after Calibre has had its
  turn the document is rounded through LibreOffice to `.docx` and offered again. Tried once, only for
  formats LibreOffice actually reads.
- **LibreOffice fails SILENTLY in parallel** — two `soffice` processes sharing the default user
  profile do not both convert: the second sees the first's lock, exits non-zero having printed
  NOTHING, and the book fails with an empty reason. It works serially and fails under workers, which
  reads as "LibreOffice cannot read this file". Every call now gets its own
  `-env:UserInstallation` under the book's temp directory, and `soffice.com` (which blocks) is
  preferred over `soffice.exe` (which can return before the file is written).
- **`Break signaled`** — 7-Zip PROMPTS for a password on an encrypted archive, and a prompt against a
  closed stdin looks like a hang. A dummy `-p` makes it fail immediately with a reportable reason.

Calibre's own title and authors are passed to every conversion, so the new EPUB's metadata *and* the
cover Calibre generates for a book with no artwork are right at the source.

### Runbook

```powershell
.\scripts\books\run_convert.ps1 -Plan                 # the whole population, nothing written
.\scripts\books\run_convert.ps1 -Phase 1 -Batches 1   # ONE batch, watch it
.\scripts\books\run_convert.ps1 -Phase 1 -Apply       # convert to staging, to completion
# inspect the staging directory, then:
.\scripts\books\run_convert.ps1 -Phase 2 -Apply       # add the staged EPUBs to Calibre
```

Then, in this order — none of it is automatic:

```
books-import-calibre --apply --reset    # moves each item onto its new EPUB; reports "upgraded"
books-resolve --series
books-thumbs                            # real covers replace the generated title cards
```

`books-import-calibre` is what turns "the book now has an EPUB" into "the site opens the EPUB".

## Known gaps

- **751 `.doc` books need LibreOffice installed** (`winget install TheDocumentFoundation.LibreOffice`,
  elevated). Until then they are journalled `blocked` with that reason — a listed number, not a silent
  loss. The pre-step is written and applies to a `.doc` found inside an archive too.
- **26 `.djvu`**: one of two spot checks failed on a 55 MB file. Expect a handful in the failure list.
- A handful of **encrypted RARs** cannot be opened at all.
- ~120 of the 1,119 `.epub` flagged broken fail under every parser preset — genuine ZIP corruption.
  An unparseable book gives the reader a 500, which the SPA already renders as
  "Failed to load this book."; it is not silent, but it is not a 4xx either.
- The 37 broken comics (F) are untouched.
