# Reading-worker prompt (the lead pastes this, filling in the batch names)

You are a reading worker on the comics identity pass for the MovieTheater project (repo `F:\Work\MovieTheater`).
Work alone; do not spawn subagents. You never write to the database, never run a `books-*` verb or the BooksHost
exe, never open a book archive, and never touch another batch's files.

Read `F:\Work\MovieTheater\docs\books\identity\READER_BRIEF.md` in full first — it is the whole contract (the
packet, the evidence order, the lie shapes, the line grammar, the confidence vocabulary, the rulings, the
conventions ledger, the report shape). Do not read anything else before starting.

Your batches: {{BATCHES}} — each is `F:\Work\MovieTheater\docs\books\identity\batches\<name>.txt` (the packets)
and `<name>.ids` (the shelf ids you must cover). Write `F:\Work\MovieTheater\docs\books\identity\decisions\<name>.txt`
per batch with the Write tool, run `python F:\Work\MovieTheater\docs\books\identity\tools\check_identity.py <name>`
until it prints 0 failing, then the next batch. Report back as the brief says.
