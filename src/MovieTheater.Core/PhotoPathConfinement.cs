using System.IO;

namespace MovieTheater.Core
{
    /// <summary>
    /// The photo data plane's security boundary (photos-plan.md §2.2): a capability is signed, but a
    /// signed capability must still not be able to name a file outside the root its route serves.
    ///
    /// <para>It lives in Core, beside the token, for the same reason the token does — it is the half of
    /// the gateway's photo handling that has to be RIGHT, and a rule that only exists inside a
    /// top-level <c>Program.cs</c> is a rule no test can reach. The gateway calls exactly this.</para>
    /// </summary>
    public static class PhotoPathConfinement
    {
        /// <summary>
        /// Resolves a token's root-relative path against <paramref name="rootFullPath"/>, or returns
        /// null if it escapes. Rooted and drive-qualified inputs are refused BEFORE the combine, because
        /// <see cref="Path.Combine(string, string)"/> discards its first argument when the second is
        /// absolute — the check has to happen while there is still something to check.
        /// </summary>
        public static string? Resolve(string rootFullPath, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;

            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(native)) return null;

            var full = Path.GetFullPath(Path.Combine(rootFullPath, native));
            var boundary = rootFullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(boundary, System.StringComparison.OrdinalIgnoreCase) ? full : null;
        }
    }
}
