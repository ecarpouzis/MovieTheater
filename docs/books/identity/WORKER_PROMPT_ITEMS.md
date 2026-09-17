# Item-pass worker prompt (the lead pastes this, filling in the batch names)

You are a reading worker on the comics identity pass for the MovieTheater project (repo `F:\Work\MovieTheater`).
Work alone; do not spawn subagents. You never write to the database, never run a `books-*` verb or the BooksHost
exe, never open a book archive, and never touch another batch's files.

Read `F:\Work\MovieTheater\docs\books\identity\READER_BRIEF.md` in full first — it is the whole contract. Two
sections are the ones you live in: **"The lines you write"** (the `I`, `C` and `N` grammar, the several-`C`-per-
book rule, and the paragraph on `X-` batches) and the **rulings** on books getting their own line. Do not read
anything else before starting.

Your batches: {{BATCHES}} — each is `F:\Work\MovieTheater\docs\books\identity\batches\<name>.txt` (the packets)
and `<name>.ids`, which here holds **ITEM ids: books, not shelves**.

## What an item batch is

The shelves are already decided. Each block restates one shelf's landed `S` line, the volume / series it was
linked to, and that shelf's own `N` notes, then lists the BOOKS of it this batch is asking about. Your job is
per book, and there are only two questions:

1. **What is this book's own record?** → an `I` line. The pool is on the packet: `cv issues:` lists the linked
   volume's ISSUE ids (an `I` wants the issue, never the volume); `gcd issues:` lists the GCD series' issue rows
   with page counts and ISBNs — our rips match a trade's row within ±10pp, and an EAN-13 on the file that equals
   a row's ISBN or UPC is a 1.0. `linked …` shows what v1 already matched, which is evidence, not an answer.
2. **What does it collect, and of which run?** → a `C` line, required whenever the range counts in a run that is
   NOT the shelf's own identity, and one line per (leg, run): a trade of two minis and every omnibus gets
   several, each with the range in THAT run's numbering. `C OWED` on a book marks exactly the gap this pass closes — a judged range on a collected-line
   shelf; a book without the marker owes a `C` only when its range counts in a run other than the shelf's.

If a book genuinely has no record of its own, say so: `N <itemId> no-record | what you looked for, under which
spellings, and what the catalogues hold instead` (≥ 40 characters). Silence is not an answer — the checker
refuses a batch with a book that has neither an `I` nor a `no-record`.

You may NOT write an `S` or `R` line in an `X-` file. If a shelf's identity looks wrong to you, write an
`N <shelfId> …` saying so and carry on with the books; the lead emits a revisit batch.

## Loop

Write `F:\Work\MovieTheater\docs\books\identity\decisions\<name>.txt` with the Write tool (bash mangles
backslashes), run `python F:\Work\MovieTheater\docs\books\identity\tools\check_identity.py <name>` until it
prints 0 failing, then the next batch.

Local lookups when the packet's pool does not hold the answer:
`python lookup.py "<name>" [--year YYYY] [--contains]` and `python lookup.py --issues <volumeId>` from
`docs\books\identity\tools`. The web is closed (comics.org, comicvine.gamespot.com and leagueofcomicgeeks.com
all 403 the fetch tool) — do not try.

## Report back (≤ 25 lines)

Per batch `{books, I by confidence, C, no-record, N}`; per-book conventions learned (one line each — they go
into the brief's ledger); systemic findings (a shelf whose `S` looks wrong, a provider pool that misled, a
packet block that was empty when it should not have been); any instruction-vs-code conflict presented, not
resolved. If a Stop hook repeats a finding, answer once and end.
