namespace MovieTheater.Db
{
    /// <summary>Where a Takeout item stands against the local library (photos-plan.md §2.10).</summary>
    public enum PhotoGoogleItemStatus
    {
        /// <summary>Seen in the archive, not yet run through the match pass.</summary>
        Pending = 0,

        /// <summary>Matched to a <see cref="PhotoGoogleItem.MatchedPhotoAssetId"/>. Once matched, always
        /// matched — re-running against a later archive upserts rather than re-deciding.</summary>
        Matched = 1,

        /// <summary>The match pass drained without finding a local file: a Google-only item, which is
        /// the review list the (opt-in, additive, non-overwriting) download lane draws from.</summary>
        Unmatched = 2,

        /// <summary>A Google-only item a human decided not to bring down.</summary>
        Ignored = 3,

        /// <summary>Copied into the dated sync folder and ingested normally.</summary>
        Downloaded = 4,
    }
}
