namespace MovieTheater.Db
{
    /// <summary>
    /// What a <see cref="MovieFile"/> is, relative to its movie. One logical title is a single
    /// <see cref="Primary"/> plus 0..n other files. Lets a movie split across discs, carry alternate
    /// cuts, and keep featurettes — none of which have an IMDb id of their own.
    /// </summary>
    public enum MovieFileRole
    {
        /// <summary>The main feature presentation (the one a "watch" plays). Exactly one per movie.</summary>
        Primary = 0,

        /// <summary>An ordered part of a movie split across multiple files (CD1/CD2, disc 1/2); see <see cref="MovieFile.PartNumber"/>.</summary>
        Part = 1,

        /// <summary>An alternate cut of the same film (Director's Cut, Extended, Theatrical); see <see cref="MovieFile.Label"/>.</summary>
        Variant = 2,

        /// <summary>A bonus/extra (featurette, deleted scenes, behind the scenes) — not the film itself.</summary>
        Extra = 3,
    }
}
