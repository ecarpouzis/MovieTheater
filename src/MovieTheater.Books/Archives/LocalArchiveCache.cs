using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// A bounded local-disk LRU of WHOLE archive files copied off the library share, so a reading session
    /// extracts pages from local disk instead of re-opening a multi-hundred-MB file over SMB for every cold page.
    /// It complements <see cref="PageByteCache"/>: the byte cache makes a page free the SECOND time it is needed
    /// within a session; this makes every COLD page of a recently-read book fast, and survives a restart.
    ///
    /// <para>Rules that make it safe:</para>
    /// <list type="bullet">
    /// <item>Only UNC sources are cached — a local library root gains nothing from a copy.</item>
    /// <item><see cref="Resolve"/> NEVER blocks on the share: the local copy if it exists, otherwise the original
    /// path, with the copy started in the background only when <c>warm</c> says a reading session began.</item>
    /// <item>Cache files are named <c>{sha1(path)}_{mtimeTicks}{ext}</c>, so a replaced or re-scanned source
    /// naturally misses and the stale copy ages out (plus an eager sibling sweep after each copy).</item>
    /// <item>LRU is the cache file's own LastWriteTimeUtc, bumped on access (throttled) so it survives restarts;
    /// eviction deletes oldest-first until the budget holds. A file currently open in a reader fails to delete on
    /// Windows — those are skipped and retried on a later pass.</item>
    /// </list>
    ///
    /// <para><b>This class writes ONLY inside its own cache directory.</b> The source is opened read-only; the
    /// library share is never written, renamed or deleted.</para>
    /// </summary>
    public sealed class LocalArchiveCache
    {
        private readonly string? dir;   // null = disabled (not configured, or the directory is unusable)
        private readonly long limitBytes;
        private readonly long maxFileBytes;
        private readonly ILogger<LocalArchiveCache> logger;

        // Local cache paths with a copy in progress — dedupes concurrent warms of the same book.
        private readonly ConcurrentDictionary<string, byte> inFlight = new(StringComparer.OrdinalIgnoreCase);
        // Last LRU-stamp bump per cache file, so a page-per-second session does not write file metadata per request.
        private readonly ConcurrentDictionary<string, DateTime> lastTouch = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan TouchThrottle = TimeSpan.FromMinutes(15);
        private int evicting;

        public LocalArchiveCache(BooksOptions options, ILogger<LocalArchiveCache> logger)
        {
            this.logger = logger;
            limitBytes = Math.Max(0, options.ArchiveCacheGb) * 1024L * 1024L * 1024L;
            // One book may not monopolize the cache; anything bigger is served from the share as before.
            maxFileBytes = limitBytes / 4;
            if (options.ArchiveCacheGb <= 0) return;

            var configured = string.IsNullOrWhiteSpace(options.ArchiveCacheDir)
                ? (string.IsNullOrWhiteSpace(options.CacheDir) ? null : Path.Combine(options.CacheDir, "archives"))
                : options.ArchiveCacheDir;
            if (configured == null) return;

            try
            {
                Directory.CreateDirectory(configured);
                // Orphaned partial copies from a previous run (a crash or shutdown mid-copy).
                foreach (var tmp in Directory.EnumerateFiles(configured, "*.tmp"))
                    try { File.Delete(tmp); } catch { /* best effort */ }
                dir = Path.GetFullPath(configured);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Local archive cache disabled — its directory could not be used.");
            }
        }

        public bool Enabled => dir != null;

        /// <summary>
        /// The path the caller should open: the local copy when present, otherwise <paramref name="filePath"/>
        /// unchanged. Pass <c>warm: false</c> for requests that do NOT signal a reading session (covers,
        /// thumbnails) so browsing a grid of a hundred books does not enqueue a hundred copies.
        /// </summary>
        public string Resolve(string filePath, long modifiedTicks, bool warm)
        {
            if (dir is null || !filePath.StartsWith(@"\\", StringComparison.Ordinal)) return filePath;

            var local = LocalPath(filePath, modifiedTicks);
            if (File.Exists(local))
            {
                Touch(local);
                return local;
            }
            if (warm) BeginCopy(filePath, local);
            return filePath;
        }

        private string LocalPath(string filePath, long modifiedTicks)
        {
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(filePath.ToLowerInvariant())));
            return Path.Combine(dir!, $"{hash}_{modifiedTicks}{Path.GetExtension(filePath)}");
        }

        private void Touch(string local)
        {
            var now = DateTime.UtcNow;
            var last = lastTouch.GetOrAdd(local, DateTime.MinValue);
            if (now - last < TouchThrottle) return;
            lastTouch[local] = now;
            try { File.SetLastWriteTimeUtc(local, now); } catch { /* in use / racing eviction — fine */ }
        }

        private void BeginCopy(string source, string local)
        {
            if (!inFlight.TryAdd(local, 0)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var info = new FileInfo(source);
                    if (!info.Exists || info.Length > maxFileBytes) return;

                    var tmp = local + ".tmp";
                    var sw = Stopwatch.StartNew();
                    await using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
                    await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                        await src.CopyToAsync(dst);
                    File.Move(tmp, local, overwrite: true);
                    logger.LogInformation("Cached archive locally ({Mb:F0} MB in {Sec:F1}s).",
                        info.Length / 1048576.0, sw.Elapsed.TotalSeconds);

                    // Stale siblings = same source path, older mtime (the file was replaced upstream).
                    var prefix = Path.GetFileName(local).Split('_')[0];
                    foreach (var sibling in Directory.EnumerateFiles(dir!, prefix + "_*"))
                        if (!sibling.Equals(local, StringComparison.OrdinalIgnoreCase)
                            && !sibling.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                            try { File.Delete(sibling); } catch { /* in use — LRU gets it later */ }

                    EvictOverBudget();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Local archive cache copy failed.");
                    try { File.Delete(local + ".tmp"); } catch { /* best effort */ }
                }
                finally
                {
                    inFlight.TryRemove(local, out _);
                }
            });
        }

        private void EvictOverBudget()
        {
            if (dir is null || Interlocked.Exchange(ref evicting, 1) == 1) return;
            try
            {
                var files = new DirectoryInfo(dir).EnumerateFiles()
                    .Where(f => !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.LastWriteTimeUtc)   // oldest access first (Touch bumps this)
                    .ToList();
                var total = files.Sum(f => f.Length);
                foreach (var f in files)
                {
                    if (total <= limitBytes) break;
                    if (inFlight.ContainsKey(f.FullName)) continue;
                    try
                    {
                        var size = f.Length;
                        f.Delete();
                        total -= size;
                        lastTouch.TryRemove(f.FullName, out _);
                    }
                    catch
                    {
                        // Open in a reader right now — skip it; a later pass retires it.
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Local archive cache eviction pass failed.");
            }
            finally
            {
                Interlocked.Exchange(ref evicting, 0);
            }
        }
    }
}
