using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace MovieTheater.ArcadeGateway;

/// <summary>
/// Per-site-user saves for heavy-lane (Moonlight-streamed) titles — docs/arcade-heavy-lane-plan.md §8.
/// Emulator processes all run as the one logged-in desktop user, so per-user state can't come from
/// Windows profiles; it comes from this vault seeding the emulator's save directory per launch and
/// harvesting it back after — the same philosophy as the CloudRetro seed/harvest, with one new
/// artifact kind: <b>directory saves</b>, stored as zips (<c>Kind='dirzip'</c>, slot 0, one
/// "Continue" per (user, title)).
///
/// <para><b>Deterministic zips:</b> entries are sorted ordinally and carry a fixed timestamp, so the
/// zip's SHA-256 IS a content hash — "did the save change?" is a byte compare, immune to zip
/// metadata noise.</para>
///
/// <para><b>Never-clobber:</b> seeding moves the current live content aside to
/// <c>&lt;store&gt;\_displaced\&lt;appId&gt;\&lt;timestamp&gt;\</c> before unzipping — never deletes.
/// Whatever was there (another user's un-vaulted progress, a pre-vault local save) stays
/// recoverable by hand.</para>
///
/// <para><b>Blob layout matches <see cref="SaveStore"/></b> (<c>&lt;store&gt;\&lt;userId&gt;\&lt;gameId&gt;\dirzip.zip</c>
/// + sidecar), so the site's existing My-Saves blob ops (read/delete/import) serve heavy saves with
/// no new plumbing.</para>
/// </summary>
public sealed class HeavyVault
{
    public const string Kind = "dirzip";

    // Fixed entry timestamp (zip format's minimum) — what makes the zip content-addressed.
    private static readonly DateTimeOffset ZipEpoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string storeRoot;
    private readonly ILogger log;

    public HeavyVault(string storeRoot, ILogger log)
    {
        this.storeRoot = Path.GetFullPath(storeRoot);
        this.log = log;
    }

    /// <summary>
    /// The live directories a descriptor's <c>save.livePath</c> names. The LAST segment may be a
    /// glob (RPCS3: <c>...\savedata\NPUA80247*</c> — one title spreads over several dirs); a plain
    /// path matches its one directory. Missing dirs resolve to empty (a title never played yet).
    /// </summary>
    public static List<string> ResolveLiveDirs(string livePath)
    {
        var parent = Path.GetDirectoryName(livePath);
        var leaf = Path.GetFileName(livePath);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return new List<string>();
        if (!Directory.Exists(parent)) return new List<string>();
        if (leaf.Contains('*') || leaf.Contains('?'))
            return Directory.EnumerateDirectories(parent, leaf).OrderBy(d => d, StringComparer.Ordinal).ToList();
        var one = Path.Combine(parent, leaf);
        return Directory.Exists(one) ? new List<string> { one } : new List<string>();
    }

    /// <summary>Deterministic zip of everything under the live dirs (entry names
    /// <c>&lt;dirName&gt;/&lt;relative&gt;</c> so a multi-dir title restores exactly), or null when
    /// nothing exists to save.</summary>
    public static byte[]? ZipLiveDirs(string livePath)
    {
        var dirs = ResolveLiveDirs(livePath);
        if (dirs.Count == 0) return null;

        // Collect (entryName, filePath) sorted ordinally — determinism requirement #1.
        var entries = new List<(string Name, string File)>();
        foreach (var dir in dirs)
        {
            var dirName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                entries.Add(($"{dirName}/{Path.GetRelativePath(dir, f).Replace('\\', '/')}", f));
        }
        if (entries.Count == 0) return null;
        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, file) in entries)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                e.LastWriteTime = ZipEpoch; // determinism requirement #2
                using var src = File.OpenRead(file);
                using var dst = e.Open();
                src.CopyTo(dst);
            }
        }
        return ms.ToArray();
    }

    private string BlobPath(int userId, int gameId) =>
        Path.GetFullPath(Path.Combine(storeRoot, userId.ToString(), gameId.ToString(), "dirzip.zip"));

    /// <summary>Current vaulted zip's sha (lowercase hex), or null when the user has no entry.</summary>
    public string? VaultedSha(int userId, int gameId)
    {
        var blob = BlobPath(userId, gameId);
        if (!File.Exists(blob)) return null;
        using var f = File.OpenRead(blob);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    /// <summary>
    /// Zip the live save and store it for the user IF it changed. Returns the metadata to mirror
    /// into the app DB, or null (no live content / unchanged).
    /// </summary>
    public SaveMeta? Harvest(HeavyApp app, int userId)
    {
        if (app.ArcadeGameId is not int gameId || string.IsNullOrEmpty(app.Save?.LivePath)) return null;
        var bytes = ZipLiveDirs(app.Save.LivePath!);
        if (bytes == null) return null;

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (sha == VaultedSha(userId, gameId)) return null; // unchanged since last vault

        var blob = BlobPath(userId, gameId);
        if (!IsUnder(storeRoot, blob)) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        File.WriteAllBytes(blob, bytes);

        var meta = new SaveMeta(userId, gameId, app.System ?? "", Kind, 0, null,
            Path.GetFileNameWithoutExtension(app.Exe), null,
            Path.GetRelativePath(storeRoot, blob).Replace('\\', '/'),
            bytes.LongLength, sha, "heavy", false, DateTime.UtcNow, DateTime.UtcNow);
        File.WriteAllText(blob + ".json", System.Text.Json.JsonSerializer.Serialize(meta));
        log.LogInformation("Heavy save harvested: user {User} app {App} ({Bytes} bytes)", userId, app.Id, bytes.LongLength);
        return meta;
    }

    /// <summary>
    /// Restore the user's vaulted save into the live path. No vault entry = leave what's live (v1
    /// keeps continuity with pre-vault local saves — plan §8). Current live content is moved aside
    /// (never deleted) unless it already matches the vault byte-for-byte.
    /// </summary>
    public bool Seed(HeavyApp app, int userId)
    {
        if (app.ArcadeGameId is not int gameId || string.IsNullOrEmpty(app.Save?.LivePath)) return false;
        var blob = BlobPath(userId, gameId);
        if (!File.Exists(blob)) return false;

        var livePath = app.Save.LivePath!;
        var parent = Path.GetDirectoryName(livePath)!;

        // Already identical (same user relaunching)? Skip the displace/unzip churn.
        var liveBytes = ZipLiveDirs(livePath);
        if (liveBytes != null)
        {
            var liveSha = Convert.ToHexString(SHA256.HashData(liveBytes)).ToLowerInvariant();
            if (liveSha == VaultedSha(userId, gameId)) return true;
        }

        // Displace whatever is live — to the STORE side, not the emulator tree (an emulator that
        // scans its save root must never see our graveyard).
        var liveDirs = ResolveLiveDirs(livePath);
        if (liveDirs.Count > 0)
        {
            var displaced = Path.Combine(storeRoot, "_displaced", Sanitize(app.Id), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(displaced);
            foreach (var dir in liveDirs)
            {
                var dest = Path.Combine(displaced, Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)));
                try { Directory.Move(dir, dest); }
                catch (IOException)
                {
                    // Cross-volume move (store on D:, emulator on C:/E:): copy + delete-after-copy.
                    CopyTree(dir, dest);
                    Directory.Delete(dir, recursive: true);
                }
            }
            log.LogInformation("Heavy seed displaced {Count} live dir(s) for {App} → {Dest}", liveDirs.Count, app.Id, displaced);
        }

        // Unzip the vault entry under the live parent, with a zip-slip guard.
        Directory.CreateDirectory(parent);
        using (var zip = ZipFile.OpenRead(blob))
        {
            foreach (var e in zip.Entries)
            {
                if (string.IsNullOrEmpty(e.Name)) continue; // directory marker
                var dest = Path.GetFullPath(Path.Combine(parent, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsUnder(parent, dest)) throw new InvalidOperationException($"zip entry escapes the save dir: {e.FullName}");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);
            }
        }
        log.LogInformation("Heavy save seeded: user {User} app {App}", userId, app.Id);
        return true;
    }

    private static void CopyTree(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
        }
    }

    private static string Sanitize(string id)
    {
        var s = id;
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private static bool IsUnder(string root, string fullPath)
    {
        var r = Path.GetFullPath(root + Path.DirectorySeparatorChar);
        return Path.GetFullPath(fullPath).StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}
