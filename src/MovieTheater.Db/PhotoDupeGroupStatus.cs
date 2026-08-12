namespace MovieTheater.Db
{
    /// <summary>Review state of a <see cref="PhotoDupeGroup"/> (photos-plan.md §2.6). Resolution means
    /// a master was settled on — rows and flags only; no file is ever touched.</summary>
    public enum PhotoDupeGroupStatus
    {
        /// <summary>Awaiting a human's master pick. Auto-grouped Exact/Variant groups start here too
        /// (with a master already proposed) so nothing is silently decided.</summary>
        Pending = 0,

        /// <summary>A master is settled; non-masters are collapsed out of timeline/albums.</summary>
        Resolved = 1,

        /// <summary>A human said "these are not the same photo". Kept as a row so the grouping pass
        /// does not re-propose the pair on every run.</summary>
        Rejected = 2,
    }
}
