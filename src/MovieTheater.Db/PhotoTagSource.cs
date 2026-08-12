namespace MovieTheater.Db
{
    /// <summary>
    /// How a <see cref="PhotoPersonTag"/> came to exist (photos-plan.md §2.8). The split is the whole
    /// point of the tag queue: nothing auto-confirms, so a face cluster's fan-out lands as
    /// <see cref="Suggested"/> rows that a human either promotes to <see cref="Confirmed"/> or deletes.
    /// Browse surfaces and person pages count only Manual/Confirmed.
    /// </summary>
    public enum PhotoTagSource
    {
        /// <summary>A human tagged this photo directly.</summary>
        Manual = 0,

        /// <summary>Proposed by the Immich sync (§2.4). Pending review; never treated as truth.</summary>
        Suggested = 1,

        /// <summary>A suggestion a human accepted.</summary>
        Confirmed = 2,

        /// <summary>
        /// A suggestion a human REFUSED. The row survives as a tombstone rather than being deleted,
        /// which is what stops the next <c>photos-sync-immich</c> proposing the same (asset, person)
        /// again — the <see cref="PhotoDupeGroupStatus.Rejected"/> stance applied to faces, and for the
        /// same reason: a review queue that re-asks a question you already answered is a review queue
        /// nobody opens.
        ///
        /// <para>Stored as a state on the tag row deliberately: the (asset, person) uniqueness this
        /// table already carries is exactly the key a "do not propose this again" record needs, so
        /// remembering the refusal costs no new column and no migration.</para>
        /// </summary>
        Rejected = 3,
    }
}
