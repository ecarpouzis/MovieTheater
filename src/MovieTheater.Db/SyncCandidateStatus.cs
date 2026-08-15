namespace MovieTheater.Db
{
    /// <summary>Lifecycle of a <see cref="SyncCandidate"/> across syncs and review actions.</summary>
    public enum SyncCandidateStatus
    {
        /// <summary>Detected; awaiting review (or awaiting detail resolution for a NewTitle).</summary>
        Pending = 0,

        /// <summary>A NewTitle whose quarantined <c>ReviewBatch</c> Movie row has been created
        /// (see <see cref="SyncCandidate.CreatedMovieId"/>); the normal review-ingest flow owns it now.</summary>
        Ingested = 1,

        /// <summary>The reviewer acted on this file against its target movie: either the movie was
        /// re-pointed to it (an upgrade) or it was attached beside the existing feature as an alternate
        /// version. Both are decisions about the same pairing, so both retire the candidate the same way.</summary>
        Approved = 2,

        /// <summary>Dismissed by the reviewer. Kept so the next sync does not re-offer the same path.</summary>
        Rejected = 3,

        /// <summary>Cleared by a later sync: the path became tracked some other way, or vanished.</summary>
        Superseded = 4,
    }
}
