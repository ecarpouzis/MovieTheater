using System;
using System.IO;
using System.Linq;
using MovieTheater.Music;
using Xunit;

namespace MovieTheater.Tests;

/// <summary>
/// Where the raw external-metadata cache lands when nobody names a root.
/// </summary>
/// <remarks>
/// This looks like plumbing and is not. The cache exists so that every future change to what we
/// EXTRACT from Last.fm / MusicBrainz answers is an offline re-parse rather than another hour of
/// somebody else's server. A cache that silently relocates spends that hour anyway, and reports
/// success while doing it — which is exactly what happened on 2026-08-30, when three roots held
/// 5 / 1,567 / 75 bodies and a re-parse of 995 banked answers went back to the network for all of
/// them.
/// </remarks>
public class MusicResponseCacheRootTests : IDisposable
{
    private readonly string tmp = Path.Combine(Path.GetTempPath(), "mt-cache-root-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(tmp, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Make(params string[] segments)
    {
        var p = Path.Combine(new[] { tmp }.Concat(segments).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    [Fact]
    public void The_root_is_the_repo_that_contains_dot_git()
    {
        var repo = Make("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var deep = Make("repo", "src", "App", "bin", "Release", "net10.0");

        Assert.Equal(Path.Combine(repo, "data", "music-cache"),
            MusicResponseCache.ResolveDefaultRoot(deep));
    }

    [Fact]
    public void A_stray_data_directory_beside_the_binary_no_longer_shadows_the_real_cache()
    {
        // THE REGRESSION, in miniature. The old resolver took the first data/ at-or-above the start
        // directory, so the one the cache itself had minted next to the binary won permanently and
        // every later run silently re-fetched.
        var repo = Make("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Make("repo", "data", "music-cache");
        var bin = Make("repo", "src", "App", "bin", "Release", "net10.0");
        Make("repo", "src", "App", "bin", "Release", "net10.0", "data", "music-cache");
        Make("repo", "src", "App", "data", "music-cache");

        Assert.Equal(Path.Combine(repo, "data", "music-cache"),
            MusicResponseCache.ResolveDefaultRoot(bin));
    }

    [Fact]
    public void A_worktree_whose_dot_git_is_a_FILE_is_still_a_repo()
    {
        var repo = Make("wt");
        File.WriteAllText(Path.Combine(repo, ".git"), "gitdir: /elsewhere/.git/worktrees/wt");
        var deep = Make("wt", "src", "App");

        Assert.Equal(Path.Combine(repo, "data", "music-cache"),
            MusicResponseCache.ResolveDefaultRoot(deep));
    }

    [Fact]
    public void An_explicit_root_always_wins()
    {
        var chosen = Make("explicit");
        Assert.Equal(Path.GetFullPath(chosen), Path.GetFullPath(new MusicResponseCache(chosen).Root));
    }

    [Fact]
    public void With_no_repo_marker_at_all_it_still_returns_a_usable_path()
    {
        // A published deployment has no .git. Falling back is fine; returning null or throwing is not.
        var orphan = Make("no-repo", "nested");
        var root = MusicResponseCache.ResolveDefaultRoot(orphan);

        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.EndsWith("music-cache", root);
    }
}
