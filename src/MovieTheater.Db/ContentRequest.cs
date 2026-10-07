using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// The site's request queue (2026-10-06): "I'm out and saw a movie / record / boardgame / game we
    /// should have." One row per ask, from any signed-in user, visible to everyone. The queue is the
    /// communal wishlist for the LIBRARY (what to acquire), distinct from a user's Want-to-Watch list
    /// (what to watch out of what we already have).
    ///
    /// <para>The row carries which part of the site the content is for (<see cref="Section"/>), the
    /// title as the requester typed it, and the one extra field that disambiguates in that section
    /// (<see cref="Detail"/> — an artist for music, a system for arcade, an author for books). Those
    /// three plus the optional <see cref="Year"/> and <see cref="Link"/> are what a future acquisition
    /// job will key off, so they are kept as separate columns rather than folded into a blob.</para>
    ///
    /// <para>Resolution is loose and admin-confirmed: <see cref="ContentRequestMatcher"/> (in the web
    /// project) looks for a catalog row whose normalized title agrees with the request, and when it
    /// finds one it writes the <c>Match*</c> columns. Nothing is closed automatically — the admin
    /// confirms ("Added") or waves it off ("Not it"), and a waved-off key is remembered in
    /// <see cref="DismissedMatchKey"/> so the same row is not proposed twice.</para>
    ///
    /// <para>The requester FK is <c>Restrict</c>: a request is attributed, and losing the account
    /// should not silently rewrite who asked. The matched catalog item is an untyped
    /// (<see cref="MatchKind"/>, <see cref="MatchId"/>) pair with NO foreign key — the match is a hint
    /// about a row that may be re-ingested or deleted, and it must never block that.</para>
    /// </summary>
    [Table("ContentRequest")]
    public class ContentRequest
    {
        [Key]
        public int Id { get; set; }

        public DateTime CreatedUtc { get; set; }

        public int RequestedByUserId { get; set; }

        [ForeignKey(nameof(RequestedByUserId))]
        public User? RequestedBy { get; set; }

        /// <summary>Which part of the site this is for — one of <see cref="ContentRequestSections"/>.</summary>
        [MaxLength(16)]
        public string Section { get; set; } = default!;

        [MaxLength(200)]
        public string Title { get; set; } = default!;

        /// <summary>Release year when the requester knows it; narrows the match to ±1 year.</summary>
        public int? Year { get; set; }

        /// <summary>The section's disambiguator: artist (music), system (arcade), author (books),
        /// designer/publisher (boardgames). Free text; null when the requester left it blank.</summary>
        [MaxLength(200)]
        public string? Detail { get; set; }

        /// <summary>An outside URL the requester pasted — an IMDb / BGG / Discogs / store page. Kept for
        /// the admin and for the acquisition job that may one day read it.</summary>
        [MaxLength(500)]
        public string? Link { get; set; }

        /// <summary>Why / anything else — "the 4K cut", "only the first three seasons", a comment.</summary>
        [MaxLength(1000)]
        public string? Notes { get; set; }

        /// <summary>One of <see cref="ContentRequestStatuses"/>. Indexed with CreatedUtc: the page reads
        /// the open queue newest-first and the history behind a toggle.</summary>
        [MaxLength(16)]
        public string Status { get; set; } = ContentRequestStatuses.Open;

        public DateTime? ResolvedUtc { get; set; }

        /// <summary>Who closed it (an admin, or the requester withdrawing). No FK: a closed row is history.</summary>
        public int? ResolvedByUserId { get; set; }

        [MaxLength(400)]
        public string? ResolutionNote { get; set; }

        // ── The matcher's proposal (see the class summary) ─────────────────────────────────────────

        /// <summary>The catalog row kind the matcher proposed — "movie", "series", "album", "boardgame",
        /// "arcade". Null when there is no standing proposal.</summary>
        [MaxLength(16)]
        public string? MatchKind { get; set; }

        public int? MatchId { get; set; }

        /// <summary>The matched row's display title at proposal time, so the queue can say "looks like
        /// X (1999)" without a join into every vertical.</summary>
        [MaxLength(300)]
        public string? MatchTitle { get; set; }

        public DateTime? MatchFoundUtc { get; set; }

        /// <summary>"kind:id" of the last proposal an admin dismissed. The matcher skips that one key on
        /// its next pass so a wrong guess is not re-proposed forever; a different row may still be.</summary>
        [MaxLength(40)]
        public string? DismissedMatchKey { get; set; }

        /// <summary>When the request is fulfilled, the catalog row it was fulfilled WITH (the confirmed
        /// match, or whatever the admin picked). Same untyped pair; same no-FK stance.</summary>
        [MaxLength(16)]
        public string? FulfilledKind { get; set; }

        public int? FulfilledId { get; set; }
    }

    /// <summary>A "me too" on somebody else's request. One per user per request (unique index).</summary>
    [Table("ContentRequestVote")]
    public class ContentRequestVote
    {
        [Key]
        public int Id { get; set; }

        public int RequestId { get; set; }

        [ForeignKey(nameof(RequestId))]
        public ContentRequest? Request { get; set; }

        public int UserId { get; set; }

        public DateTime CreatedUtc { get; set; }
    }

    /// <summary>The sections a request can be for. The values are the SPA's section keys, so the page can
    /// link straight to the part of the site the content would land in.</summary>
    public static class ContentRequestSections
    {
        public const string Movies = "movies";
        public const string Music = "music";
        public const string Boardgames = "boardgames";
        public const string Arcade = "arcade";
        public const string Books = "books";

        public static readonly string[] All = { Movies, Music, Boardgames, Arcade, Books };

        public static bool IsValid(string? s) => s != null && Array.IndexOf(All, s) >= 0;
    }

    public static class ContentRequestStatuses
    {
        /// <summary>Waiting to be acquired.</summary>
        public const string Open = "open";
        /// <summary>Added to the library (admin-confirmed).</summary>
        public const string Fulfilled = "fulfilled";
        /// <summary>An admin said no (reason in ResolutionNote).</summary>
        public const string Declined = "declined";
        /// <summary>The requester took it back.</summary>
        public const string Withdrawn = "withdrawn";

        public static readonly string[] All = { Open, Fulfilled, Declined, Withdrawn };

        public static bool IsValid(string? s) => s != null && Array.IndexOf(All, s) >= 0;
    }
}
