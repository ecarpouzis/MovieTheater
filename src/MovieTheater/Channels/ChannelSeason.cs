using System;
using MovieTheater.Db;

namespace MovieTheater.Channels
{
    /// <summary>
    /// Deterministic seasonal visibility for a channel (Channels 2.0 §D). A channel with a season
    /// window (e.g. Spooky Season, Oct 1 – Nov 1) stays <see cref="Channel.Enabled"/> year-round — so
    /// its lineup never goes cold — but is hidden from the guide outside its window. Handles wrap-around
    /// windows (Dec → Jan). Null season parts ⇒ always in-season.
    /// </summary>
    public static class ChannelSeason
    {
        public static bool HasSeason(Channel c) => c.SeasonStartMonth != null && c.SeasonEndMonth != null;

        public static bool InSeason(Channel c, DateTime nowUtc)
        {
            if (!HasSeason(c)) return true;
            static int Md(int m, int d) => m * 100 + d;
            int start = Md(c.SeasonStartMonth!.Value, c.SeasonStartDay ?? 1);
            int end = Md(c.SeasonEndMonth!.Value, c.SeasonEndDay ?? 28);
            int today = Md(nowUtc.Month, nowUtc.Day);
            // Non-wrapping window (Oct→Nov): inside the range. Wrapping window (Dec→Jan): outside the gap.
            return start <= end ? today >= start && today <= end : today >= start || today <= end;
        }
    }
}
