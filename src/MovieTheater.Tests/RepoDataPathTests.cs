using System;
using System.IO;
using MovieTheater.Arcade;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The arcade CLIs default their data-file options to repo-root-relative paths
    /// (<c>data/arcade/fbneo-arcade.dat</c>). Running via <c>dotnet run --project src/MovieTheater/…</c>
    /// sets the working directory to the PROJECT dir, so those defaults used to miss — and
    /// <c>arcade-romcache-export</c> answered a missed DAT by publishing a manifest with every FBNeo
    /// dependency closure silently gone. These pin the upward search that fixes it.
    /// <para>Tests drive the search-roots overload rather than <c>Directory.SetCurrentDirectory</c>: the
    /// CWD is process-wide, and mutating it would race every other test class xunit runs in parallel.</para>
    /// </summary>
    public class RepoDataPathTests
    {
        /// <summary>The trap itself: the file sits at the "repo root" but we run from a nested dir.</summary>
        [Fact]
        public void Resolve_FindsFileInAnAncestorDirectory()
        {
            using var repo = new TempDir();
            var dataFile = repo.WriteFile("data/arcade/fbneo-arcade.dat", "x");
            var nested = repo.MakeDir("src/MovieTheater");

            var resolved = RepoDataPath.Resolve("data/arcade/fbneo-arcade.dat", nested);

            Assert.True(Path.IsPathRooted(resolved));
            Assert.True(File.Exists(resolved));
            Assert.Equal(dataFile, Path.GetFullPath(resolved));
        }

        /// <summary>A root is probed itself before its ancestors, so a local copy still wins.</summary>
        [Fact]
        public void Resolve_PrefersAFileInTheSearchRootItself()
        {
            using var repo = new TempDir();
            repo.WriteFile("data/f.dat", "root");
            var nestedCopy = repo.WriteFile("src/data/f.dat", "nested");

            Assert.Equal(nestedCopy, Path.GetFullPath(RepoDataPath.Resolve("data/f.dat", repo.MakeDir("src"))));
        }

        /// <summary>Earlier search roots win, so the working directory beats the binary's location.</summary>
        [Fact]
        public void Resolve_TriesSearchRootsInOrder()
        {
            using var first = new TempDir();
            using var second = new TempDir();
            var inFirst = first.WriteFile("data/f.dat", "first");
            second.WriteFile("data/f.dat", "second");

            Assert.Equal(inFirst, Path.GetFullPath(RepoDataPath.Resolve("data/f.dat", first.Path, second.Path)));
        }

        /// <summary>...but a later root is still reached when the earlier one has nothing.</summary>
        [Fact]
        public void Resolve_FallsThroughToALaterSearchRoot()
        {
            using var empty = new TempDir();
            using var holder = new TempDir();
            var target = holder.WriteFile("data/f.dat", "here");

            Assert.Equal(target, Path.GetFullPath(RepoDataPath.Resolve("data/f.dat", empty.Path, holder.Path)));
        }

        /// <summary>An explicit --dat must never be silently redirected to some other file up the tree.</summary>
        [Fact]
        public void Resolve_ReturnsARootedPathUntouched()
        {
            using var repo = new TempDir();
            repo.WriteFile("data/f.dat", "decoy");
            var rooted = Path.Combine(Path.GetTempPath(), "definitely-not-here.dat");

            Assert.Equal(rooted, RepoDataPath.Resolve(rooted, repo.Path));
        }

        /// <summary>Unresolvable input comes back verbatim so the caller's own error names what the user
        /// typed, rather than some absolutised path they never mentioned.</summary>
        [Fact]
        public void Resolve_ReturnsInputUnchangedWhenNothingMatches()
        {
            using var repo = new TempDir();
            const string missing = "data/arcade/no-such-file-9f3a.dat";

            Assert.Equal(missing, RepoDataPath.Resolve(missing, repo.Path));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Resolve_PassesThroughBlankInput(string input) =>
            Assert.Equal(input, RepoDataPath.Resolve(input, Path.GetTempPath()));

        private sealed class TempDir : IDisposable
        {
            public string Path { get; } = Directory.CreateDirectory(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mt-repodata-" + Guid.NewGuid().ToString("N"))).FullName;

            /// <summary>Creates <paramref name="relative"/> under this dir; returns its full path.</summary>
            public string WriteFile(string relative, string content)
            {
                var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relative));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
                return full;
            }

            public string MakeDir(string relative) =>
                Directory.CreateDirectory(System.IO.Path.Combine(Path, relative)).FullName;

            public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
        }
    }
}
