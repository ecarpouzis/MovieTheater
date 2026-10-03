# Comic shelf rating pass — the rubric (2026-10, after the identity pass)

One judgement per SHELF (a `Series` = one publishing run). You are shown what the identity pass established about
the run — its ComicVine volume (name, start year, publisher, issue count, description), the creators credited in our
files, how many files/issues/collections we hold — and the CURRENT insight a weaker model wrote earlier, often from
the shelf name alone. Correct it.

## What to output — one JSON object per line, nothing else

```
{"id": 17509, "r": 74, "c": "H", "aud": ["teen"], "aw": [], "stale": false, "syn": null, "why": "..."}
```

| key | meaning |
|---|---|
| `id` | the shelf id (`S…` without the S) |
| `r` | rating 0–100, or `null` when there is truly no basis (a reprint pamphlet, a sketchbook, a run you cannot place). Never 0. |
| `c` | confidence: `H` you know this run (its reputation, creators, reception); `M` you know the creators/line and can place it; `L` an educated guess |
| `aud` | the audience tags: any of `all-ages`, `teen`, `mature`, `adult`. Usually ONE. Leave `[]` to keep the current insight's audience unchanged. |
| `aw` | award tags to ADD, lowercase-hyphenated (`eisner-award`, `harvey-award`, `hugo-award`, `eisner-nominee`…) — only awards you are sure of. `[]` = none to add. |
| `stale` | `true` when the current insight's PROSE describes something other than this run (one storyline, an annual, a relaunch's namesake, the wrong publisher's book). Its synopsis/credits/years are then dropped; credits are re-taken from our files. Tags are kept unless you replace them. |
| `g` | optional: the genre tags to REPLACE the current ones with, when they are wrong (lowercase-hyphenated: `superhero`, `horror`, `crime`, `sci-fi`, `fantasy`, `humor`, `drama`, `war`, `western`, `romance`, `anthology`, `slice-of-life`, `adventure`, `mystery`, `satire`…). Omit to keep them. |
| `syn` | a one/two-sentence synopsis ONLY when `stale` is true or there is no current synopsis and you know the work. Otherwise `null`. Never invent plot you do not know. |
| `why` | ≤ 12 words: the basis ("Ennis/McCrea, Eisner winner", "licensed tie-in, minor", "same as 2017 run? no — Image 2026 relaunch"). |

## The scale (the v1 convention, which the site's Explore rails read: spotlight ≥ 75, top-series ≥ 72)

| band | meaning | anchors |
|---|---|---|
| 90–100 | masterwork, canon of the medium | Watchmen, Maus, Sandman, Bone, Love and Rockets (Hernandez prime), Akira, Calvin and Hobbes, From Hell |
| 80–89 | essential, the best of its kind | Saga, Y: The Last Man, Hellboy (Mignola), Preacher, Transmetropolitan, Lone Wolf and Cub, Usagi Yojimbo, Astro City, Cerebus (High Society–Jaka's Story) |
| 70–79 | recommended, strong run | Invincible, The Walking Dead (early), Monstress, Something is Killing the Children, East of West |
| 60–69 | good, competent genre work | most solid creator-owned minis, decent licensed runs (IDW TMNT 2011, Titan Conan 2023), long-running house-style superhero |
| 50–59 | decent / for fans | routine licensed tie-ins, filler minis, variant/cash-in one-shots, weak relaunches |
| < 50 | flawed | notoriously bad runs only — say why |

Rules:

- **Rate the RUN, not the franchise.** Royals (Image 2026) is not Marvel's Royals (2017); Conan the Barbarian
  (Titan 2023) is not Marvel's 1970 run; The Harbinger (2021) is not Harbinger (1992). A relaunch earns its own
  number.
- **Use the full scale.** The previous passes put 91 % of shelves between 50 and 79. A spread is the point: a
  canonical work belongs in the 90s, a forgettable tie-in in the 50s. Do not drift to 65 because you are unsure —
  lower the CONFIDENCE instead.
- **Collected-edition shelves** (a shelf of trades / an omnibus line) are rated as the work they collect.
  Reference books, art books, sketchbooks, previews, FCBD samplers: `r` around 50–60 only if you know them,
  otherwise `null`.
- **Manga**: rate the series as published in English (its reputation, completion, translation line).
- **Magazines / anthologies** (2000 AD, Heavy Metal, MAD, Dark Horse Presents): rate the anthology's standing over
  the run we hold.
- **Audience** (this is the kids/teen gate — be careful): `all-ages` only when a child can read every issue
  (Bone, Owly, Adventure Time, Archie, Disney/Carl Barks, Tintin). `teen` = mainstream superhero, most licensed
  action, most shōnen. `mature` = graphic violence/sex/language (Vertigo, Black Label, most Image horror, Ennis).
  `adult` = sexually explicit. When unsure between two, take the OLDER audience (teen over all-ages, mature over
  teen). If the current audience is already right, leave `aud` as `[]`.
- **Never** guess an award, a creator, or a plot. `null` / `[]` are correct answers.
