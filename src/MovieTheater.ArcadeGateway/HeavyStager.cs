using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MovieTheater.ArcadeGateway;

/// <summary>
/// The heavy tier of ROM staging (docs/arcade-heavy-lane-plan.md §5). PSX-era JIT extracts ~500 MB at
/// first play; Switch/PS3-class titles are 5–45 GB, so the heavy tier is PRE-staged: the card shows
/// "Prepare (N GB)" and this stager copies the source from the library drive into the local cache in
/// bounded chunks, one chunk per call, with the CALLER driving to completion (bulk-job house rules:
/// bounded per call, progress every chunk, resumable+idempotent, deterministic stop).
///
/// <para><b>Integrity without a source hash:</b> each copy chunk folds its SHA-256 into a persisted
/// hash CHAIN (chain = SHA256(prevChainHex + chunkHash)) — serializable, so the copy survives gateway
/// restarts mid-title. After the copy a bounded VERIFY pass re-reads the target computing the same
/// chain; a mismatch (torn write, disk error) resets to a clean re-copy. Only then does
/// <c>.partial</c> lose its suffix and the app count as staged.</para>
///
/// <para><b>Never destructive:</b> the stager writes only under its own cache root, refuses (with a
/// clear error) rather than evicts when the cap is hit, and never touches the source. The library
/// drive is read-only to automation, always.</para>
/// </summary>
public sealed class HeavyStager
{
    public sealed class StageState
    {
        public string AppId { get; set; } = default!;
        public string Source { get; set; } = default!;
        public string Target { get; set; } = default!;      // final path (no .partial suffix)
        /// <summary>copy | verify | done | error</summary>
        public string Phase { get; set; } = "copy";
        /// <summary>Chunk size PINNED at stage start: verify must re-read on the same boundaries as
        /// the copy or the hash chains diverge even on a perfect copy (a config change mid-title
        /// would otherwise read as corruption and trigger a pointless 45 GB re-copy).</summary>
        public long ChunkBytes { get; set; }
        public long TotalBytes { get; set; }
        public long StagedBytes { get; set; }
        public long VerifiedBytes { get; set; }
        public string ChainHash { get; set; } = "";          // copy-side chain
        public string VerifyChainHash { get; set; } = "";    // verify-side chain (must converge)
        public string? Error { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string cacheRoot;
    private readonly long maxBytes;
    private readonly long chunkBytes;
    private readonly ILogger log;
    private readonly object gate = new();                 // guards the states dict + its save
    private readonly SemaphoreSlim advanceSem = new(1, 1); // serializes chunk I/O without blocking reads
    private readonly string stateFile;
    private Dictionary<string, StageState> states = new(StringComparer.OrdinalIgnoreCase);

    public HeavyStager(string cacheRoot, long maxBytes, long chunkBytes, ILogger log)
    {
        this.cacheRoot = Path.GetFullPath(cacheRoot);
        this.maxBytes = maxBytes;
        this.chunkBytes = Math.Max(4096, chunkBytes); // floor guards against a config typo of "0"
        this.log = log;
        Directory.CreateDirectory(this.cacheRoot);
        stateFile = Path.Combine(this.cacheRoot, "heavystage.json");
        LoadState();
    }

    /// <summary>The path <c>{rom}</c> resolves to for a staged app (final target, no suffix).</summary>
    public string TargetPathFor(HeavyApp app)
    {
        // One folder per app id keeps titles separate and eviction (a human act) obvious.
        var name = Path.GetFileName(app.Staging!.Source);
        return Path.Combine(cacheRoot, Sanitize(app.Id), name);
    }

    /// <summary>Staged and verified — the card can say Play.</summary>
    public bool IsStaged(HeavyApp app)
    {
        if (!app.NeedsStaging) return true;
        var s = Get(app.Id);
        if (s is { Phase: "done" } && File.Exists(s.Target)) return true;
        // Adoption: a file already at the target (hand-copied, or pre-dating the stager) counts —
        // the 20 hand-staged Switch titles must not re-copy (plan §5). Size must match the source.
        var target = TargetPathFor(app);
        if (!File.Exists(target)) return false;
        try
        {
            var srcLen = new FileInfo(app.Staging!.Source).Length;
            return new FileInfo(target).Length == srcLen;
        }
        catch { return File.Exists(target); } // source unreachable (NAS off) — trust the local file
    }

    public StageState? Get(string appId) { lock (gate) return states.GetValueOrDefault(appId); }

    /// <summary>Progress snapshot for the status endpoint (also covers never-started apps).</summary>
    public object Progress(HeavyApp app)
    {
        if (!app.NeedsStaging) return new { state = "local", stagedBytes = 0L, totalBytes = 0L };
        if (IsStaged(app))
        {
            long total = 0;
            try { total = new FileInfo(TargetPathFor(app)).Length; } catch { }
            return new { state = "done", stagedBytes = total, totalBytes = total };
        }
        var s = Get(app.Id);
        long srcTotal = s?.TotalBytes ?? SafeLength(app.Staging!.Source);
        return new
        {
            state = s?.Phase ?? "none",
            stagedBytes = s?.StagedBytes ?? 0,
            verifiedBytes = s?.VerifiedBytes ?? 0,
            totalBytes = srcTotal,
            error = s?.Error,
        };
    }

    /// <summary>
    /// Advance this app's staging by ONE bounded chunk (copy or verify) and report progress. The
    /// caller loops until <c>state == "done"</c>. Idempotent: a done app returns done; a fresh call
    /// after an interruption picks up at the persisted offset.
    /// </summary>
    public object Advance(HeavyApp app)
    {
        if (!app.NeedsStaging) return new { state = "local" };
        // One chunk in flight globally (staging is disk-bound) — but on the SEPARATE semaphore, not
        // the state lock: a 256 MB read off the NAS takes seconds, and status polls must not stall
        // behind it. Concurrent Advance callers simply queue; each call stays bounded.
        advanceSem.Wait();
        try { return AdvanceCore(app); }
        catch (Exception ex)
        {
            log.LogError(ex, "Heavy stage chunk failed for {App}", app.Id);
            lock (gate)
            {
                var s = states.GetValueOrDefault(app.Id);
                if (s != null) { s.Phase = "error"; s.Error = ex.Message; SaveState(); }
            }
            return new { state = "error", error = ex.Message };
        }
        finally { advanceSem.Release(); }
    }

    private object AdvanceCore(HeavyApp app)
    {
        if (IsStaged(app)) return Progress(app);

        var source = app.Staging!.Source;
        if (!File.Exists(source)) return new { state = "error", error = "Source file not found on the library drive." };
        var target = TargetPathFor(app);
        var partial = target + ".partial";
        var total = new FileInfo(source).Length;

        StageState? s;
        lock (gate) s = states.GetValueOrDefault(app.Id);
        if (s == null || s.Phase == "error" || s.Target != target || s.TotalBytes != total || !File.Exists(partial) && s.StagedBytes > 0)
        {
            // Fresh start (first call, error retry, or the partial vanished): check the cap FIRST.
            long used = CacheUsedBytes();
            if (used + total > maxBytes)
                return new { state = "error", error = $"Heavy cache cap would be exceeded ({(used + total) / (1024 * 1024 * 1024)} GB > {maxBytes / (1024 * 1024 * 1024)} GB). Evict a title manually or raise the budget — the stager never deletes on its own." };
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(partial)) File.Delete(partial);
            s = new StageState { AppId = app.Id, Source = source, Target = target, TotalBytes = total, ChunkBytes = chunkBytes };
            lock (gate) states[app.Id] = s;
        }
        long chunk = s.ChunkBytes > 0 ? s.ChunkBytes : chunkBytes;

        if (s.Phase == "copy")
        {
            using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var dst = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 20))
            {
                src.Position = s.StagedBytes;
                dst.Position = s.StagedBytes;
                var (written, chunkHash) = CopyChunk(src, dst, Math.Min(chunk, total - s.StagedBytes));
                dst.Flush(flushToDisk: true);
                s.StagedBytes += written;
                s.ChainHash = Chain(s.ChainHash, chunkHash);
            }
            if (s.StagedBytes >= total) { s.Phase = "verify"; s.VerifiedBytes = 0; s.VerifyChainHash = ""; }
        }
        else if (s.Phase == "verify")
        {
            using (var f = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                f.Position = s.VerifiedBytes;
                // Verify re-reads on the SAME chunk boundaries as the copy so the chains are comparable.
                var (read, chunkHash) = HashChunk(f, Math.Min(chunk, total - s.VerifiedBytes));
                s.VerifiedBytes += read;
                s.VerifyChainHash = Chain(s.VerifyChainHash, chunkHash);
            }
            if (s.VerifiedBytes >= total)
            {
                if (!string.Equals(s.VerifyChainHash, s.ChainHash, StringComparison.Ordinal))
                {
                    // Torn/corrupt copy: reset for a clean re-copy rather than shipping a bad ROM.
                    log.LogWarning("Heavy stage verify MISMATCH for {App} — restarting the copy", app.Id);
                    File.Delete(partial);
                    lock (gate) { states.Remove(app.Id); SaveState(); }
                    return new { state = "copy", stagedBytes = 0L, totalBytes = total, note = "verify mismatch — restarted" };
                }
                File.Move(partial, target, overwrite: true);
                s.Phase = "done";
                log.LogInformation("Heavy staged {App}: {GB:F1} GB verified → {Target}", app.Id, total / 1073741824.0, target);
            }
        }

        s.UpdatedUtc = DateTime.UtcNow;
        lock (gate) SaveState();
        return Progress(app);
    }

    private static (long written, string hash) CopyChunk(Stream src, Stream dst, long budget)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        long done = 0;
        while (done < budget)
        {
            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, budget - done));
            if (n <= 0) break;
            dst.Write(buf, 0, n);
            sha.AppendData(buf, 0, n);
            done += n;
        }
        return (done, Convert.ToHexString(sha.GetHashAndReset()));
    }

    private static (long read, string hash) HashChunk(Stream src, long budget)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        long done = 0;
        while (done < budget)
        {
            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, budget - done));
            if (n <= 0) break;
            sha.AppendData(buf, 0, n);
            done += n;
        }
        return (done, Convert.ToHexString(sha.GetHashAndReset()));
    }

    private static string Chain(string prev, string chunkHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(prev + chunkHash)));

    private long CacheUsedBytes()
    {
        long sum = 0;
        foreach (var f in Directory.EnumerateFiles(cacheRoot, "*", SearchOption.AllDirectories))
            try { sum += new FileInfo(f).Length; } catch { }
        return sum;
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static string Sanitize(string id)
    {
        var s = id;
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(stateFile))
                states = JsonSerializer.Deserialize<Dictionary<string, StageState>>(File.ReadAllText(stateFile), JsonOpts)
                         ?? new(StringComparer.OrdinalIgnoreCase);
            states = new Dictionary<string, StageState>(states, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { log.LogWarning(ex, "Heavy stage state unreadable — starting clean"); }
    }

    private void SaveState()
    {
        try { File.WriteAllText(stateFile, JsonSerializer.Serialize(states, JsonOpts)); }
        catch (Exception ex) { log.LogWarning(ex, "Heavy stage state save failed"); }
    }
}
