namespace MovieTheater.Db
{
    /// <summary>What a <see cref="SyncCandidate"/> appears to be, from the sync's classification.</summary>
    public enum SyncCandidateKind
    {
        /// <summary>The sync saw the file but couldn't tie it to a movie or parse it as one.</summary>
        Unclassified = 0,

        /// <summary>A new file that looks like a replacement/upgrade of an existing movie's file
        /// (same folder, unique same-size pair, or title match to a now-missing movie).</summary>
        Upgrade = 1,

        /// <summary>A file whose folder parses as a movie the library doesn't have yet.</summary>
        NewTitle = 2,
    }
}
