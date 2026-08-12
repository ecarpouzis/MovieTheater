namespace MovieTheater.Db
{
    /// <summary>
    /// What kind of sameness a <see cref="PhotoDupeGroup"/> asserts (photos-plan.md §2.6). The kinds
    /// differ in who resolves them, not just in how they were found: Exact and Near are offered to a
    /// human to pick the better copy, while a Variant is two files that are supposed to both exist and
    /// must never be put up for that choice.
    /// </summary>
    public enum PhotoDupeGroupKind
    {
        /// <summary>Byte-identical (equal SHA256). Auto-grouped and auto-mastered, still listed for review.</summary>
        Exact = 0,

        /// <summary>Perceptually similar — the scanned-print problem: pHash within threshold, or an
        /// Immich CLIP candidate (which catches crops and recolors a pHash misses). Needs a human.</summary>
        Near = 1,

        /// <summary>One capture, several files BY DESIGN: RAW+JPEG, a motion photo's paired video half,
        /// a Live Photo's .heic+.mov, an edited export. Auto-paired, display half is master, never
        /// offered for "pick the better copy".</summary>
        Variant = 2,
    }
}
