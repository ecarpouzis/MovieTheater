using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MovieTheater.Core;

namespace MovieTheater.ArcadeGateway;

/// <summary>
/// Durable, user-owned arcade saves (docs/arcade-saves-plan.md). CloudRetro names a session's save
/// files after the room id and drops them in the <c>/saves</c> mount; on their own those are ephemeral
/// and room-scoped. This store, which lives on the media host next to that mount, <b>harvests</b> a
/// session's files into a per-user+game area and can <b>seed</b> a chosen save back before the next
/// game boots — so a save belongs to a user, not a room, and survives across sessions.
///
/// <para>Layout under <see cref="SaveStoreOptions.StoreDir"/> (poster pattern — blobs on disk, a tiny
/// JSON sidecar of metadata beside each): <c>&lt;userId&gt;/&lt;gameId&gt;/sram.srm</c> (the canonical
/// in-game/battery save — the portable artifact a future EmuDeck sync keys on) and
/// <c>slot-NNN.dat</c> (save states; <c>slot-000</c> is the auto/"Continue" slot, higher slots are
/// user-named snapshots).</para>
///
/// <para><b>Destructive-safety</b> (global bulk-job rule): every delete is re-verified to live under the
/// store root; retention only ever prunes the oldest <i>unnamed</i> auto-state slots — never a labeled
/// snapshot and never the SRAM. Reads/writes on the <c>/saves</c> mount are confined to the exact
/// <c>&lt;sessionId&gt;.dat/.srm</c> files of the session in question.</para>
/// </summary>
public sealed class SaveStoreOptions
{
    /// <summary>Durable blob root (e.g. D:\ArcadeStorage\savestore).</summary>
    public string? StoreDir { get; set; }

    /// <summary>Host path of CloudRetro's working save dir (same as compose SAVES_DIR, e.g. D:\ArcadeStorage\saves).</summary>
    public string? SavesMountDir { get; set; }

    /// <summary>Soft disk cap for the whole store. Saves are KB–MB so this is a safety net, not a real limit.</summary>
    public long MaxBytes { get; set; } = 100L * 1024 * 1024 * 1024;

    /// <summary>Coalesce autosave-driven harvests within this window (ms).</summary>
    public int HarvestDebounceMs { get; set; } = 1500;

    /// <summary>Max save-state slots kept per (user, game); oldest unnamed auto-slots are pruned past this.</summary>
    public int MaxStatesPerGame { get; set; } = 20;

    public bool Enabled => !string.IsNullOrWhiteSpace(StoreDir) && !string.IsNullOrWhiteSpace(SavesMountDir);
}

/// <summary>Metadata for one stored save (mirrors the app-DB <c>ArcadeSave</c> row; also serialized as
/// the on-disk sidecar). <c>Kind</c> = "sram" | "state"; <c>StorageRelPath</c> is relative to the store root.</summary>
public sealed record SaveMeta(
    int UserId, int GameId, string System, string Kind, int SlotId, string? Label,
    string? CoreName, string? CoreVersion, string StorageRelPath, long SizeBytes, string Sha256,
    string Source, bool IsAutosave, DateTime CreatedUtc, DateTime UpdatedUtc);

public sealed class SaveStore
{
    public const string KindSram = "sram";
    public const string KindState = "state";
    public const int ContinueSlot = 0;

    private readonly SaveStoreOptions opt;
    private readonly ILogger log;
    private readonly string storeRoot;
    private readonly string savesMount;
    private readonly Func<DateTime> now;

    public SaveStore(SaveStoreOptions options, ILogger logger, Func<DateTime>? clock = null)
    {
        opt = options;
        log = logger;
        storeRoot = Path.GetFullPath(opt.StoreDir!);
        savesMount = Path.GetFullPath(opt.SavesMountDir!);
        now = clock ?? (() => DateTime.UtcNow);
        Directory.CreateDirectory(storeRoot);
    }

    // ── Harvest ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Copy a session's save files out of the <c>/saves</c> mount into the user's store: the raw
    /// <c>&lt;sessionId&gt;.srm</c> becomes the canonical SRAM, <c>&lt;sessionId&gt;.dat</c> becomes the
    /// auto/"Continue" state slot. Idempotent — a re-harvest with identical bytes is a no-op (same sha).
    /// Returns metadata for each file actually written (for the caller to mirror into the app DB).
    /// </summary>
    public async Task<IReadOnlyList<SaveMeta>> HarvestSessionAsync(
        int userId, int gameId, string system, string sessionId, bool isAutosave, CancellationToken ct = default)
    {
        var results = new List<SaveMeta>();
        if (string.IsNullOrEmpty(sessionId)) return results;

        var srm = MountFile(sessionId, ".srm");
        if (File.Exists(srm))
        {
            var m = await CopyIntoStoreAsync(userId, gameId, system, KindSram, ContinueSlot, label: null,
                coreName: null, coreVersion: null, src: srm, destName: "sram.srm", isAutosave, ct);
            if (m != null) results.Add(m);
        }

        var dat = MountFile(sessionId, ".dat");
        if (File.Exists(dat))
        {
            var m = await CopyIntoStoreAsync(userId, gameId, system, KindState, ContinueSlot, label: null,
                coreName: null, coreVersion: null, src: dat, destName: SlotFile(ContinueSlot), isAutosave, ct);
            if (m != null) results.Add(m);
        }

        if (results.Count > 0) PruneStates(userId, gameId);
        return results;
    }

    // Per-session-file mtime last harvested, so a sweep only copies changed files.
    private readonly Dictionary<string, long> lastSwept = new();

    /// <summary>True if CloudRetro has already written a save file for this session in the mount (so the
    /// room is live / was booted) — the gateway uses this to avoid re-seeding over a running session.</summary>
    public bool MountHasSave(string sessionId) =>
        !string.IsNullOrEmpty(sessionId) &&
        (File.Exists(MountFile(sessionId, ".dat")) || File.Exists(MountFile(sessionId, ".srm")));

    /// <summary>
    /// One harvest sweep: scan the <c>/saves</c> mount for OUR deterministic-id save files and copy any
    /// that changed since the last sweep into the owning user's store. Run on a timer by the gateway —
    /// this captures periodic autosaves, the save-on-close flush, and an unclean disconnect alike,
    /// independent of the signaling connection's lifetime (which ends before CloudRetro's room reap).
    /// The (user, game, system) come from the id itself (minted by the site), so no DB lookup is needed.
    /// </summary>
    /// <param name="mirror">Called with each harvested file's metadata to mirror it into the app DB;
    /// returns true on a confirmed write. A session's mtime is only marked swept when ALL its files
    /// mirror successfully, so a failed mirror (e.g. the site mid-deploy) is retried on the next sweep
    /// instead of being silently dropped. The file itself is always copied into the store (resume works
    /// regardless of the DB).</param>
    public async Task<int> HarvestMountChangesAsync(Func<SaveMeta, Task<bool>> mirror, CancellationToken ct = default)
    {
        int harvested = 0;
        if (!Directory.Exists(savesMount)) return harvested;

        // Group the mount's .dat/.srm by session id, keep only our ids.
        var sessions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(savesMount))
        {
            var ext = Path.GetExtension(f);
            if (ext != ".dat" && ext != ".srm") continue;
            var sessionId = Path.GetFileNameWithoutExtension(f);
            if (!ArcadeSaveId.Is(sessionId)) continue;
            long m = FileMtime(f);
            sessions[sessionId] = sessions.TryGetValue(sessionId, out var cur) ? Math.Max(cur, m) : m;
        }

        foreach (var (sessionId, mtime) in sessions)
        {
            if (lastSwept.TryGetValue(sessionId, out var seen) && seen >= mtime) continue;
            if (!ArcadeSaveId.TryParse(sessionId, out var userId, out var gameId, out _, out var system, out _)) continue;
            try
            {
                var written = await HarvestSessionAsync(userId, gameId, system, sessionId, isAutosave: true, ct);
                bool allMirrored = true;
                foreach (var m in written)
                    if (!await mirror(m)) allMirrored = false;
                // Only remember this mtime as done once the DB is in sync — else retry next sweep.
                if (allMirrored) lastSwept[sessionId] = mtime;
                harvested += written.Count;
            }
            catch (Exception ex) { log.LogWarning(ex, "SaveStore harvest sweep failed for {Session}", sessionId); }
        }
        return harvested;
    }

    private static long FileMtime(string f) { try { return new FileInfo(f).LastWriteTimeUtc.Ticks; } catch { return 0; } }

    // ── Seed / clear (used by the resume flow, S2) ────────────────────────────────────────────────

    /// <summary>
    /// Seed a chosen stored slot back into the mount as <c>&lt;sessionId&gt;.dat</c> (+ the canonical
    /// <c>.srm</c>) so CloudRetro auto-restores it when the game boots. Returns false if the slot has no
    /// stored blob (caller then boots fresh).
    /// </summary>
    public bool SeedSession(int userId, int gameId, string sessionId, int slotId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        bool seeded = false;

        var stateBlob = StoreFile(userId, gameId, SlotFile(slotId));
        if (File.Exists(stateBlob)) { CopyGuarded(stateBlob, MountFile(sessionId, ".dat")); seeded = true; }

        var sramBlob = StoreFile(userId, gameId, "sram.srm");
        if (File.Exists(sramBlob)) { CopyGuarded(sramBlob, MountFile(sessionId, ".srm")); seeded = true; }

        return seeded;
    }

    /// <summary>Remove any stale mount files for a session so a "New game" boots clean (HasSave=false).</summary>
    public void ClearSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        foreach (var ext in new[] { ".dat", ".srm" })
        {
            var f = MountFile(sessionId, ext);
            TryDeleteUnder(savesMount, f);
        }
    }

    // ── Listing ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>All stored saves for a (user, game), newest first — the source for the resume dropdown.</summary>
    public IReadOnlyList<SaveMeta> ListSaves(int userId, int gameId)
    {
        var dir = GameDir(userId, gameId);
        var list = new List<SaveMeta>();
        if (!Directory.Exists(dir)) return list;
        foreach (var side in Directory.EnumerateFiles(dir, "*.json"))
        {
            var m = ReadSidecar(side);
            if (m != null) list.Add(m);
        }
        return list.OrderByDescending(m => m.UpdatedUtc).ToList();
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────────

    private static string SlotFile(int slot) => $"slot-{slot:D3}.dat";

    private string MountFile(string sessionId, string ext) =>
        Path.GetFullPath(Path.Combine(savesMount, sessionId + ext));

    private string GameDir(int userId, int gameId) =>
        Path.GetFullPath(Path.Combine(storeRoot, userId.ToString(), gameId.ToString()));

    private string StoreFile(int userId, int gameId, string name) =>
        Path.GetFullPath(Path.Combine(GameDir(userId, gameId), name));

    private async Task<SaveMeta?> CopyIntoStoreAsync(
        int userId, int gameId, string system, string kind, int slotId, string? label,
        string? coreName, string? coreVersion, string src, string destName, bool isAutosave, CancellationToken ct)
    {
        var dir = GameDir(userId, gameId);
        Directory.CreateDirectory(dir);
        var dest = Path.GetFullPath(Path.Combine(dir, destName));
        if (!IsUnder(storeRoot, dest)) throw new InvalidOperationException($"refusing to write outside store: {dest}");

        byte[] bytes = await File.ReadAllBytesAsync(src, ct);
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        // Idempotent: identical bytes already stored → just refresh the timestamp, no rewrite.
        var existing = ReadSidecar(SidecarPath(dest));
        var created = existing?.CreatedUtc ?? now();
        if (existing != null && existing.Sha256 == sha && File.Exists(dest))
            return null;

        await File.WriteAllBytesAsync(dest, bytes, ct);
        var meta = new SaveMeta(userId, gameId, system, kind, slotId, label ?? existing?.Label,
            coreName, coreVersion, RelPath(dest), bytes.LongLength, sha, "online", isAutosave, created, now());
        WriteSidecar(dest, meta);
        EnforceCap();
        return meta;
    }

    private void PruneStates(int userId, int gameId)
    {
        var dir = GameDir(userId, gameId);
        if (!Directory.Exists(dir)) return;
        // Candidates = state slots with NO label (auto slots). Keep the newest MaxStatesPerGame; never the
        // Continue slot (0), never a labeled snapshot, never SRAM.
        var autos = Directory.EnumerateFiles(dir, "slot-*.dat")
            .Select(f => (file: f, meta: ReadSidecar(SidecarPath(f))))
            .Where(x => x.meta != null && x.meta!.Kind == KindState
                        && string.IsNullOrEmpty(x.meta.Label) && x.meta.SlotId != ContinueSlot)
            .OrderByDescending(x => x.meta!.UpdatedUtc)
            .ToList();
        foreach (var (file, _) in autos.Skip(Math.Max(0, opt.MaxStatesPerGame)))
        {
            TryDeleteUnder(storeRoot, file);
            TryDeleteUnder(storeRoot, SidecarPath(file));
        }
    }

    // Soft global cap: if the store is over budget, drop the oldest unnamed auto-state slots (never SRAM,
    // never a labeled snapshot, never the Continue slot). Best-effort; logs if it can't get under.
    private void EnforceCap()
    {
        long total = DirSize(storeRoot);
        if (total <= opt.MaxBytes) return;
        var victims = EnumerateSidecars()
            .Where(m => m.Kind == KindState && string.IsNullOrEmpty(m.Label) && m.SlotId != ContinueSlot)
            .OrderBy(m => m.UpdatedUtc)
            .ToList();
        foreach (var m in victims)
        {
            if (total <= opt.MaxBytes) break;
            var blob = Path.GetFullPath(Path.Combine(storeRoot, m.StorageRelPath));
            long freed = FileLen(blob);
            TryDeleteUnder(storeRoot, blob);
            TryDeleteUnder(storeRoot, SidecarPath(blob));
            total -= freed;
        }
        if (total > opt.MaxBytes)
            log.LogWarning("SaveStore over cap ({Have} > {Cap}); remaining saves are SRAM/labeled (kept).",
                total, opt.MaxBytes);
    }

    private IEnumerable<SaveMeta> EnumerateSidecars()
    {
        if (!Directory.Exists(storeRoot)) yield break;
        foreach (var side in Directory.EnumerateFiles(storeRoot, "*.json", SearchOption.AllDirectories))
        {
            var m = ReadSidecar(side);
            if (m != null) yield return m;
        }
    }

    private void CopyGuarded(string src, string dest)
    {
        var full = Path.GetFullPath(dest);
        if (!IsUnder(savesMount, full)) throw new InvalidOperationException($"refusing to seed outside mount: {full}");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.Copy(src, full, overwrite: true);
    }

    private void TryDeleteUnder(string root, string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!IsUnder(root, full) || !File.Exists(full)) return;
            File.Delete(full);
        }
        catch (Exception ex) { log.LogWarning(ex, "SaveStore could not delete {File}", path); }
    }

    private static string SidecarPath(string blobPath) => blobPath + ".json";

    private void WriteSidecar(string blobPath, SaveMeta meta) =>
        File.WriteAllText(SidecarPath(blobPath), JsonSerializer.Serialize(meta));

    private SaveMeta? ReadSidecar(string sidecarPath)
    {
        try
        {
            if (!File.Exists(sidecarPath)) return null;
            return JsonSerializer.Deserialize<SaveMeta>(File.ReadAllText(sidecarPath));
        }
        catch { return null; }
    }

    private string RelPath(string full) => Path.GetRelativePath(storeRoot, full).Replace('\\', '/');

    private static bool IsUnder(string root, string fullPath)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(r, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static long FileLen(string f) { try { return new FileInfo(f).Length; } catch { return 0; } }

    private static long DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            total += FileLen(f);
        return total;
    }
}
