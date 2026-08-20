using MovieTheater.Services.Igdb;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The gate that decides whether a SteamGridDB search hit is really the game we asked for. It exists
    /// because a wrong cover here is close to permanent: the cascade writes it to a shared posters mount the
    /// site cannot delete from, and every later request serves the cached file before it re-searches.
    ///
    /// <para>The false matches below are real. A 20-card backfill of coverless cards returned 6 covers and
    /// TWO were wrong — "Super Masters!" got a game named "Super", "Rack + Roll" got a visual novel named
    /// "Rack" — because the original gate accepted ANY 4-character prefix in either direction.</para>
    /// </summary>
    public class SteamGridDbNameGateTests
    {
        private static bool Match(string ours, string theirs)
            => SteamGridDbClient.NameMatches(SteamGridDbClient.Normalize(ours), SteamGridDbClient.Normalize(theirs));

        [Theory]
        // Exact once normalized — punctuation, case and parentheticals are folded away.
        [InlineData("Manx TT Superbike", "Manx TT SuperBike")]
        [InlineData("Gotzendiener", "Götzendiener")]                 // diacritics folded
        [InlineData("Rack + Roll", "Rack & Roll")]
        // Real edition/subtitle prefixes: the shorter name still covers most of the longer one.
        [InlineData("Sonic Adventure", "Sonic Adventure DX")]
        [InlineData("Metal Gear Solid", "Metal Gear Solid HD")]
        [InlineData("Sonic Adventure", "Sonic Adventure DX")]   // an EDITION suffix is not a sequel marker
        public void Accepts(string ours, string theirs) => Assert.True(Match(ours, theirs));

        [Theory]
        // The two that actually shipped wrong covers.
        [InlineData("Super Masters!", "Super")]
        [InlineData("Rack + Roll", "Rack")]
        // Same shape, other directions.
        [InlineData("Legend", "Legend of Zelda - Ocarina of Time")]
        [InlineData("Pang", "Pang Adventures Deluxe Edition")]
        [InlineData("Solo Crisis", "Solo")]
        // A residual that is a volume or sequel marker means SIBLINGS, however high the ratio. Both of
        // these shipped wrong covers: the CD-i educational discs "Tell Me Why One"/"Two" were given the
        // 2020 Dontnod adventure game's key art at 75%.
        [InlineData("Tell Me Why One", "Tell Me Why")]
        [InlineData("Tell Me Why Two", "Tell Me Why")]
        [InlineData("Icebreaker II", "Icebreaker")]
        [InlineData("Ridge Racer 2", "Ridge Racer")]
        [InlineData("Street Fighter", "Street Fighter III")]
        // Too short to risk a prefix match at all.
        [InlineData("Ico", "Ico and Shadow of the Colossus")]
        [InlineData("", "Anything")]
        public void Rejects(string ours, string theirs) => Assert.False(Match(ours, theirs));
    }
}
