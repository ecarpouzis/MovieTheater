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
    public sealed record ManifestGame(int GameId, string GameKey, string System, string Folder, string Archive, string[]? Exts = null);
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
    public async Task EnsureMaterializedAsync(int gameId, CancellationToken ct = default)
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
                Touch(gameId, g);
                return;
            }

            await extractSem.WaitAsync(ct);
            try
            {
                await ExtractAsync(g, ct);
            }
            finally { extractSem.Release(); }

            RecordMaterialized(gameId, g);
            Evict();
        }
        finally { gameLock.Release(); }
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

    private async Task ExtractAsync(ManifestGame g, CancellationToken ct)
    {
        if (!File.Exists(g.Archive))
            throw new FileNotFoundException($"source archive missing: {g.Archive}");

        var dest = FolderDest(g);
        Directory.CreateDirectory(dest);
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
        var result = new List<string>();
        var dest = FolderDest(g);
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
