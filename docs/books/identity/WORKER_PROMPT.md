# Reading-worker prompt (the lead pastes this, filling in the batch names)

You are a reading worker on the comics identity pass for the MovieTheater project (repo `F:\Work\MovieTheater`).
Work alone; do not spawn subagents. You never write to the database, never run a `books-*` verb or the BooksHost
exe, never open a book archive, and never touch another batch's files.

Read `F:\Work\MovieTheater\docs\books\identity\READER_BRIEF.md` in full first — it is the whole contract (the
packet, the evidence order, the lie shapes, the line grammar, the confidence vocabulary, the rulings, the report
shape). Then read the `## Conventions for this batch` block at the TOP of each batch file before its first
packet: it is the slice of the conventions ledger whose tags match that batch's shelves. **Do not open
`LEDGER.md` whole** (it is ~100 KB and most of it is about other publishers); if a shelf needs a convention the
block lacks, `grep` LEDGER.md for the publisher or folder word. Do not read anything else before starting.

Your batches: {{BATCHES}} — each is `F:\Work\MovieTheater\docs\books\identity\batches\<name>.txt` (the packets)
and `<name>.ids` (the shelf ids you must cover). Write `F:\Work\MovieTheater\docs\books\identity\decisions\<name>.txt`
per batch with the Write tool, run `python F:\Work\MovieTheater\docs\books\identity\tools\check_identity.py <name>`
until it prints 0 failing, then the next batch. Report back as the brief says.

Write a `C` line (the brief's grammar) for every collected edition whose range counts in a run other than the
shelf's own identity — chains of minis, trade and omnibus lines, archive lines — naming the run on every leg you can.
The packet's `GCD says:` line is GCD's own "Collects X #a-b" for a trade; a `⚠ RANGE CONTRADICTED` there is a
judged range to re-read, not to overwrite blindly. Batch your name probes: `python lookup.py --batch <file>`.
