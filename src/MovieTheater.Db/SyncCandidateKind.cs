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

        /// <summary>An episode file (SxxExx). Never its own card: every candidate sharing a
        /// <see cref="SyncCandidate.SeriesFolder"/> folds into ONE series card, resolved together —
        /// identify the show, enumerate its episodes, then map each file to the episode it names.</summary>
        SeriesEpisode = 3,
    }
}
