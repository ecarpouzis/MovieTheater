using System;
using System.IO;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Resolves the arcade CLIs' <c>data/...</c> default file paths independently of the working directory.
    /// <para>
    /// Those defaults (<c>data/arcade/fbneo-arcade.dat</c>, <c>data/launchbox/Metadata.zip</c>, …) name
    /// files committed at the REPO ROOT, but they were plain relative paths — so they only resolved when the
    /// command happened to run from the repo root. <c>dotnet run --project src/MovieTheater/…</c> sets the
    /// working directory to the PROJECT dir, so every one of them probed <c>src/MovieTheater/data/…</c> and
    /// missed. That is not a cosmetic miss: <c>arcade-romcache-export</c> treated a missing DAT as a warning
    /// and published a manifest with ZERO FBNeo dependency closures, silently breaking every arcade game's
    /// parent/BIOS staging. It recurred three times (2026-07-14, -07-25, -07-31) because the failure reads
    /// exactly like success.
    /// </para>
    /// <para>
    /// So: probe upward for the file instead of demanding a particular working directory. An absolute path
    /// is always honoured as given (an explicit <c>--dat</c> must never be silently redirected), and an
    /// unresolvable relative path is returned unchanged so the caller's own error names what the user typed.
    /// </para>
    /// </summary>
    public static class RepoDataPath
    {
        /// <summary>Resolve <paramref name="path"/> to an existing file/directory, searching the working
        /// directory and the binary's location and then each of their ancestors. Returns the input unchanged
        /// when it is rooted or cannot be found.</summary>
        public static string Resolve(string path) =>
            // The working dir covers `dotnet MovieTheater.dll` from anywhere in the tree; the binary's own
            // location covers a published/bin-run copy whose CWD is somewhere else entirely.
            Resolve(path, Directory.GetCurrentDirectory(), AppContext.BaseDirectory);

        /// <summary>Search-root-explicit overload. Exists so tests can exercise the upward walk without
        /// mutating the process-wide working directory (global state xunit's parallel classes would race).
        /// Each root is probed itself before its ancestors, so passing the CWD covers "already relative to
        /// where I am".</summary>
        internal static string Resolve(string path, params string[] searchRoots)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return path;

            foreach (var start in searchRoots)
            {
                for (var dir = SafeDirectory(start); dir != null; dir = dir.Parent)
                {
                    var candidate = Path.Combine(dir.FullName, path);
                    if (Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }
            return path;
        }

        private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

        private static DirectoryInfo? SafeDirectory(string path)
        {
            try { return string.IsNullOrWhiteSpace(path) ? null : new DirectoryInfo(path); }
            catch { return null; }
        }
    }
}
