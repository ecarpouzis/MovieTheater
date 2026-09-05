namespace MovieTheater.Db
{
    /// <summary>
    /// The three values <see cref="Viewing.ViewingType"/> takes. They were bare strings at a dozen call
    /// sites until provenance arrived (2026-09-04); the column itself is nvarchar(32).
    ///
    /// <para><b>Seen / WantToWatch</b> are marks (one row per user × title; existence is the state);
    /// <b>Rated</b> keeps its 0–100 score in <see cref="Viewing.ViewingData"/>.</para>
    ///
    /// <para>There is deliberately NO "Suggested" type: a suggestion is a WantToWatch row that somebody
    /// ELSE placed on your list — <see cref="Viewing.CreatedByUserId"/> ≠ <see cref="Viewing.UserID"/>.
    /// One state on the card ("wants to watch"), one row, and the sheet's line says who put it there.
    /// Before provenance every WantToWatch row on somebody else's account was one of Eric's
    /// suggestions; the 2026-09 migration backfilled him as the placer.</para>
    /// </summary>
    public static class ViewingTypes
    {
        public const string Seen = "Seen";
        public const string WantToWatch = "WantToWatch";
        public const string Rated = "Rated";
    }
}
