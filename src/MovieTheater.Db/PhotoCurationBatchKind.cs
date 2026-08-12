namespace MovieTheater.Db
{
    /// <summary>
    /// Which review lane a <see cref="PhotoCurationBatch"/> belongs to (docs/photos-plan.md §2.5 ingest
    /// quarantine, §2.9 suggested-hide proposals). One table for both because they are the same shape —
    /// a machine proposed something in bulk, a human decides it in bulk — and two tables that differ
    /// only in a word would need every review surface written twice.
    /// </summary>
    public enum PhotoCurationBatchKind
    {
        /// <summary>An ingest marker a human has let into the timeline (§2.5). The row's existence with
        /// <see cref="PhotoCurationBatchStatus.Accepted"/> IS the approval; absence is quarantine.</summary>
        IngestApproval = 0,

        /// <summary>One <c>photos-suggest-hide</c> run's proposal (§2.9), with a
        /// <see cref="PhotoCurationBatchItem"/> per proposed asset carrying the rule that proposed it.</summary>
        HideProposal = 1,

        /// <summary>
        /// The single row that records WHEN ingest review was first materialized (§2.5's baseline rule).
        ///
        /// <para>Without it, "no approval rows exist" is ambiguous between "the feature has never run"
        /// and "nothing has been approved yet" — and reading it the second way would quarantine a
        /// collection that was ingested before the feature existed, emptying the one surface whose job
        /// is to show that nothing was lost. The marker makes the question answerable, and it is why
        /// quarantine only ever describes what arrives AFTERWARDS.</para>
        /// </summary>
        IngestBaseline = 2,

        /// <summary>
        /// The single row recording the last <c>photos-sync-immich</c> run: its
        /// <see cref="PhotoCurationBatch.Cursor"/> holds the SIDECAR VERSION that produced the current
        /// crop of suggestions (§2.4's version pin).
        ///
        /// <para>Not a review lane — nothing here is decided by a human — but the same shape (a marker
        /// with a cursor and a timestamp), and reusing the table is what let Phase 4 ship with no
        /// migration against a database that is shared with production. "Which Immich proposed this" is
        /// the first question a surprising suggestion raises, and it must be answerable months later.</para>
        /// </summary>
        ImmichSync = 3,

        /// <summary>
        /// The <c>photos-sync-jellyfin</c> reserved-folder-name audit (§2.3's ⚠ trap): one batch per
        /// run, one <see cref="PhotoCurationBatchItem"/> per video sitting inside a folder whose name
        /// Jellyfin's core folder walk reserves for extras and therefore drops.
        ///
        /// <para>Not a proposal a human accepts or rejects — <b>there is no action to take on it that
        /// this pipeline is allowed to perform</b>, because the fix would be a rename under the
        /// collection root and §6 forbids that absolutely. It is a REPORT, and it lives here for the
        /// Phase 3 reason: the site pods cannot read the CLI host's report directory, so a
        /// JSON-backed audit renders empty in production while looking healthy.</para>
        /// </summary>
        JellyfinReserved = 4,
    }
}
