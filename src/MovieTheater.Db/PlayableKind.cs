namespace MovieTheater.Db
{
    /// <summary>What a <see cref="Playable"/> represents — a streamable/schedulable unit.</summary>
    public enum PlayableKind
    {
        Movie = 0,
        Episode = 1,

        /// <summary>A library video with no IMDb id of its own (workprint, stage performance,
        /// instructional/shorts set); see <see cref="MiscVideo"/>.</summary>
        MiscVideo = 2,
    }
}
