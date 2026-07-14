using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MovieTheater.ArcadeGateway;

/// <summary>
/// Just-in-time ROM cache for the arcade (docs/arcade-jit-cache.md). Some systems — PS1 especially —
/// have a master collection far too large to pre-stage (the PSX set is ~448 GB of <c>.7z</c> discs).
/// Instead the site catalogs every game as browsable, pointing each row at its source archive; this
/// cache, which lives on the media host next to the disk, extracts a game's ROM into the workers'
/// read-only ROM mount the first time someone plays it, and LRU-evicts cold games to hold total disk
/// under a cap.
///
/// <para>The gateway is the right home: it is the only site component co-located with the ROM disk in
/// the go-live topology (the control-plane API runs in the k8s pod, which has no disk), and it already
/// gates every connection — so it can materialize before forwarding and pin/unpin around the live
/// session. It stays DB-free: the site exports a manifest (<c>arcade-romcache-export</c>) mapping the
/// capability token's <c>gameId</c> to a source archive; this reads that file.</para>
///
/// <para><b>Saves are unaffected</b> by extract/evict: CloudRetro keys save files by room id, not ROM,
/// and writes them to the separate <c>/saves</c> mount; <c>/roms</c> is read-only so the core can never
/// write beside a ROM. Re-extraction is byte-identical, so a save reattaches by room id.</para>
///
/// <para><b>Rescan:</b> a freshly-extracted ROM postdates the worker's boot-time library scan.
/// CloudRetro's fsnotify WatchMode does not reliably fire across the Windows→WSL2 bind mount, so the
/// worker image carries a scan-on-miss patch (docker/arcade/patches/0001-jit-scan-on-miss.patch): on a
/// launch miss it rescans once and retries. We therefore only need the file on disk before forwarding.</para>
///
/// <para><b>Destructive-safety</b> (global bulk-job rule): eviction only ever deletes files it extracted
/// itself, each re-verified to live under the ROM mount; it never touches the source archive (which sits
/// on the library drive, outside the mount) nor any file it did not create.</para>
/// </summary>
public sealed class RomCacheOptions
{
    /// <summary>Path to the manifest written by <c>arcade-romcache-export</c>.</summary>
    public string? ManifestPath { get; set; }

    /// <summary>Host path of the ROM mount root (same as compose <c>ROMS_DIR</c>, e.g. D:\Arcade\roms).</summary>
    public string? RomsDir { get; set; }

    /// <summary>Disk cap for extracted JIT ROMs. Default 30 GB.</summary>
    public long MaxBytes { get; set; } = 30L * 1024 * 1024 * 1024;

    /// <summary>7-Zip executable. Default the standard Windows install; falls back to "7z" on PATH.</summary>
    public string SevenZipPath { get; set; } = @"C:\Program Files\7-Zip\7z.exe";

    /// <summary>Max concurrent extractions (they are disk-bound). Default 1.</summary>
    public int MaxParallelExtractions { get; set; } = 1;

    /// <summary>Per-extraction timeout. A big multi-track PS1 disc unpacks in well under this.</summary>
    public int ExtractTimeoutSeconds { get; set; } = 300;

    public bool Enabled => !string.IsNullOrWhiteSpace(ManifestPath) && !string.IsNullOrWhiteSpace(RomsDir);
}

public sealed class RomCache
{
    // Exts = the system's candidate ROM extensions (e.g. [".cue",".chd"] for PS1, [".sfc",".smc"] for
    // SNES, [".md",".gen",".smd",".bin"] for Genesis). A JIT archive holds one launch ROM whose base
    // name equals the archive's; after extraction "present" = <GameKey><one of Exts> exists. Nullable for
    // back-compat with a pre-generalization manifest (then we assume the PS1 default, ".cue").
    // Deps = extra source archives that must be staged into the same folder alongside the launch ROM so
    // the core can assemble the set — the FBNeo romof closure (a game's split parent + BIOS zips such as
    // neogeo.zip). Without them fbneo reports "missing romset". Staged verbatim by filename, idempotently,
    // and deliberately NOT tracked for eviction: a BIOS zip is shared by hundreds of games, so it stays
    // resident once copied (the closure is small and bounded, far under the cap).
    public sealed record ManifestGame(int GameId, string GameKey, string System, string Folder, string Archive, string[]? Exts = null, DiscRef[]? Discs = null, string[]? Deps = null);

    // A member disc of a multi-disc .m3u game: the source archive to extract + the .cue/.chd filename it
    // produces (which goes, in order, into the generated playlist). See docs/arcade-dedupe-multidisc-plan.md.
    public sealed record DiscRef(string Archive, string File);
    private sealed record Manifest(int Version, List<ManifestGame> Games);

    private sealed class GameState
    {
        public required List<string> Files;   // absolute dest paths we extracted
        public long Bytes;
        public long LastUsed;                 // monotonic LRU tick
        public int Pins;                      // live connections referencing this game
    }

    private readonly RomCacheOptions opt;
    private readonly ILogger log;
    private readonly string romsRoot;
    private readonly string sevenZip;

    private readonly object gate = new();
    private readonly Dictionary<int, ManifestGame> byId = new();
    private readonly Dictionary<int, GameState> materialized = new();
    private readonly Dictionary<int, SemaphoreSlim> gameLocks = new();
    private readonly SemaphoreSlim extractSem;
    private long clock;
    private DateTime manifestMtime;

    public RomCache(RomCacheOptions options, ILogger logger)
    {
        opt = options;
        log = logger;
        romsRoot = Path.GetFullPath(opt.RomsDir!);
        sevenZip = ResolveSevenZip(opt.SevenZipPath);
        extractSem = new SemaphoreSlim(Math.Max(1, opt.MaxParallelExtractions));
        LoadManifest();
        ReconcileFromDisk();
    }

    /// <summary>Manifest games, for logging/diagnostics.</summary>
    public int CatalogCount { get { lock (gate) return byId.Count; } }

    /// <summary>True if this game is JIT-backed (has a manifest entry).</summary>
    public bool IsManaged(int gameId)
    {
        MaybeReloadManifest();
        lock (gate) return byId.ContainsKey(gameId);
    }

    /// <summary>
    /// Ensure the game's ROM is present under the mount before its worker tries to launch it. Idempotent
    /// and safe under concurrency: a per-game lock collapses simultaneous first-plays into one extraction;
    /// an already-present game just refreshes its LRU stamp. No-op for non-managed games.
    /// </summary>
    public Task EnsureMaterializedAsync(int gameId, CancellationToken ct = default)
        => EnsureMaterializedAsync(gameId, ct, null);

    private async Task EnsureMaterializedAsync(int gameId, CancellationToken ct, StageJob? job)
    {
        MaybeReloadManifest();
        ManifestGame? g;
        lock (gate) { byId.TryGetValue(gameId, out g); }
        if (g is null) return; // directly-staged (non-JIT) game — nothing to do

        var gameLock = LockFor(gameId);
        await gameLock.WaitAsync(ct);
        try
        {
            if (IsPresent(g))
            {
                // Game ROM already resident, but its dependency closure may not be (e.g. it was staged
                // before deps were added to the manifest). Ensure it — idempotent and cheap when present.
                await StageDepsAsync(g, FolderDest(g), ct);
                Touch(gameId, g);
                return;
            }

            await extractSem.WaitAsync(ct);
            try
            {
                progress.Value = job;
                await ExtractAsync(g, ct);
            }
            finally { progress.Value = null; extractSem.Release(); }

            RecordMaterialized(gameId, g);
            Evict();
        }
        finally { gameLock.Release(); }
    }

    // The staging job for the extraction running on THIS async flow, so the decompressors can report
    // progress without threading a parameter through every call site (7-Zip, copy, gcz, cso).
    private static readonly AsyncLocal<StageJob?> progress = new();

    private void ReportProgress(long done, long total)
    {
        var j = progress.Value;
        if (j is null) return;
        lock (gate) { j.Done = done; j.Total = total; }
    }

    // ── Staging as an OBSERVABLE, DETACHED job (2026-07-14) ─────────────────────────────────────────
    //
    // Staging used to run inline on the WebSocket upgrade with the caller's RequestAborted token, which
    // made the player's patience the timeout: the browser sat on "Connecting…" while a 562 MB image was
    // inflated, gave up, and the abort CANCELLED the extraction — so the next attempt started from
    // scratch and could never finish either. A ROM being prepared is a STATE, not a race; the client is
    // entitled to be told about it.
    //
    // So preparation is a background job keyed by game id, running on CancellationToken.None (nobody's
    // disconnect can kill it), and the client polls Status() to render "Preparing ROM… n%" and connects
    // when it flips to Ready.

    public enum StageState { Absent, Preparing, Ready, Failed }

    public sealed record StageStatus(StageState State, int Percent, string? Error);

    private readonly Dictionary<int, StageJob> jobs = new();

    private sealed class StageJob
    {
        public Task? Task;
        public long Done, Total;
        public string? Error;
    }

    /// <summary>Where this game's ROM is: already staged, being prepared (with progress), or failed.</summary>
    public StageStatus Status(int gameId)
    {
        MaybeReloadManifest();
        ManifestGame? g;
        lock (gate) { byId.TryGetValue(gameId, out g); }
        if (g is null) return new StageStatus(StageState.Ready, 100, null); // not JIT-backed: nothing to prepare
        if (IsPresent(g)) return new StageStatus(StageState.Ready, 100, null);

        lock (gate)
        {
            if (!jobs.TryGetValue(gameId, out var j)) return new StageStatus(StageState.Absent, 0, null);
            if (j.Error is not null) return new StageStatus(StageState.Failed, 0, j.Error);
            var pct = j.Total > 0 ? (int)Math.Clamp(j.Done * 100 / j.Total, 0, 99) : 0;
            return new StageStatus(StageState.Preparing, pct, null);
        }
    }

    /// <summary>
    /// Start (or join) this game's preparation and return immediately. The job is DETACHED — it runs on
    /// CancellationToken.None, so a client that closes the tab mid-inflate no longer aborts the work that
    /// the next player will need anyway.
    /// </summary>
    public void BeginMaterialize(int gameId)
    {
        if (!IsManaged(gameId)) return;
        lock (gate)
        {
            if (jobs.TryGetValue(gameId, out var existing) && existing.Task is { IsCompleted: false }) return;
            var job = new StageJob();
            jobs[gameId] = job;
            job.Task = Task.Run(async () =>
            {
                try
                {
                    await EnsureMaterializedAsync(gameId, CancellationToken.None, job);
                }
                catch (Exception ex)
                {
                    lock (gate) job.Error = ex.Message;
                    log.LogError(ex, "RomCache preparation failed for game {GameId}", gameId);
                }
            });
        }
    }

    /// <summary>Await this game's preparation, starting it if nobody has. Never cancelled by the caller.</summary>
    public async Task WaitMaterializedAsync(int gameId, CancellationToken ct = default)
    {
        BeginMaterialize(gameId);
        Task? t;
        lock (gate) { jobs.TryGetValue(gameId, out var j); t = j?.Task; }
        if (t is null) return;
        // ct only abandons the WAIT, never the WORK.
        await t.WaitAsync(ct);
        var s = Status(gameId);
        if (s.State == StageState.Failed) throw new InvalidOperationException(s.Error ?? "ROM preparation failed");
    }

    /// <summary>Mark a game in-use for the life of a connection so eviction won't pull it mid-session.</summary>
    public void Pin(int gameId)
    {
        lock (gate)
        {
            if (materialized.TryGetValue(gameId, out var s)) { s.Pins++; s.LastUsed = ++clock; }
        }
    }

    public void Unpin(int gameId)
    {
        lock (gate)
        {
            if (materialized.TryGetValue(gameId, out var s) && s.Pins > 0) s.Pins--;
        }
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────────

    private SemaphoreSlim LockFor(int gameId)
    {
        lock (gate)
        {
            if (!gameLocks.TryGetValue(gameId, out var l)) { l = new SemaphoreSlim(1, 1); gameLocks[gameId] = l; }
            return l;
        }
    }

    private void Touch(int gameId, ManifestGame g)
    {
        lock (gate)
        {
            if (materialized.TryGetValue(gameId, out var s)) s.LastUsed = ++clock;
            else RecordMaterializedLocked(gameId, g);
        }
    }

    private static readonly string[] DefaultExts = { ".cue" };

    private string[] ExtsOf(ManifestGame g) => (g.Exts is { Length: > 0 }) ? g.Exts : DefaultExts;

    private string RomDest(ManifestGame g, string ext) =>
        Path.GetFullPath(Path.Combine(romsRoot, g.Folder, g.GameKey + ext));

    private string FolderDest(ManifestGame g) =>
        Path.GetFullPath(Path.Combine(romsRoot, g.Folder));

    // "Present" = the launch ROM the catalog promised exists on disk under one of the system's candidate
    // extensions (a PS1 .cue, a SNES .sfc, a Genesis .md/.bin, …).
    private bool IsPresent(ManifestGame g) => ExtsOf(g).Any(e => File.Exists(RomDest(g, e)));

    // The source IS the launch ROM (an uncompressed N64 .z64, or a MAME .zip fbneo loads whole) when its
    // extension is one of the system's ROM extensions — then we COPY it into the mount rather than extract.
    // Otherwise it's an archive (.7z/.zip) that contains the ROM, and we extract. Either way it's still a
    // bounded, on-demand, LRU-evicted cache — never a mass pre-copy.
    private bool SourceIsRom(ManifestGame g) =>
        ExtsOf(g).Contains(Path.GetExtension(g.Archive), StringComparer.OrdinalIgnoreCase);

    // A multi-disc member whose source file IS the launch ROM (its extension matches the playlist filename
    // we place — a GameCube .gcz, a .chd) is COPIED; one whose source differs (a PS1 .7z → .cue) is extracted.
    private static bool DiscSourceIsRom(DiscRef d) =>
        Path.GetExtension(d.Archive).Equals(Path.GetExtension(d.File), StringComparison.OrdinalIgnoreCase);

    private string CopyDest(ManifestGame g) =>
        Path.GetFullPath(Path.Combine(FolderDest(g), Path.GetFileName(g.Archive)));

    // Copy each dependency archive (fbneo romof parent/BIOS zips) into the game's folder under its own
    // filename. Idempotent (skip when already staged — shared BIOS zips serve many games) and tolerant (a
    // truly absent dep means an incomplete romset, which fbneo will surface at launch — not our failure).
    // Deliberately NOT recorded in the eviction whitelist, so once resident a BIOS/parent zip stays put.
    private async Task StageDepsAsync(ManifestGame g, string dest, CancellationToken ct)
    {
        if (g.Deps is not { Length: > 0 }) return;
        foreach (var dep in g.Deps)
        {
            try
            {
                if (!File.Exists(dep)) { log.LogWarning("RomCache dep archive missing for game {GameId}: {Dep}", g.GameId, dep); continue; }
                var to = Path.Combine(dest, Path.GetFileName(dep));
                if (File.Exists(to)) continue; // already staged (shared parent/BIOS)
                using var src = new FileStream(dep, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst, ct);
            }
            catch (Exception ex) { log.LogWarning(ex, "RomCache could not stage dep {Dep} for game {GameId}", dep, g.GameId); }
        }
    }

    /// <summary>
    /// Materialize one source file into the mount. Plain sources are copied byte-for-byte; a Dolphin
    /// <c>.gcz</c> is DECOMPRESSED to the raw disc image, written under the SAME <c>.gcz</c> filename
    /// (Dolphin's DiscIO opens by content sniffing, not extension, so the game key, manifest, eviction
    /// whitelist and DB paths all stay untouched). Why: GameCube titles with disc-streamed (DTK) audio
    /// — F-Zero GX's announcer being the canonical case — issue continuous small disc reads during play,
    /// and each read of a compressed image pays zlib inflation on the emulator thread. Uncompressed on
    /// the cache disk removes that class of stutter for the cost of ~40% more staged bytes, which is
    /// exactly what the LRU cap is for. (2026-07-11, the F-Zero GX slowdown post-mortem.)
    /// </summary>
    /// <remarks>
    /// EVERYTHING here writes to a <c>.part</c> file and RENAMES it into place only on success, because
    /// "present" is decided by <see cref="IsPresent"/> — which just asks whether the file exists. A
    /// half-written image left behind by a cancel (the player closed the tab), a crash, or an eviction
    /// racing an extract therefore does not merely waste a stage: it counts as STAGED FOREVER, and the
    /// core boots a TRUNCATED disc image. Observed live: a cancelled cso decompress left a 499 MB stub of
    /// a 562 MB ISO, and nothing in the cache would ever have noticed. Rename is atomic on NTFS, so the
    /// destination only ever exists complete.
    /// </remarks>
    private async Task StageRomFileAsync(string source, string dest, CancellationToken ct)
    {
        var part = dest + ".part";
        try
        {
            var ext = Path.GetExtension(source);
            var decompressed = false;
            if (ext.Equals(".gcz", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await Task.Run(() => GczDecompressTo(source, part, ct, ReportProgress), ct);
                    log.LogInformation("RomCache decompressed gcz -> raw image: {Dest}", dest);
                    decompressed = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A malformed/unknown gcz falls back to the plain copy — the core reads compressed
                    // images fine; this path is an optimization, never a gate.
                    log.LogWarning(ex, "RomCache gcz decompress failed for {Source}; staging compressed copy", source);
                }
            }
            else if (ext.Equals(".cso", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await Task.Run(() => CsoDecompressTo(source, part, ct, ReportProgress), ct);
                    log.LogInformation("RomCache decompressed cso -> raw iso: {Dest}", dest);
                    decompressed = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Same contract as gcz: an optimization, never a gate. A ZSO (LZ4) or an already-raw
                    // image named .cso lands here and is copied as-is, which still plays.
                    log.LogWarning(ex, "RomCache cso decompress failed for {Source}; staging compressed copy", source);
                }
            }

            if (!decompressed)
            {
                using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst, ct);
            }

            File.Move(part, dest, overwrite: true);
        }
        catch
        {
            // Never leave a stub that IsPresent() would mistake for a staged ROM.
            try { if (File.Exists(part)) File.Delete(part); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// PSP CSO (CISO) → raw ISO, written under the SAME <c>.cso</c> filename — the identical bargain the
    /// <c>.gcz</c> path makes above, and for the identical reason. PPSSPP picks its block device by
    /// SNIFFING the magic (<c>constructBlockDevice</c>: "CISO" → CISOFileBlockDevice, else a plain
    /// FileBlockDevice), so a raw image keeping the .cso name plays fine and the game key, manifest,
    /// eviction whitelist and DB paths all stay untouched.
    ///
    /// Why it matters: a compressed image inflates zlib ON THE EMULATOR THREAD for every disc read. PSP
    /// games stream ATRAC3+ music and assets off the disc continuously during play — LocoRoco is the
    /// canonical case — so those inflations land as stalls inside retro_run, which is a hole in the audio
    /// the encoder cannot fill. It is the same stutter class the gcz change fixed for GameCube DTK audio.
    /// Costs ~40% more staged bytes, which is what the LRU cap is for.
    ///
    /// Format (CISO v1): 24-byte header — magic "CISO", header_size u32, total_bytes u64, block_size u32,
    /// version u8, index_shift u8, 2 reserved. Then (blocks+1) u32 index entries: offset = (e &amp; 0x7FFFFFFF)
    /// &lt;&lt; index_shift, and the top bit means the block is stored RAW. Compressed blocks are BARE deflate
    /// (no zlib header) — the one real difference from gcz, and getting it wrong yields garbage, not an error.
    /// </summary>
    internal static void CsoDecompressTo(string source, string dest, CancellationToken ct,
                                         Action<long, long>? onProgress = null)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(src);
        if (br.ReadUInt32() != 0x4F534943u) throw new InvalidDataException("not a CISO file"); // "CISO" LE
        _ = br.ReadUInt32();                     // header size (24)
        var totalBytes = br.ReadUInt64();
        var blockSize = br.ReadUInt32();
        var version = br.ReadByte();
        var indexShift = br.ReadByte();
        _ = br.ReadUInt16();                     // reserved
        if (version > 1) throw new InvalidDataException($"unsupported CISO version {version}");
        if (blockSize == 0 || totalBytes == 0) throw new InvalidDataException("degenerate CISO header");

        var numBlocks = (int)(totalBytes / blockSize);
        var index = new uint[numBlocks + 1];     // +1: the last entry bounds the final block
        for (var i = 0; i <= numBlocks; i++) index[i] = br.ReadUInt32();

        const uint RawFlag = 0x80000000u;
        // ReadWrite (not Write): the ISO9660 signature check below reads back what we wrote.
        using var dst = new FileStream(dest, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var block = new byte[blockSize];
        long written = 0;
        for (var i = 0; i < numBlocks; i++)
        {
            ct.ThrowIfCancellationRequested();
            var raw = (index[i] & RawFlag) != 0;
            var off = (long)(index[i] & ~RawFlag) << indexShift;
            var end = (long)(index[i + 1] & ~RawFlag) << indexShift;
            var len = (int)(end - off);
            if (len <= 0) throw new InvalidDataException($"bad CISO index at block {i}");

            src.Seek(off, SeekOrigin.Begin);
            if (raw)
            {
                // A raw block is exactly one block; index alignment can make `len` overshoot into padding.
                src.ReadExactly(block, 0, (int)blockSize);
                dst.Write(block, 0, (int)blockSize);
            }
            else
            {
                var chunk = new byte[len];
                src.ReadExactly(chunk);
                using var z = new System.IO.Compression.DeflateStream(
                    new MemoryStream(chunk), System.IO.Compression.CompressionMode.Decompress);
                var got = z.ReadAtLeast(block, (int)blockSize, throwOnEndOfStream: false);
                if (got != (int)blockSize) throw new InvalidDataException($"short CISO block {i}: {got}/{blockSize}");
                dst.Write(block, 0, (int)blockSize);
            }
            written += blockSize;
            if ((i & 0x3FF) == 0) onProgress?.Invoke(written, (long)totalBytes); // every ~1k blocks
        }
        onProgress?.Invoke(written, (long)totalBytes);
        if ((ulong)written != totalBytes)
            throw new InvalidDataException($"cso decompress size mismatch: wrote {written}, header says {totalBytes}");

        // Prove it is actually an ISO before we hand it to the core. A size-correct but WRONG decode (a
        // misread index, the zlib-vs-bare-deflate trap) would otherwise ship a corrupt image silently and
        // present as "the game won't boot" — far worse than the compressed copy this falls back to.
        dst.Seek(0x8001, SeekOrigin.Begin);
        var sig = new byte[5];
        dst.ReadExactly(sig);
        if (!sig.AsSpan().SequenceEqual("CD001"u8))
            throw new InvalidDataException("cso decompressed to something that is not an ISO9660 image");
    }

    /// <summary>
    /// Dolphin GCZ → raw disc image. Format: 32-byte header (magic <c>0xB10BC001</c>, sub-type,
    /// compressed_size u64, data_size u64, block_size u32, num_blocks u32), then u64 block pointers
    /// (top bit set = block stored raw), u32 adler32 table (unused here), then block data. Compressed
    /// blocks are zlib-wrapped deflate.
    /// </summary>
    internal static void GczDecompressTo(string source, string dest, CancellationToken ct,
                                         Action<long, long>? onProgress = null)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(src);
        if (br.ReadUInt32() != 0xB10BC001u) throw new InvalidDataException("not a GCZ file");
        _ = br.ReadUInt32();                       // sub-type
        var compressedSize = br.ReadUInt64();
        var dataSize = br.ReadUInt64();
        _ = br.ReadUInt32();                       // block size
        var numBlocks = br.ReadUInt32();
        var ptrs = new ulong[numBlocks];
        for (var i = 0; i < numBlocks; i++) ptrs[i] = br.ReadUInt64();
        src.Seek(4L * numBlocks, SeekOrigin.Current); // adler table
        var dataStart = src.Position;

        const ulong RawFlag = 1UL << 63;
        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        long written = 0;
        for (var i = 0; i < numBlocks; i++)
        {
            ct.ThrowIfCancellationRequested();
            var off = (long)(ptrs[i] & ~RawFlag);
            var end = i + 1 < numBlocks ? (long)(ptrs[i + 1] & ~RawFlag) : (long)compressedSize;
            src.Seek(dataStart + off, SeekOrigin.Begin);
            var chunk = new byte[end - off];
            src.ReadExactly(chunk);
            if ((ptrs[i] & RawFlag) != 0)
            {
                dst.Write(chunk);
                written += chunk.Length;
            }
            else
            {
                using var z = new System.IO.Compression.ZLibStream(new MemoryStream(chunk), System.IO.Compression.CompressionMode.Decompress);
                var before = dst.Position;
                z.CopyTo(dst);
                written += dst.Position - before;
            }
            if ((i & 0xFF) == 0) onProgress?.Invoke(written, (long)dataSize);
        }
        onProgress?.Invoke(written, (long)dataSize);
        if ((ulong)written != dataSize)
            throw new InvalidDataException($"gcz decompress size mismatch: wrote {written}, header says {dataSize}");
    }

    private async Task ExtractAsync(ManifestGame g, CancellationToken ct)
    {
        var dest = FolderDest(g);
        Directory.CreateDirectory(dest);

        // Stage the dependency closure (fbneo romof parent+BIOS zips) into the same folder before the game.
        await StageDepsAsync(g, dest, ct);

        // Multi-disc: materialize every member disc, then write the .m3u playlist the core loads and swaps
        // discs within (patch 0005). GameKey is the playlist basename; Exts is [".m3u"]. A disc's source is
        // either an archive to EXTRACT (a PS1 .7z → .cue+tracks) or the ROM ITSELF to COPY (a GameCube .gcz,
        // a .chd) — same distinction as the single-disc path, decided per disc.
        if (g.Discs is { Length: > 0 })
        {
            foreach (var d in g.Discs)
            {
                if (!File.Exists(d.Archive))
                    throw new FileNotFoundException($"source disc archive missing: {d.Archive}");
                if (DiscSourceIsRom(d))
                {
                    var to = Path.Combine(dest, d.File);
                    await StageRomFileAsync(d.Archive, to, ct);
                }
                else
                {
                    await RunSevenZipAsync(new[] { "x", "-y", "-bd", d.Archive, "-o" + dest }, ct);
                }
                if (!File.Exists(Path.Combine(dest, d.File)))
                    throw new InvalidOperationException($"disc materialize produced no {d.File} in {dest}.");
            }
            await File.WriteAllLinesAsync(RomDest(g, ".m3u"), g.Discs.Select(d => d.File), ct);
            if (!IsPresent(g))
                throw new InvalidOperationException($"failed to write playlist {g.GameKey}.m3u in {dest}.");
            return;
        }

        if (!File.Exists(g.Archive))
            throw new FileNotFoundException($"source ROM/archive missing: {g.Archive}");

        if (SourceIsRom(g))
        {
            var to = CopyDest(g);
            await StageRomFileAsync(g.Archive, to, ct);
            if (!IsPresent(g))
                throw new InvalidOperationException($"copy of {g.GameKey} produced no {string.Join('/', ExtsOf(g))} in {dest}.");
            return;
        }

        // 7z x: extract preserving internal names, overwrite, into the system folder. 7z returns exit 1
        // on a *warning* (non-fatal) and 2+ on a real error — so success is gated on the expected launch
        // ROM actually appearing, not on the exit code.
        var (code, err) = await RunSevenZipAsync(new[] { "x", "-y", "-bd", g.Archive, "-o" + dest }, ct);
        if (!IsPresent(g))
            throw new InvalidOperationException(
                $"7z exit {code} extracting {g.GameKey}; no {string.Join('/', ExtsOf(g))} found in {dest}. {Trunc(err)}");
    }

    private void RecordMaterialized(int gameId, ManifestGame g)
    {
        lock (gate) RecordMaterializedLocked(gameId, g);
    }

    private void RecordMaterializedLocked(int gameId, ManifestGame g)
    {
        var files = ExtractedFilesOnDisk(g);
        long bytes = 0;
        foreach (var f in files) { try { bytes += new FileInfo(f).Length; } catch { /* raced */ } }
        materialized[gameId] = new GameState { Files = files, Bytes = bytes, LastUsed = ++clock, Pins = 0 };
    }

    // The set of files this game owns on disk. We derive it from the archive listing so it's exact and
    // not confused by a sibling game's files sharing the folder; only paths that resolve UNDER the mount
    // and actually exist are kept (the eviction whitelist).
    private List<string> ExtractedFilesOnDisk(ManifestGame g)
    {
        var dest = FolderDest(g);

        // Multi-disc: the .m3u playlist plus, per disc, the copied ROM (one file) or the archive's unpacked
        // entries. Without this the eviction whitelist would be empty and multi-disc discs would leak disk.
        if (g.Discs is { Length: > 0 })
        {
            var discFiles = new List<string>();
            void Keep(string name)
            {
                var full = Path.GetFullPath(Path.Combine(dest, name));
                if (IsUnderRoot(full) && File.Exists(full) && !discFiles.Contains(full)) discFiles.Add(full);
            }
            Keep(g.GameKey + ".m3u");
            foreach (var d in g.Discs)
            {
                if (DiscSourceIsRom(d)) Keep(d.File);                              // the copied ROM
                else foreach (var internalPath in ListArchiveEntries(d.Archive))  // the extracted entries
                    Keep(internalPath);
            }
            return discFiles;
        }

        // A copied-in ROM owns exactly one file on disk (the copy); an extracted archive owns whatever it
        // unpacked, derived from its listing.
        if (SourceIsRom(g))
        {
            var to = CopyDest(g);
            return (IsUnderRoot(to) && File.Exists(to)) ? new List<string> { to } : new List<string>();
        }
        var result = new List<string>();
        foreach (var internalPath in ListArchiveEntries(g.Archive))
        {
            var full = Path.GetFullPath(Path.Combine(dest, internalPath));
            if (IsUnderRoot(full) && File.Exists(full)) result.Add(full);
        }
        return result;
    }

    private void Evict()
    {
        lock (gate)
        {
            long total = materialized.Values.Sum(s => s.Bytes);
            if (total <= opt.MaxBytes) return;

            foreach (var kv in materialized.Where(k => k.Value.Pins == 0).OrderBy(k => k.Value.LastUsed).ToList())
            {
                if (total <= opt.MaxBytes) break;
                var (gameId, s) = (kv.Key, kv.Value);
                long freed = DeleteFiles(s.Files);
                materialized.Remove(gameId);
                total -= s.Bytes;
                log.LogInformation("RomCache evicted game {GameId} (freed ~{MB} MB)", gameId, freed / (1024 * 1024));
            }

            var stillOver = materialized.Values.Sum(s => s.Bytes);
            if (stillOver > opt.MaxBytes)
                log.LogWarning("RomCache over cap ({Have} > {Cap}) but remaining games are pinned (in use).",
                    stillOver, opt.MaxBytes);
        }
    }

    // Guarded delete: each path is re-verified to live under the ROM mount before removal; the source
    // archive can never match (it's off-mount). Best-effort — a file held open is left for the next pass.
    private long DeleteFiles(IEnumerable<string> files)
    {
        long freed = 0;
        foreach (var f in files)
        {
            try
            {
                var full = Path.GetFullPath(f);
                if (!IsUnderRoot(full) || !File.Exists(full)) continue;
                var len = new FileInfo(full).Length;
                File.Delete(full);
                freed += len;
            }
            catch (Exception ex) { log.LogWarning(ex, "RomCache could not delete {File}", f); }
        }
        return freed;
    }

    private bool IsUnderRoot(string fullPath)
    {
        var root = romsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private void ReconcileFromDisk()
    {
        // On (re)start, pick up games already extracted from a previous run so their disk is counted and
        // LRU-ordered — otherwise the cap would be blind to them.
        lock (gate)
        {
            foreach (var (id, g) in byId)
            {
                if (materialized.ContainsKey(id) || !IsPresent(g)) continue;
                RecordMaterializedLocked(id, g);
            }
        }
    }

    private void LoadManifest()
    {
        try
        {
            var path = opt.ManifestPath!;
            if (!File.Exists(path)) { log.LogWarning("RomCache manifest not found: {Path}", path); return; }
            manifestMtime = File.GetLastWriteTimeUtc(path);
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            lock (gate)
            {
                byId.Clear();
                foreach (var g in m?.Games ?? new()) byId[g.GameId] = g;
            }
            log.LogInformation("RomCache loaded {Count} JIT game(s) from {Path}", byId.Count, path);
        }
        catch (Exception ex) { log.LogError(ex, "RomCache failed to load manifest"); }
    }

    private void MaybeReloadManifest()
    {
        try
        {
            var path = opt.ManifestPath!;
            if (!File.Exists(path)) return;
            var mt = File.GetLastWriteTimeUtc(path);
            if (mt != manifestMtime) { LoadManifest(); ReconcileFromDisk(); }
        }
        catch { /* keep serving the loaded manifest */ }
    }

    private IEnumerable<string> ListArchiveEntries(string archive)
    {
        // `7z l -ba -slt` lists one block per entry; we want the Path of file (non-directory) entries.
        // Exit 1 is a non-fatal warning (still lists), only 2+ is fatal.
        var (code, outp) = RunSevenZip(new[] { "l", "-ba", "-slt", archive });
        if (code >= 2) yield break;
        string? path = null;
        foreach (var raw in outp.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Path = ")) path = line["Path = ".Length..];
            else if (line.StartsWith("Attributes = "))
            {
                var attr = line["Attributes = ".Length..];
                if (path != null && !attr.Contains('D')) yield return path; // 'D' = directory entry
                path = null;
            }
            else if (line.Length == 0 && path != null)
            {
                // block ended without an Attributes line (rare) — treat as a file
                yield return path;
                path = null;
            }
        }
    }

    private (int code, string output) RunSevenZip(string[] args)
    {
        var psi = NewPsi(args);
        using var p = Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, outp);
    }

    private async Task<(int code, string err)> RunSevenZipAsync(string[] args, CancellationToken ct)
    {
        var psi = NewPsi(args);
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        using var reg = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, opt.ExtractTimeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, reg.Token);
        try { await p.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, await errTask);
    }

    private ProcessStartInfo NewPsi(string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = sevenZip,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static string ResolveSevenZip(string configured)
    {
        if (File.Exists(configured)) return configured;
        return "7z"; // fall back to PATH (7z / 7z.exe)
    }

    private static string Trunc(string s) => s.Length <= 300 ? s : s[..300];
}
