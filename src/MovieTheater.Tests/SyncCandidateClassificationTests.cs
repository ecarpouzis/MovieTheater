using MovieTheater.Db;
using MovieTheater.Services.Jellyfin;
using MovieTheater.Services.Series;

namespace MovieTheater.Tests;

/// <summary>
/// The sync's candidate classification, exercised against the ACTUAL paths sitting in the
/// SyncCandidate table (captured 2026-08-14) rather than invented ones — the whole point of the
/// series lane is that it survives this library's real folder shapes: scene release dirs, "Season N"
/// dirs with a quality suffix, "Specials", a nested spin-off, and a DVD rip's non-video sidecars.
/// </summary>
public class SyncCandidateClassificationTests
{
    // ── The video gate (the .ifo/.bup false-upgrade bug) ──────────────────────────────────────

    [Theory]
    [InlineData(@"L:\1 - Movies\B\Brick (2025) 1080p\Brick.2025.1080p.NF.WEB-DL.DDP5.1.Atmos.H.264-playWEB.mkv", true)]
    [InlineData(@"L:\2 - Video\Misc\Stage Performances\Spamalot\Spamalot-1.avi", true)]
    [InlineData(@"L:\2 - Video\Series\Wacky Races (1968-1969)\Season 2\Wacky Races - S02E03.mkv", true)]
    [InlineData(@"L:\1 - Movies\M\Mr. Smith Goes To Washington (1939)\Mr.Smith.1939.CD1.DVDivX-DDX.mp4", true)]
    // The two rows that were being offered as upgrades of a working movie:
    [InlineData(@"L:\1 - Movies\M\Mr. Smith Goes To Washington (1939) [IMDB 101]\Mr.Smith.Goes.To.Washington.1939.CD1.DVDivX-DDX.ifo", false)]
    [InlineData(@"L:\1 - Movies\M\Mr. Smith Goes To Washington (1939) [IMDB 101]\Mr.Smith.Goes.To.Washington.1939.CD2.DVDivX-DDX.ifo", false)]
    [InlineData(@"L:\1 - Movies\X\Something\VIDEO_TS.BUP", false)]
    [InlineData(@"L:\1 - Movies\X\Something\poster.jpg", false)]
    [InlineData(@"L:\1 - Movies\X\Something\subs.srt", false)]
    [InlineData(@"L:\1 - Movies\X\no-extension-at-all", false)]
    public void IsVideoFile_admits_only_real_containers(string path, bool expected) =>
        Assert.Equal(expected, MovieFolderParser.IsVideoFile(path));

    [Fact]
    public void IsVideoFile_is_not_fooled_by_a_dot_in_a_folder_name()
    {
        // The folder carries dots; the file does not carry an extension. Nothing may be inferred
        // from the folder's ".0" — this is the case a naive LastIndexOf('.') gets wrong.
        Assert.False(MovieFolderParser.IsVideoFile(@"L:\2 - Video\Series\X\Nick.Arcade.S01.AAC2.0.x264-BTN\readme"));
    }

    // ── Episode identity ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Nick.Arcade.S01E07.Episode.7.540p.PMTP.WEB-DL.AAC2.0.x264-BTN", 1, 7, 7)]
    [InlineData("Nick.Arcade.S02E41.Episode.84.540p.PMTP.WEB-DL.AAC2.0.x264-BTN", 2, 41, 41)]
    [InlineData("SpongeBob SquarePants.S01E21.F.U.N.", 1, 21, 21)]
    [InlineData("SpongeBob SquarePants - S10E44 - Sold! (x265 10Bit)", 10, 44, 44)]
    [InlineData("Ren & Stimpy - S00E05   Fire Dogs 2 (1)", 0, 5, 5)]
    [InlineData("Star Trek Voyager - 5x06 Once Upon A Time  [snake_eyes]", 5, 6, 6)]
    [InlineData("S1E31 Lice", 1, 31, 31)]
    [InlineData("Show - S03E01-E02 - Double", 3, 1, 2)]
    public void ParseEpisode_reads_the_library_naming_shapes(string name, int season, int episode, int spans)
    {
        var got = MovieFolderParser.ParseEpisode(name);
        Assert.NotNull(got);
        Assert.Equal(season, got!.Value.Season);
        Assert.Equal(episode, got.Value.Episode);
        Assert.Equal(spans, got.Value.Spans);
    }

    [Theory]
    [InlineData("Brick.2025.1080p.NF.WEB-DL.DDP5.1.Atmos.H.264-playWEB")]
    [InlineData("Book of Mormon 2011-03-07 Act 1")]
    [InlineData("Hedwig (Neil Patrick Harris)")]
    [InlineData("tin.toy.1988.720p.bluray.x264-sinners")]
    // A resolution must never read as an episode: "1080p" has no x, "H.264" has no S/E pair.
    [InlineData("Masters.of.the.Universe.2026.2160p.AMZN.WEB-DL.DDP5.1.Atmos.DV.HDR.H.265-FLUX")]
    public void ParseEpisode_rejects_non_episodes(string name) =>
        Assert.Null(MovieFolderParser.ParseEpisode(name));

    // ── The series-root climb ─────────────────────────────────────────────────────────────────

    [Theory]
    // Scene release dir under the show folder — all 84 Nick Arcade files must land on ONE root.
    [InlineData(@"L:\2 - Video\Series\Nick Arcade (1992)\Nick.Arcade.S01.540p.PMTP.WEB-DL.AAC2.0.x264-BTN\Nick.Arcade.S01E07.Episode.7.540p.PMTP.WEB-DL.AAC2.0.x264-BTN.mkv",
        @"L:\2 - Video\Series\Nick Arcade (1992)")]
    [InlineData(@"L:\2 - Video\Series\Nick Arcade (1992)\Nick.Arcade.S02.540p.PMTP.WEB-DL.AAC2.0.x264-BTN\Nick.Arcade.S02E41.Episode.84.540p.PMTP.WEB-DL.AAC2.0.x264-BTN.mkv",
        @"L:\2 - Video\Series\Nick Arcade (1992)")]
    // Plain and suffixed season folders.
    [InlineData(@"L:\2 - Video\Series\SpongeBob SquarePants (1999-2020)\Season 1\SpongeBob SquarePants.S01E21.F.U.N..avi",
        @"L:\2 - Video\Series\SpongeBob SquarePants (1999-2020)")]
    [InlineData(@"L:\2 - Video\Series\SpongeBob SquarePants (1999-2020)\Season 10 1080p\SpongeBob SquarePants - S10E44 - Sold! (x265 10Bit).mkv",
        @"L:\2 - Video\Series\SpongeBob SquarePants (1999-2020)")]
    [InlineData(@"L:\2 - Video\Series\Invader Zim (2001)\Season 1\S1E31 Lice.avi",
        @"L:\2 - Video\Series\Invader Zim (2001)")]
    // "Specials" is a season folder too.
    [InlineData(@"L:\2 - Video\Series\Ren & Stimpy Show, The (1991-1996)\Specials\Ren & Stimpy - S00E05   Fire Dogs 2 (1).avi",
        @"L:\2 - Video\Series\Ren & Stimpy Show, The (1991-1996)")]
    // A nested spin-off: the root is Voyager, NOT the Star Trek umbrella folder.
    [InlineData(@"L:\2 - Video\Series\Star Trek (1966)\Voyager\Star Trek. Voyager Season 5\Star Trek Voyager - 5x06 Once Upon A Time  [snake_eyes].avi",
        @"L:\2 - Video\Series\Star Trek (1966)\Voyager")]
    public void SeriesRootOf_finds_the_show_folder(string file, string expectedRoot) =>
        Assert.Equal(expectedRoot, MovieFolderParser.SeriesRootOf(file));

    [Fact]
    public void SeriesRootOf_never_climbs_into_a_library_container()
    {
        // A show stored as ONE flat release folder directly under the shelf. Climbing past it would
        // make "…\Series" the group key and fold every unrelated show into a single bogus card.
        Assert.Equal(
            @"L:\2 - Video\Series\Some.Show.S01.1080p.WEB-DL",
            MovieFolderParser.SeriesRootOf(@"L:\2 - Video\Series\Some.Show.S01.1080p.WEB-DL\Some.Show.S01E01.mkv"));
        // Same guard one level up: the alpha buckets under "1 - Movies" are containers too.
        Assert.Equal(
            @"L:\1 - Movies\S\Some.Show.S01.1080p",
            MovieFolderParser.SeriesRootOf(@"L:\1 - Movies\S\Some.Show.S01.1080p\Some.Show.S01E01.mkv"));
    }

    [Fact]
    public void SeriesRootOf_groups_every_file_of_one_show_onto_one_key()
    {
        // The property the review card depends on: 84 files, 2 release folders, 1 card.
        var files = new List<string>();
        for (int e = 1; e <= 43; e++)
            files.Add($@"L:\2 - Video\Series\Nick Arcade (1992)\Nick.Arcade.S01.540p.PMTP.WEB-DL.AAC2.0.x264-BTN\Nick.Arcade.S01E{e:00}.Episode.{e}.540p.PMTP.WEB-DL.AAC2.0.x264-BTN.mkv");
        for (int e = 1; e <= 41; e++)
            files.Add($@"L:\2 - Video\Series\Nick Arcade (1992)\Nick.Arcade.S02.540p.PMTP.WEB-DL.AAC2.0.x264-BTN\Nick.Arcade.S02E{e:00}.Episode.{e + 43}.540p.PMTP.WEB-DL.AAC2.0.x264-BTN.mkv");

        var roots = files.Select(MovieFolderParser.SeriesRootOf).Distinct().ToList();
        Assert.Single(roots);
        Assert.Equal(@"L:\2 - Video\Series\Nick Arcade (1992)", roots[0]);
        Assert.Equal(84, files.Count);

        // And the per-file episode numbers are the FILE's own season numbering (S02E01, not E44) —
        // the trap in this release: its titles carry absolute numbers while its names do not.
        var s2e1 = MovieFolderParser.ParseEpisode(
            "Nick.Arcade.S02E01.Episode.44.540p.PMTP.WEB-DL.AAC2.0.x264-BTN");
        Assert.Equal((2, 1, 1), (s2e1!.Value.Season, s2e1.Value.Episode, s2e1.Value.Spans));
    }

    // ── Folder identity ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"Nick Arcade (1992)", "Nick Arcade", 1992)]
    [InlineData(@"SpongeBob SquarePants (1999-2020)", "SpongeBob SquarePants", 1999)]
    [InlineData(@"Ren & Stimpy Show, The (1991-1996)", "Ren & Stimpy Show, The", 1991)]
    [InlineData(@"Spidey and His Amazing Friends (2021-2022) 1080p", "Spidey and His Amazing Friends", 2021)]
    [InlineData(@"Voyager", "Voyager", null)]
    public void ParseSeriesFolder_accepts_year_ranges_and_no_year(string leaf, string title, int? year)
    {
        var got = MovieFolderParser.ParseSeriesFolder(leaf);
        Assert.NotNull(got);
        Assert.Equal(title, got!.Value.Title);
        Assert.Equal(year, got.Value.Year);
    }

    [Fact]
    public void ParseSeriesFolder_and_Parse_disagree_on_purpose()
    {
        // The MOVIE parser requires a plain (Year) and must keep refusing a range — a movie folder
        // never carries one, and accepting it would route series folders into the new-movie lane.
        Assert.Null(MovieFolderParser.Parse("SpongeBob SquarePants (1999-2020)"));
        Assert.NotNull(MovieFolderParser.ParseSeriesFolder("SpongeBob SquarePants (1999-2020)"));
    }

    [Theory]
    [InlineData("Season 1", true)]
    [InlineData("Season 10 1080p", true)]
    [InlineData("Specials", true)]
    [InlineData("Star Trek. Voyager Season 5", true)]
    [InlineData("Nick.Arcade.S01.540p.PMTP.WEB-DL.AAC2.0.x264-BTN", true)]
    [InlineData("Nick Arcade (1992)", false)]
    [InlineData("Voyager", false)]
    [InlineData("SpongeBob SquarePants (1999-2020)", false)]
    [InlineData("Star Trek (1966)", false)]
    public void IsSeasonFolder_separates_containers_from_shows(string leaf, bool expected) =>
        Assert.Equal(expected, MovieFolderParser.IsSeasonFolder(leaf));

    // ── The numbering guards (the silent-corruption cases) ────────────────────────────────────

    private static List<SyncCandidate> Files(params (int Season, int Episode)[] eps)
    {
        var id = 1;
        return eps.Select(e => new SyncCandidate
        {
            Id = id++,
            Path = $@"L:\x\S{e.Season:00}E{e.Episode:00}.mkv",
            SeasonNumber = e.Season,
            EpisodeNumber = e.Episode,
        }).ToList();
    }

    private static List<Episode> Catalogue(params (int Season, int Count)[] seasons)
    {
        var rows = new List<Episode>();
        var id = 1000;
        foreach (var (s, n) in seasons)
            for (int e = 1; e <= n; e++)
                rows.Add(new Episode { Id = id++, SeasonNumber = s, EpisodeNumber = e, Title = $"S{s}E{e}" });
        return rows;
    }

    [Fact]
    public void SeasonShape_catches_the_Nick_Arcade_boundary_shift()
    {
        // 43 + 41 on disk, 42 + 42 catalogued: the same 84 episodes, split differently. Every
        // season-2 file would map one episode early and NOTHING would look wrong.
        var files = Files(Enumerable.Range(1, 43).Select(e => (1, e))
            .Concat(Enumerable.Range(1, 41).Select(e => (2, e))).ToArray());
        var catalogue = Catalogue((1, 42), (2, 42));
        Assert.Equal(84, files.Count);
        Assert.Equal(84, catalogue.Count);

        var mismatch = SyncSeriesMatcher.SeasonShapeMismatch(files, catalogue);
        Assert.NotNull(mismatch);
        Assert.Contains("season 1 runs to E43", mismatch);
        Assert.Contains("stops at E42", mismatch);
    }

    [Fact]
    public void SeasonShape_catches_a_season_the_catalogue_does_not_have()
    {
        // Wacky Races: a file claims S02E03; the catalogued list is one season.
        Assert.Equal("season 2 is not in the catalogued episode list at all",
            SyncSeriesMatcher.SeasonShapeMismatch(Files((2, 3)), Catalogue((1, 17))));
        // Ren & Stimpy: a Specials file (S00E05) against a list with no season 0.
        Assert.Equal("season 0 is not in the catalogued episode list at all",
            SyncSeriesMatcher.SeasonShapeMismatch(Files((0, 5)), Catalogue((1, 6), (2, 20))));
    }

    [Fact]
    public void SeasonShape_catches_segment_numbering()
    {
        // SpongeBob's files number SEGMENTS (S01E21, S01E31) while the catalogue numbers EPISODES
        // (season 1 has 20). This is the case that would map segment 21 onto some later episode.
        var mismatch = SyncSeriesMatcher.SeasonShapeMismatch(Files((1, 21), (1, 31)), Catalogue((1, 20)));
        Assert.Equal("season 1 runs to E31 on disk but the catalogue stops at E20", mismatch);
    }

    [Fact]
    public void SeasonShape_passes_when_the_files_fit_inside_the_catalogued_seasons()
    {
        // The ordinary good case: gap-fill files that land inside the existing list. Mapping by
        // (season, episode) is exactly right here and must not be blocked.
        Assert.Null(SyncSeriesMatcher.SeasonShapeMismatch(
            Files((5, 6), (5, 26), (1, 1)), Catalogue((1, 16), (5, 26))));
    }

    [Fact]
    public void AbsolutePairing_realigns_the_shifted_boundary()
    {
        var files = Files(Enumerable.Range(1, 43).Select(e => (1, e))
            .Concat(Enumerable.Range(1, 41).Select(e => (2, e))).ToArray());
        var catalogue = Catalogue((1, 42), (2, 42));

        var pairs = SyncSeriesMatcher.AbsolutePairing(files, catalogue);
        Assert.NotNull(pairs);
        Assert.Equal(84, pairs!.Count);

        // File S01E01 → catalogued S1E1 (both absolute #1).
        var first = files.Single(f => f.SeasonNumber == 1 && f.EpisodeNumber == 1);
        Assert.Equal((1, 1), (pairs[first.Id].SeasonNumber, pairs[first.Id].EpisodeNumber));
        // File S01E43 (absolute #43) → catalogued S2E1, which is the catalogue's 43rd. This is the
        // pairing the naive by-number map gets wrong, and it is the whole point of the override.
        var boundary = files.Single(f => f.SeasonNumber == 1 && f.EpisodeNumber == 43);
        Assert.Equal((2, 1), (pairs[boundary.Id].SeasonNumber, pairs[boundary.Id].EpisodeNumber));
        // File S02E01 (absolute #44) → catalogued S2E2, NOT S2E1.
        var s2e1 = files.Single(f => f.SeasonNumber == 2 && f.EpisodeNumber == 1);
        Assert.Equal((2, 2), (pairs[s2e1.Id].SeasonNumber, pairs[s2e1.Id].EpisodeNumber));
        // Last file → last catalogued episode.
        var last = files.Single(f => f.SeasonNumber == 2 && f.EpisodeNumber == 41);
        Assert.Equal((2, 42), (pairs[last.Id].SeasonNumber, pairs[last.Id].EpisodeNumber));
    }

    [Fact]
    public void AbsolutePairing_refuses_anything_but_an_exact_one_to_one()
    {
        // One file short: every pair after the gap would be wrong, so there is no safe answer.
        Assert.Null(SyncSeriesMatcher.AbsolutePairing(Files((1, 1), (1, 2)), Catalogue((1, 3))));
        Assert.Null(SyncSeriesMatcher.AbsolutePairing(Files((1, 1), (1, 2), (1, 3)), Catalogue((1, 2))));
        Assert.Null(SyncSeriesMatcher.AbsolutePairing(new List<SyncCandidate>(), Catalogue((1, 3))));
        Assert.NotNull(SyncSeriesMatcher.AbsolutePairing(Files((1, 1), (1, 2)), Catalogue((1, 2))));
    }

    [Theory]
    [InlineData("Series", true)]
    [InlineData("Misc", true)]
    [InlineData("2 - Video", true)]
    [InlineData("1 - Movies", true)]
    [InlineData("B", true)]
    [InlineData("S", true)]
    [InlineData("Nick Arcade (1992)", false)]
    [InlineData("Voyager", false)]
    [InlineData("Stage Performances", false)]
    public void IsContainerRoot_knows_the_library_skeleton(string leaf, bool expected) =>
        Assert.Equal(expected, MovieFolderParser.IsContainerRoot(leaf));
}
