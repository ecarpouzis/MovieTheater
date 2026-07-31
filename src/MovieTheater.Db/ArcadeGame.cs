using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A playable arcade title (arcade-plan.md §5): one ROM on Ziggy's local disk, matched to a
    /// libretro core by file extension. Deliberately its own small table — arcade games are not
    /// Movies, and none of the movie plumbing (posters pipeline, OData, viewings) applies at v1.
    /// Populated by the <c>arcade-ingest</c> CLI (chunked/resumable/idempotent, upsert on the
    /// System+RomPath unique key; vanished files are flagged <see cref="IsEnabled"/>=false, never
    /// deleted).
    /// </summary>
    [Table("ArcadeGame")]
    public class ArcadeGame
    {
        [Key]
        public int Id { get; set; }

        [MaxLength(200)]
        public string Title { get; set; } = default!;

        /// <summary>Article-inverted sort key, same convention as <c>Movie.SimpleTitle</c>.</summary>
        [MaxLength(200)]
        public string SortTitle { get; set; } = default!;

        /// <summary>Punctuation/article-folded grouping key (<c>LaunchBoxMetadata.NormalizeTitle</c> of the
        /// Title) — the lobby groups cards by (System, CollapseKey), NOT exact Title, so different dumps of
        /// one game whose Titles differ only cosmetically ("Atlantis - The Lost Tales" ⇄ "Atlantis: The
        /// Lost Tales", "Daytona Usa" ⇄ "Daytonausa", ", The" ⇄ "The …") fold into ONE card. Derived from
        /// Title at ingest / <c>arcade-renormalize-titles</c>; indexed with System. Empty for a title with
        /// no alphanumerics (nothing to group on) — such rows fall back to grouping alone.</summary>
        [MaxLength(200)]
        public string CollapseKey { get; set; } = "";

        /// <summary>'nes','snes','genesis','gb','gbc','gba','n64','ps1','arcade' (§9 matrix).</summary>
        [MaxLength(20)]
        public string System { get; set; } = default!;

        /// <summary>Path relative to the workers' read-only ROM mount — the audit/ingest key.</summary>
        [MaxLength(400)]
        public string RomPath { get; set; } = default!;

        /// <summary>The launch key: the game name as CloudRetro's filename-based library scan exposes it
        /// (t=104 <c>game_name</c> / InitSession <c>games[].title</c>). Stored per game so a filename change
        /// on Ziggy can't silently orphan the catalog row (§3.3).</summary>
        [MaxLength(200)]
        public string CloudRetroGameKey { get; set; } = default!;

        /// <summary>Controller ports this title supports (N64: 4, SNES multitap: 5, GBA: 1).</summary>
        public byte MaxPlayers { get; set; } = 1;

        /// <summary>Rating ceiling on the same scale as the TV channel age gate; a room inherits its
        /// game's ceiling and is hidden from users whose AgeRestriction is below it.</summary>
        public int RatingCeiling { get; set; }

        /// <summary>Box art file on the posters mount, served via the /ArcadeImage route. Null = none yet.</summary>
        [MaxLength(400)]
        public string? BoxArtPath { get; set; }

        /// <summary>For a just-in-time (JIT) game, the source archive on the library drive (e.g. a PSX
        /// <c>.7z</c> in the L: master collection). Null for a directly-staged ROM whose file already
        /// lives under the ROM mount. When set, the ArcadeGateway extracts this into the ROM mount on
        /// demand at play time and LRU-evicts it later (docs/arcade-jit-cache.md); the row is browsable
        /// even while <see cref="RomPath"/> is not yet materialized on disk.</summary>
        [MaxLength(500)]
        public string? SourceArchivePath { get; set; }

        /// <summary>One ADDITIONAL file-or-directory a JIT game needs beyond its primary
        /// <see cref="SourceArchivePath"/>, staged into a subfolder named after <see cref="CloudRetroGameKey"/>
        /// — for the shapes a single archive can't express: a Naomi GD-ROM disc living in
        /// <c>&lt;name&gt;/gdl-XXXX.chd</c> beside its own boot-stub zip, or a ScummVM game whose "ROM" is a
        /// whole directory of loose data files (no single cartridge file at all — the primary
        /// <see cref="SourceArchivePath"/> is then a tiny generated <c>.scummvm</c> hook naming the target,
        /// and this field is the real game data). A companion that's a directory is copied recursively; a
        /// companion that's a file is copied as-is into the subfolder. Same-basename SIBLING files next to
        /// the primary archive (a CloneCD <c>.cue</c>'s <c>.img</c>/<c>.sub</c>/<c>.ccd</c>) need no entry
        /// here at all — the gateway copies those automatically, flat, alongside the primary. Null for every
        /// ordinary single-file game. Same LRU/eviction contract as <see cref="SourceArchivePath"/> — nothing
        /// here is ever deleted on the library drive, only the copy under the ROM mount.</summary>
        [MaxLength(500)]
        public string? SourceCompanionPath { get; set; }

        public int? Year { get; set; }

        /// <summary>Normalized release region parsed from the ROM filename tags — USA/Europe/Japan/World/
        /// Asia/Other/Unknown. An arcade-lobby filter; populated by <c>MovieTheater.Arcade.ArcadeRomTags</c>.</summary>
        [MaxLength(20)]
        public string? Region { get; set; }

        /// <summary>Release vs unofficial/modified dump parsed from the ROM filename — Release/Hack/Beta/
        /// Proto/Demo/Unlicensed/Pirate/BadDump. The lobby's "mods" filter (modded = Variant != Release).</summary>
        [MaxLength(20)]
        public string? Variant { get; set; }

        // ─── IGDB-sourced metadata (single-pass enrich via arcade-igdb; nullable = not yet resolved). One
        // lookup fills art (cover → BoxArtPath), the review score, and these discovery fields. Deliberately a
        // curated subset — not the full IGDB record (no screenshots/storyline/etc). ────────────────────────
        /// <summary>The matched IGDB game id — the refresh/dedupe key so re-enrich needn't re-search.</summary>
        public long? IgdbId { get; set; }

        /// <summary>IGDB <c>total_rating</c> (0–100, blends critic + user) and its sample count for a
        /// confidence gate. Null = no rating on IGDB (common for obscure arcade titles).
        /// <para>NOT the primary rating any more — IGDB's user score is wildly unreliable on obscure titles
        /// (American Chopper: 99.5 from 49 user votes and no critic score; LaunchBox says 65.7). It is now a
        /// FALLBACK, used only for the ~541 cards LaunchBox doesn't rate. See <see cref="LaunchBoxRating"/>.</para></summary>
        public double? RatingScore { get; set; }
        public int? RatingCount { get; set; }

        // ─── LaunchBox-sourced rating (arcade-launchbox). The PRIMARY rating source: it covers ~83% of cards
        // vs IGDB's 34%, and its community score tracks retro consensus far better. Stored on the card ANCHOR
        // (lowest-id row), same convention as the IGDB fields + box art. ──────────────────────────────────────
        /// <summary>LaunchBox <c>CommunityRating</c> rescaled from 0–5 stars to 0–100, with its vote count.</summary>
        public double? LaunchBoxRating { get; set; }
        public int? LaunchBoxRatingCount { get; set; }

        // ─── Hand-curated community rating. The THIRD rating source and the highest-precedence one, for the
        // titles the bulk importers structurally cannot reach: romhacks. LaunchBox and IGDB index published
        // games, so a hack either misses entirely or — worse — fuzzy-matches its BASE game and inherits a
        // score that isn't its own (OoT "4 Player Edition" came out of the LaunchBox dump at 10/100). These
        // columns hold a per-hack score researched from a source that actually rates hacks, and they win over
        // both importers so a re-run of arcade-launchbox / arcade-igdb cannot clobber the better number. ──────
        /// <summary>Community score 0-100 for this specific title. Beats <see cref="LaunchBoxRating"/> and
        /// <see cref="RatingScore"/> everywhere a score is read.</summary>
        public double? CommunityRating { get; set; }

        /// <summary>Vote count behind <see cref="CommunityRating"/>. NULL means the score is an editorial
        /// estimate rather than a poll — the Bayesian shrink then treats it as zero votes, which is correct:
        /// an unsourced number should carry no confidence and sink to its system's mean.</summary>
        public int? CommunityRatingCount { get; set; }

        /// <summary>Provenance for <see cref="CommunityRating"/>, shown to the user in the score tooltip so a
        /// researched estimate is never mistaken for a poll (e.g. "Backloggd", "romhacking.com",
        /// "Estimated - no community score"). Required whenever CommunityRating is set.</summary>
        [MaxLength(120)]
        public string? CommunityRatingSource { get; set; }

        /// <summary>Confidence-adjusted score used for <c>sort=rating</c> ONLY (never displayed): the effective
        /// raw score (LaunchBox, else IGDB) shrunk toward its system's mean by vote count —
        /// <c>(v/(v+m))·raw + (m/(v+m))·mean</c>, m=20. Without this a 1-vote 100 outranks a 4,000-vote 94.
        /// Recomputed wholesale by <c>arcade-rating-weights</c> whenever ratings change.</summary>
        public double? RatingWeighted { get; set; }

        /// <summary>Comma-separated IGDB genre names ("Shooter, Fighting") — a card badge + a lobby filter.</summary>
        [MaxLength(200)]
        public string? Genres { get; set; }

        /// <summary>Comma-separated IGDB theme names ("Action, Party") — mood/discovery; "Party" flags party games.</summary>
        [MaxLength(200)]
        public string? Themes { get; set; }

        /// <summary>One-paragraph IGDB summary for the card detail/hover (capped to avoid bloat).</summary>
        [MaxLength(1000)]
        public string? Summary { get; set; }

        [MaxLength(200)]
        public string? Developer { get; set; }
        [MaxLength(200)]
        public string? Publisher { get; set; }

        /// <summary>Comma-separated IGDB game modes ("Multiplayer, Co-operative, Split screen"). Co-op = has
        /// "Co-operative"; competitive = Multiplayer without it; split-screen = has "Split screen", else a
        /// multiplayer game is shared-screen (the arcade default).</summary>
        [MaxLength(200)]
        public string? GameModes { get; set; }

        /// <summary>IGDB multiplayer_modes offline max players — cross-checks the arcade <see cref="MaxPlayers"/>
        /// seat count (a mismatch flags a wrong seat config).</summary>
        public int? OfflineMaxPlayers { get; set; }

        /// <summary>ESRB rating category (e.g. "E", "T", "M") — can feed the <see cref="RatingCeiling"/> age gate.</summary>
        [MaxLength(20)]
        public string? EsrbRating { get; set; }

        /// <summary>Which delivery lane plays this title (docs/arcade-heavy-lane-plan.md §7.1).
        /// Null = 'cloudretro' (the in-browser WebRTC lane — every pre-heavy row). 'heavy' = streamed
        /// via Moonlight/Apollo; heavy rows join the same lobby/age-gate/box-art pipeline but their
        /// card action is Prepare/Play-via-Moonlight instead of creating a CloudRetro room, and
        /// <see cref="RomPath"/>/<see cref="CloudRetroGameKey"/> hold the heavy descriptor id.</summary>
        [MaxLength(20)]
        public string? Lane { get; set; }

        // ─── RetroAchievements support flags (arcade-ra-enrich; nullable/false = not yet checked or none).
        // Drive the card/version icons: 🏆 achievements, 🥇 high-score leaderboards, ⏱ speedrun (time)
        // leaderboards. Matched by normalized Title against RA's per-console game list, so they are
        // GAME-level (all versions of a title share them). RA is the definitions source; the actual
        // achievements/scores/times are recorded locally (see ArcadeAchievementUnlock/ArcadeLeaderboardEntry).
        /// <summary>The matched RetroAchievements game id (the enrich key; null = unmatched/unchecked).</summary>
        public int? RaGameId { get; set; }
        /// <summary>Number of core RA achievements for the matched game (0/null = none → no 🏆 icon).</summary>
        public int? RaAchievementCount { get; set; }
        /// <summary>True if the matched game has any SCORE/VALUE-format RA leaderboard (high scores → 🥇).</summary>
        public bool RaHasScoreLeaderboard { get; set; }
        /// <summary>True if the matched game has any TIME-format RA leaderboard (speedruns → ⏱).</summary>
        public bool RaHasTimeLeaderboard { get; set; }
        /// <summary>PER-VERSION: true if THIS exact dump's name matches one of RA's supported ROM hashes
        /// for the game (arcade-ra-enrich, API_GetGameHashes). Unlike the flags above (game-level), this
        /// distinguishes the RA-recognized dump from hacks/bad-dumps/unmatched regions in the same card,
        /// so <c>ArcadeVersions.Rank</c> floats the supported version to the top of the dropdown.</summary>
        public bool RaSupported { get; set; }
        /// <summary>When arcade-ra-enrich last resolved this row — the refresh/resume cursor.</summary>
        public System.DateTime? RaCheckedUtc { get; set; }

        public bool IsEnabled { get; set; } = true;

        /// <summary>The single canonical row of its (System, Title) group for the lobby's default lens —
        /// exactly one primary per game, chosen by the <c>arcade-dedupe</c> CLI (prefer official English
        /// release, highest revision, disc-1 / .m3u). Secondary rows (other regions, revisions, discs,
        /// hacks) stay enabled and reachable via filters but are hidden from the deduped default so a game
        /// shows once. Defaults true so a freshly-ingested row is visible until dedupe runs.</summary>
        public bool IsPrimary { get; set; } = true;

        public string? Notes { get; set; }
    }
}
