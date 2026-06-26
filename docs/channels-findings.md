# Channels — what the NAS inventory revealed

Profiled the live DB (6,280 movies / 322 series / 17,816 episodes, **100% AI-insight coverage**)
and the fresh NAS inventory (**98,086 files, 27,264 video files, 215 distinct TV shows**). Key
finding: **the on-disk folders contain whole collections the DB never tags as franchises** — the
files are the ground truth, exactly as predicted.

## Coverage status (the "every file on ≥2 channels" goal)

- **Movies:** essentially perfect. Only **6 streamable movies lack a genre**, and **0 lack AI
  insight** — so every movie airs on *Everything* + ≥1 genre + ≥1 mood/subgenre = **≥2 channels**.
- **TV / anime: fully ingested.** The library holds **26,226 MediaFile rows, 26,174 streamable
  (99.8%)**. A first cross-check *appeared* to show ~2,095 unmapped files (Pokémon, Dragon Ball,
  Ranma ½, JoJo…), but that was a **false alarm from an encoding bug**: dumping DB paths through
  sqlcmd's lossy text output mangled the accented folder names (Pokémon → Pok?mon) so they failed to
  match the inventory. Re-checked with a Unicode-safe dump (.NET `SqlClient` → UTF-8): **Pokémon's
  1,033 files all match, and true orphans are just ~32 files** (the `2 - Video\Misc` bin plus a few
  recent adds like Spidey and His Amazing Friends). The library is effectively **100% mapped and
  streamable — no ingest gap.** (Lesson: never diff DB paths via sqlcmd text output; it corrupts
  Unicode. Use `SqlClient` → UTF-8.)

## Collections the DB missed → new file-driven channels

The DB franchise tags had **no "looney tunes"** despite **1,147 Looney Tunes files** on disk. The
fix is a **path/folder filter** (`PathContains`) so a channel can target on-disk folders directly.
What the 215-show TV catalog surfaced (file counts):

**Cartoons & classic TV** — Looney Tunes (1,147) · Marvel Animated Series (539) · Walt Disney shorts
(439) · Tom & Jerry (161) · Hanna-Barbera: Flintstones (166)/Jetsons (73)/Scooby (125)/Rocky &
Bullwinkle (178)/Wacky Races · Disney Afternoon: DuckTales (100)/TaleSpin (65)/Darkwing Duck
(92)/Chip 'n Dale (65) · Batman TAS (109)/Batman '66 (131) · Tiny Toons (102)/Animaniacs (63).

**Nickelodeon** (real, by show — Network field is empty) — SpongeBob (510) · Ren & Stimpy (109) ·
Wild Thornberrys (91) · Are You Afraid of the Dark (92) · Pete & Pete (72) · CatDog (66) · Angry
Beavers (62) · Aaahh!!! Real Monsters (52) · Invader Zim (49) · KaBlam! (49) · All That (117) ·
Blues Clues (177) · Legends of the Hidden Temple (40) · Salute Your Shorts (26).

**Adult Swim / adult animation** — Aqua Teen (286) · Beavis & Butt-Head (196) · Venture Bros (86) ·
Duckman (75) · Rick and Morty (73) · Samurai Jack (64) · Metalocalypse (61) · Off the Air (58) ·
Harley Quinn (57) · Sealab 2021 (52) · Daria (71) · Celebrity Deathmatch (94) · Clone High (46).

**Kids & learning TV** — Mister Rogers (892!) · Peppa Pig (210) · Bluey (181) · Blues Clues (177) ·
Reading Rainbow (155) · Sesame Street (154) · Magic School Bus (52) · Schoolhouse Rock (31).

**Cult & prestige TV** — Star Trek (641) · MST3K (265) · Twilight Zone (157) · Lost (126) ·
Farscape (90) · Battlestar Galactica (76) · Lexx (63) · Doctor Who (49) · Quantum Leap (98) ·
Game of Thrones (94) · Seinfeld (171) · All in the Family (204) · Community (117) · Breaking Bad
(62) · Arrested Development (53) · Twin Peaks (48) · The Boys (48) · Archer (94) · Monty Python (45).

**Science & how-to** — How It's Made (325) · MythBusters (283) · Penn & Teller (92).

**Anime (ingested + needs-ingest)** — Naruto (220) · Fullmetal Alchemist (120) · A Certain Magical
Index (100) · Monster (74) · Demon Slayer (64) · Jujutsu Kaisen (60) · Cowboy Bebop · Evangelion ·
Trigun · Samurai Champloo · Steins;Gate · Attack on Titan · One Punch Man · Re:Zero.

**Collections** — **Criterion (383 files)** → a "Criterion Collection" arthouse channel.

**Movie buckets** — `1 - Movies\!Anime` (81 titles, 504 files) and `!Animated Movies` (139 titles,
incl. Disney Films 193) confirm the animated-movie depth.

## Engine implication

Added a **`PathContains`** filter (file-path substring match) — the escape hatch for tagging on-disk
collections. Plus the credit AND/OR upgrade (for pairings/ensembles), NC-17 exclusion, community-
watched count, and (empty-for-now) network match. These let the file-driven channels above be
defined precisely.
