using System;
using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Streaming
{
    /// <summary>
    /// Copies Jellyfin's keyframe lists into <see cref="MediaKeyframes"/>, keyed by content
    /// fingerprint — the banking half of keyframe custody. Jellyfin's own <c>KeyframeData</c> rows
    /// cascade-delete with their items, so a folder rename physically destroys the lists for
    /// everything under it; once banked here, the sync's restore pass
    /// (<c>RestoreBankedKeyframesAsync</c>) can hand the same bytes their list back on whatever item
    /// id they reappear under.
    ///
    /// <para><b>Runs on the Jellyfin host only</b> — it reads <c>jellyfin.db</c> directly, read-only
    /// (<c>Mode=ReadOnly</c>; the service stays up). The queue is our own rows: stamped, fingerprinted
    /// files whose fingerprint has no banked list yet, so re-runs are no-ops and the job is resumable
    /// by construction (global bulk-job rule). Run <c>fingerprint-media-files</c> first — a row
    /// without a fingerprint has no key to bank under, and is counted here so the gap is visible.</para>
    ///
    /// <para>Also surfaces <b>lying stamps</b>: rows whose <c>JfKeyframesUtc</c> claims a server-side
    /// list that is not actually in Jellyfin's table (the cascade already ate it). Those files play on
    /// legacy segmentation while claiming exactness — the silent regression this whole lane exists to
    /// make loud. They are reported for <c>extract-jellyfin-keyframes --force</c>, never auto-cleared.</para>
    /// </summary>
    [Command("bank-jellyfin-keyframes", Description = "Copy Jellyfin's keyframe lists into MediaKeyframes (content-keyed, rename-proof).")]
    public class BankJellyfinKeyframesCommand : BasicDICommand, ICommand
    {
        [CommandOption("limit", Description = "Max lists to bank this run (default 2000 — each is one local read + one row write).")]
        public int Limit { get; set; } = 2000;

        [CommandOption("sqlite-path", Description = "Jellyfin's database file.")]
        public string SqlitePath { get; set; } = @"C:\ProgramData\Jellyfin\Server\data\jellyfin.db";

        [CommandOption("dry-run", Description = "Report what would be banked and write nothing.")]
        public bool DryRun { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public BankJellyfinKeyframesCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var cancel = console.RegisterCancellationHandler();
            var w = console.Output;

            if (!System.IO.File.Exists(SqlitePath))
            {
                w.WriteLine($"Jellyfin database not found at {SqlitePath} — this command runs on the Jellyfin host.");
                return;
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancel);

            // Rows that SHARE a Jellyfin item with another row (version-collapsed alternate cuts,
            // multi-part titles) are excluded outright: the item's one stored list describes the
            // PRIMARY source's bytes, and banking it under the other file's fingerprint would file a
            // wrong list under a true key — the silent-wrong-list case the size check exists to catch,
            // except here the sizes agree because both rows were stamped from the same item. Measured
            // 2026-08-14: exactly 100 such rows (18,888 stamps over 18,788 distinct items), zero
            // orphans — the count difference that first LOOKED like cascade damage.
            var sharedItems = db.MediaFiles
                .Where(f => f.JellyfinItemId != null)
                .GroupBy(f => f.JellyfinItemId)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);

            // The queue: stamped + fingerprinted rows whose fingerprint is not banked yet. Left-join
            // spelled as a subquery so the whole predicate runs DB-side.
            var banked = db.MediaKeyframes.Select(k => k.Fingerprint);
            var batch = await db.MediaFiles
                .Where(f => f.MissingSinceUtc == null && f.JellyfinItemId != null
                            && f.JfKeyframesUtc != null && f.ContentFingerprint != null
                            && !banked.Contains(f.ContentFingerprint)
                            && !sharedItems.Contains(f.JellyfinItemId))
                .OrderByDescending(f => f.Id)
                .Take(Math.Max(1, Limit))
                .ToListAsync(cancel);

            var unfingerprinted = await db.MediaFiles.CountAsync(
                f => f.MissingSinceUtc == null && f.JfKeyframesUtc != null && f.ContentFingerprint == null, cancel);

            if (batch.Count == 0)
            {
                w.WriteLine($"Nothing to bank. (Stamped rows still lacking a fingerprint: {unfingerprinted} — run fingerprint-media-files.)");
                return;
            }

            await using var sqlite = new SqliteConnection($"Data Source={SqlitePath};Mode=ReadOnly");
            await sqlite.OpenAsync(cancel);

            int bankedNow = 0, orphanStamps = 0, noSize = 0;
            var now = DateTime.UtcNow;
            foreach (var file in batch)
            {
                cancel.ThrowIfCancellationRequested();

                if (file.SizeBytes == null)
                {
                    // No size on the row means no sanity check at restore time — refuse to bank a list
                    // we could never verify against the bytes claiming it.
                    noSize++;
                    w.WriteLine($"  ! {file.Id} has no SizeBytes — not banked: {file.Path}");
                    continue;
                }

                using var cmd = sqlite.CreateCommand();
                cmd.CommandText = "SELECT TotalDuration, KeyframeTicks FROM KeyframeData WHERE ItemId = $id";
                cmd.Parameters.AddWithValue("$id", Services.Jellyfin.JellyfinItemIds.DashedUpper(file.JellyfinItemId!));
                using var reader = await cmd.ExecuteReaderAsync(cancel);
                if (!await reader.ReadAsync(cancel) || reader.IsDBNull(1))
                {
                    // The stamp says Jellyfin holds a list; Jellyfin does not. The cascade (or a wipe)
                    // already ate it — this file silently plays on legacy segmentation.
                    orphanStamps++;
                    w.WriteLine($"  ! {file.Id} STAMPED but no server list (item {file.JellyfinItemId}) — " +
                                $"run extract-jellyfin-keyframes --force --playable-id {file.PlayableId}: {file.Path}");
                    continue;
                }

                if (!DryRun)
                {
                    db.MediaKeyframes.Add(new MediaKeyframes
                    {
                        Fingerprint = file.ContentFingerprint!,
                        TotalDurationTicks = reader.GetInt64(0),
                        KeyframeTicks = reader.GetString(1),
                        SizeBytes = file.SizeBytes.Value,
                        SourceItemId = file.JellyfinItemId,
                        CapturedUtc = now,
                    });
                    // Saved per row: the unique key makes a replayed run skip everything already banked,
                    // and a Ctrl-C mid-run keeps every list already copied.
                    await db.SaveChangesAsync(cancel);
                }
                bankedNow++;
            }

            var remaining = await db.MediaFiles.CountAsync(
                f => f.MissingSinceUtc == null && f.JellyfinItemId != null
                     && f.JfKeyframesUtc != null && f.ContentFingerprint != null
                     && !banked.Contains(f.ContentFingerprint)
                     && !sharedItems.Contains(f.JellyfinItemId), cancel);
            var sharedSkipped = await db.MediaFiles.CountAsync(
                f => f.MissingSinceUtc == null && f.JfKeyframesUtc != null
                     && f.JellyfinItemId != null && sharedItems.Contains(f.JellyfinItemId), cancel);
            w.WriteLine("");
            w.WriteLine($"{{ {(DryRun ? "wouldBank" : "banked")}: {bankedNow}, orphanStamps: {orphanStamps}, noSize: {noSize}, " +
                        $"sharedItemRowsExcluded: {sharedSkipped}, unfingerprinted: {unfingerprinted}, remaining: {remaining} }}");
        }

    }
}
