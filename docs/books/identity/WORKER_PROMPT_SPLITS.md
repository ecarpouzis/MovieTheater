# Split-lane worker prompt (the lead pastes this, filling in the batch names)

You are a reading worker on the comics identity pass for the MovieTheater project (repo `F:\Work\MovieTheater`).
Work alone; do not spawn subagents. You never write to the database, never run a `books-*` verb or the BooksHost
exe, never open a book archive, and never touch another batch's files.

Read `F:\Work\MovieTheater\docs\books\identity\READER_BRIEF.md` in full first — it is the whole contract. The
section you live in is **"Split batches"**; the rulings on several runs on one shelf and on RESIDUE decide what
counts as a run. Then read the `## Conventions for this batch` block at the top of each batch file. **Do not open
`LEDGER.md` whole**; `grep` it for a publisher or folder word if a shelf needs a convention the block lacks.

Your batches: {{BATCHES}} — each is `F:\Work\MovieTheater\docs\books\identity\batches\<name>.txt` (the packets)
and `<name>.ids` (SHELF ids).

## What a split batch is

Every shelf here was read already and refused (`R`, sometimes an `S` at 0.9) with `F split-needed`: it holds
several publishing runs, and no identity can be stored until each run has a shelf of its own. The split is done
by `books-series-split`, which gives the items you name a new ParsedSeriesKey; the next resolve builds the new
shelf from it. Your answer is exactly that list. Each packet shows:

- `decided:` / `F` / `N` — the winning decision, its proposed runs (often with item ids already), and its notes.
- `named:` — shelves the F line mentions, with their keys: a run that belongs WITH one of them joins it by using
  that shelf's key (S6791's 1963 issues gathering S66349's).
- `nearby:` — live shelves the F line did NOT name but a run here may belong to: `shares cv=/gcd=` (their S line
  or stored cv holds an id the F line or the per-file links name) first, then same title words (`title`, or
  `title+1` = one extra word). A run whose record is already another shelf's S identity is a JOIN (Elric S6101 →
  S6105; X-Men: Red S22296's 2022 run → S94820), not a new shelf.
- `keys now:` — the ParsedSeriesKeys the files carry today (a moved item's new key must differ from its own).
- `G1 … Gn` — the items grouped by filename title x folder, with numbers, pages and v1's per-file CV/GCD links.
  Inside a group, same-worded numbered files print as `id #num year pp` tokens under one example filename.

## What you write — `decisions/<name>.jsonl`, one JSON object per line

```
{"shelf": 9845, "split": true, "why": "Storm Front Volume 1 (CV 23697 / GCD 35902, Dabel 2008-2009) stays; the four 03 Storm Front - Volume 2 files (CV 27202; GCD 55645 #1 + 52657 #2-4) move to a run of their own"}
{"itemId": 107776, "key": "Jim Butcher's The Dresden Files - Storm Front v2 (2009)", "run": {"cv": 27202, "gcd": [55645, 52657]}}
```

- One `shelf` line for EVERY shelf in the `.ids`. `split: false` (with a why) when the flag was wrong or the
  shelf is one dominant run with residue — then no item line for it.
- One item line per item that MOVES. The largest run STAYS and is not written. A shelf never empties.
- One key per new run, spelled identically on every line: `<Title> v<N> (<Year>)` or `<Title> (<Year>)`. A
  one-shot or special takes its OWN name — `Aquaman Secret Files & Origins 2003 (2003)`, `Uncanny X-Men Special
  (2014)` — never the parent's key with `#0`. Never an existing shelf's key unless the F line names that shelf
  or the shelf line carries a lead-approved `"join": [<sid>]` (for a `nearby:` shelf the F line missed — ask
  the lead in your report if you cannot get the approval in-session; do not add it on your own). Two spellings
  that differ only in case or punctuation are ONE key to the resolver and the checker refuses them.
- Many items at once (a 900-file shelf is not typed item by item):
  `{"group": "G6", "files": 66, "key": …, "run": …}` moves the packet's group G6 whole (`files` = the count the
  packet printed; a drifted group refuses). `{"range": "#1-66", "match": "Uncanny X-Men", "folder": "Complete
  Chronology", "except": [117639], "key": …}` moves the NUMBERED issue files (never collections) numbered in
  the range whose filename carries every `match` word (and folder every `folder` word), minus `except`. Two
  matched files with one number are refused as ambiguous — narrow it, or `"dupes": true` for rips of one issue.
  In a batch of several shelves add `"from": <sid>`. Mix freely with item lines; an item named twice is refused.
- `run` names the new run's records when you know them (`cv` volume id, `gcd` series id or a list, `mu`, …),
  checked against the catalogues; it seeds the new shelf's `S` line. Omit it rather than guess.
- A fragment of one file that belongs to no run of its own (a stray issue of another title) moves only if the F
  line or your reading gives it a run; otherwise leave it and say so in the shelf line's `why`.

## Loop

Write the file with the Write tool (bash mangles backslashes), run
`python F:\Work\MovieTheater\docs\books\identity\tools\check_splits.py <name>` until it prints 0 failures, then
the next batch. A `WARN … probable MISSED JOIN` is not a failure but must be answered: join (with approval), or
say in the shelf line's `why` why that shelf is another run. `--project <name> --out <scratch file>` shows
exactly which item ids your group / range lines expand to. Lookups: `python lookup.py "<name>" [--year YYYY] [--contains]`, `--issues <cvVolumeId>`,
`--gcd-issues <gcdSeriesId>`, `--gcd-series "<name>"`, or all of them in one file via `--batch <file>`, from
`docs\books\identity\tools`. The web is closed — do not try.

## Report back (≤ 25 lines)

Per batch `{shelves, split, kept (split=false), items moved, new keys, joins}`; every join you want approved and
every WARN you answered in a `why`; shelves where the F line's
proposal was wrong and what you did instead; conventions learned (one line each, naming the publisher or folder
shape); any instruction-vs-code conflict presented, not resolved. If a Stop hook repeats a finding, answer once
and end.
