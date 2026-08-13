namespace MovieTheater.Db
{
    /// <summary>
    /// Which SHELF a photo (or an album) lives on (docs/photos-plan.md §2.12, Phase 7).
    ///
    /// <para><b>Why this is not the <see cref="PhotoAsset.Hidden"/> flag.</b> The tree carries piles of
    /// art and memes — §1 already named them ("Misc Pics", papercraft/reference folders) — and the
    /// owner's verdict is that they "are not album material … remove them from the typical timeline …
    /// We'll want a place to store art and memes eventually, but it isn't the timeline, put them in
    /// another section." Hiding them would have been the closest existing tool and it is the WRONG one:
    /// since Phase 4 the hidden pile is revealed only to an ADMIN, so hiding art would take it away from
    /// the family rather than move it. This is the opposite instruction — the pictures stay fully
    /// browsable to every member, they simply stop being part of the family record.</para>
    ///
    /// <para><b>The two flags compose, and Hidden always wins.</b> Shelf answers "which section is this
    /// in"; Hidden answers "may a non-admin see it at all". An archived AND hidden asset is admin-only
    /// everywhere, exactly as it was before this enum existed — the NWS corner of the collection is
    /// precisely that case.</para>
    ///
    /// <para><b>Storage semantic vs. user-facing name.</b> <see cref="Archive"/> is what the column
    /// MEANS: off the family timeline, on its own shelf. What the site CALLS that shelf is the Gallery
    /// (§2.12's museum treatment), and an <see cref="PhotoAlbum.ArtistName"/> on an archive album makes
    /// it an artist collection. Naming the value for its storage meaning keeps the column honest if the
    /// section is ever renamed again.</para>
    ///
    /// <para>Nothing here is a file operation (§6). A shelf move is one int on one row.</para>
    /// </summary>
    public enum PhotoShelf
    {
        /// <summary>The family record: the timeline, the undated shelf, person pages. The default, and
        /// the value every row already written reads as — which is what makes Phase 7's migration purely
        /// additive.</summary>
        Timeline = 0,

        /// <summary>The Gallery: art, memes and reference piles. Excluded from the timeline, the undated
        /// shelf and person pages; present in the folder view (with a badge) and in whatever album holds
        /// it, because being browsable by every family member is the entire point.</summary>
        Archive = 1,
    }
}
