namespace MovieTheater.Db
{
    /// <summary>Where a <see cref="PhotoCurationBatch"/> stands with its human (docs/photos-plan.md
    /// §2.9). Nothing a proposal describes is applied while it is <see cref="Pending"/> — that split is
    /// the entire reason a proposal is a row rather than the flag itself.</summary>
    public enum PhotoCurationBatchStatus
    {
        /// <summary>Waiting on a person. A hide proposal in this state has hidden nothing.</summary>
        Pending = 0,

        /// <summary>Accepted: the hide flags were written, or the ingest was let into the timeline.</summary>
        Accepted = 1,

        /// <summary>A human said no. Kept as a row so the surface does not re-ask, and so a bad rule is
        /// visible afterwards as one rejected cluster rather than as a gap.</summary>
        Rejected = 2,
    }
}
