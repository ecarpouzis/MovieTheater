using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
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

/// <summary>Body for the site→gateway blob ops (delete/relabel/read). Web JSON binds camelCase.</summary>
public class SaveOpReq
{
    public int UserId { get; set; }
    public int GameId { get; set; }
    public string? Kind { get; set; }
    public int Slot { get; set; }
    public string? Label { get; set; }
}

/// <summary>Body for the site→gateway import op (base64 blob → a stored save).</summary>
public sealed class SaveImportReq : SaveOpReq
{
    public string? System { get; set; }
    public string? DataBase64 { get; set; }
}

public sealed class SaveStore
{
    public const string KindSram = "sram";
    public const string KindState = "state";
    // A "coresave" is a whole SAVE-DIRECTORY tree the core writes itself (PSP memory stick, Dreamcast/
    // Naomi VMU, DOS) instead of exposing it via RETRO_MEMORY_SAVE_RAM — so it never becomes a .srm and
    // the flat harvest can't see it. With uniqueSaveDir the worker scopes that tree to
    // <savesMount>/coresaves/<sessionId>/; we tar the subtree into one blob per (user, game) and extract
    // it back on seed. Persistence rides the deterministic session id, no DB row required.
    public const string KindCoreSave = "coresave";
    public const string CoreSaveBlob = "coresave.tar";
    /// <summary>The rolling "where you left off" slot. The MACHINE owns it: autosave and save-on-quit
    /// write it, and it is expected to be overwritten every session.</summary>
    public const int ContinueSlot = 0;

    /// <summary>The QUICKSAVE slot — what the in-room Save button writes and Load reads. The PLAYER owns
    /// it: nothing automatic ever touches it.
    ///
    /// Save used to write <see cref="ContinueSlot"/>, which meant save-on-quit (an unconditional
    /// re-serialize on every room close) silently overwrote a deliberate save with whatever state the
    /// game happened to be in when you left — save before the secret level, die, exit, and the save is
    /// the death. Autosave would have done it every 60s. Deliberate and automatic saves must not share
    /// a slot. Numbered far above <c>NextSnapshotSlot</c>'s range (MaxStatesPerGame = 20) so it can
    /// never collide with a named snapshot.</summary>
    public const int QuickSlot = 99;

    private readonly SaveStoreOptions opt;
    private readonly ILogger log;
    private readonly string storeRoot;
    private readonly string savesMount;
    private readonly Func<DateTime> now;

    /// <summary>Session ids currently running as COMPETITIVE rooms. A competitive run must NOT vault its
    /// save-STATE over the player's casual "Continue" slot (that would let the run overwrite — and later
    /// resume from — their real progress, defeating the whole point and RA hardcore). So harvest skips the
    /// <c>.dat</c> for these sessions and keeps only the <c>.srm</c> (battery/card, legit progress). The
    /// mark is set/cleared at boot from <c>?competitive=1</c> (a casual boot of the same deterministic id
    /// clears it), and read from the background sweep thread — hence concurrent.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> competitiveSessions = new(StringComparer.Ordinal);

    /// <summary>Mark (or unmark) a session as a competitive room. Called at boot from the connect handler.</summary>
    public void SetCompetitive(string sessionId, bool competitive)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (competitive) competitiveSessions[sessionId] = true;
        else competitiveSessions.TryRemove(sessionId, out _);
    }

    /// <summary>Whether a session is currently a competitive room (harvest skips its save-state).</summary>
    public bool IsCompetitive(string sessionId) =>
        !string.IsNullOrEmpty(sessionId) && competitiveSessions.ContainsKey(sessionId);

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
        if (File.Exists(srm) && IsSettled(srm))
        {
            var m = await CopyIntoStoreAsync(userId, gameId, system, KindSram, ContinueSlot, label: null,
                coreName: null, coreVersion: null, src: srm, destName: "sram.srm", isAutosave, ct);
            if (m != null) results.Add(m);
        }

        // A competitive room's save-STATE is never vaulted — it must not overwrite the casual Continue
        // slot (that would let a competitive run clobber, and later be resumed as, the player's real
        // progress). Only its .srm (battery/card) is kept. See competitiveSessions.
        var dat = MountFile(sessionId, ".dat");
        if (File.Exists(dat) && IsSettled(dat) && !IsCompetitive(sessionId))
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
            // Still being written (close-save/autosave in flight): skip WITHOUT marking swept, so the
            // next sweep retries once the writer is done. Harvesting mid-write once vaulted a torn
            // save that then re-seeded every boot (Snowboard Kids, 2026-07-10).
            if (now().Ticks - mtime < TimeSpan.FromMilliseconds(opt.HarvestDebounceMs).Ticks) continue;
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

        // Also harvest core save-DIRECTORY trees (PSP/DC/Naomi/DOS) under coresaves/<sessionId>/. These are
        // NOT mirrored to the app DB (there's no slot to list); resume rides the deterministic id off disk.
        if (Directory.Exists(CoreSavesRoot))
        {
            foreach (var sdir in Directory.EnumerateDirectories(CoreSavesRoot))
            {
                var sessionId = Path.GetFileName(sdir);
                if (!ArcadeSaveId.Is(sessionId)) continue;
                long mtime = NewestMtime(sdir);
                var key = "core:" + sessionId;
                if (lastSwept.TryGetValue(key, out var seen) && seen >= mtime) continue;
                if (!ArcadeSaveId.TryParse(sessionId, out var userId, out var gameId, out _, out var system, out _)) continue;
                try
                {
                    var m = await HarvestCoreSaveDirAsync(userId, gameId, system, sessionId, ct);
                    lastSwept[key] = mtime; // disk write is the source of truth for resume; mark done on success
                    if (m != null) harvested++;
                }
                catch (Exception ex) { log.LogWarning(ex, "SaveStore core-save harvest failed for {Session}", sessionId); }
            }
        }
        return harvested;
    }

    private static long FileMtime(string f) { try { return new FileInfo(f).LastWriteTimeUtc.Ticks; } catch { return 0; } }

    /// <summary>A mount file is "settled" once its last write is older than the debounce window — i.e.
    /// the emulator's close-save/autosave writer is plausibly done with it. Guards every harvest read
    /// against copying a half-written file into the vault.</summary>
    private bool IsSettled(string f)
    {
        try { return now() - File.GetLastWriteTimeUtc(f) >= TimeSpan.FromMilliseconds(opt.HarvestDebounceMs); }
        catch { return false; }
    }

    private static long NewestMtime(string dir)
    {
        long m = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) m = Math.Max(m, FileMtime(f));
        return m;
    }

    // ── Seed / clear (used by the resume flow, S2) ────────────────────────────────────────────────

    /// <summary>
    /// Seed a chosen stored slot back into the mount as <c>&lt;sessionId&gt;.dat</c> (+ the canonical
    /// <c>.srm</c>) so CloudRetro auto-restores it when the game boots. Returns false if the slot has no
    /// stored blob (caller then boots fresh).
    /// </summary>
    public bool SeedSession(int userId, int gameId, string sessionId, int slotId) =>
        SeedSession(userId, gameId, sessionId, slotId, requireSystem: null, out _);

    /// <inheritdoc cref="SeedSession(int,int,string,int)"/>
    /// <param name="requireSystem">The room's system string from its id (e.g. "n64-parallel_n64"). When
    /// given, a stored STATE captured on a DIFFERENT system is not seeded — a save-state is a dump of one
    /// core's memory, so feeding a parallel_n64 state to the stock n64 core (or vice versa) restores
    /// garbage or nothing at all, and doing it silently overwrites a perfectly good mount. The vault is
    /// keyed by (user, game) but a state's compatibility is keyed by CORE, and the two do not line up:
    /// slots are shared across every core the game has ever been played on. The <c>.srm</c> is seeded
    /// either way — a battery/memory card is the GAME's data and reads the same on any core.</param>
    /// <param name="stateSkipped">True when a state blob existed but was withheld for the reason above,
    /// so the caller can say so in the log rather than looking like it simply found nothing.</param>
    public bool SeedSession(int userId, int gameId, string sessionId, int slotId, string? requireSystem, out bool stateSkipped)
    {
        stateSkipped = false;
        if (string.IsNullOrEmpty(sessionId)) return false;
        bool seeded = false;

        var stateBlob = StoreFile(userId, gameId, SlotFile(slotId));
        if (File.Exists(stateBlob))
        {
            var storedSystem = ReadSidecar(SidecarPath(stateBlob))?.System;
            // Only withhold on a KNOWN mismatch: a sidecar-less or blank-system blob predates this and is
            // seeded as before.
            if (!string.IsNullOrEmpty(requireSystem) && !string.IsNullOrEmpty(storedSystem)
                && !string.Equals(storedSystem, requireSystem, StringComparison.Ordinal))
            {
                stateSkipped = true;
                log.LogWarning(
                    "SaveStore not seeding slot {Slot} for user {User} game {Game}: it was saved on {Stored} but this room runs {Room}.",
                    slotId, userId, gameId, storedSystem, requireSystem);
            }
            else
            {
                CopyGuarded(stateBlob, MountFile(sessionId, ".dat"));
                seeded = true;
            }
        }

        var sramBlob = StoreFile(userId, gameId, "sram.srm");
        if (File.Exists(sramBlob)) { CopyGuarded(sramBlob, MountFile(sessionId, ".srm")); seeded = true; }

        return seeded;
    }

    /// <summary>Seed ONLY the SAVE_RAM memory card / battery (<c>sram.srm</c>) into the mount — never the
    /// save-STATE. This is the "New game" path: a new game skips the auto-restored save-state but must NOT
    /// eject the player's memory card. The card is a distinct persistence layer from a save-state (on real
    /// hardware, starting a new game never wipes the card), and for card-only titles (PS1 SotN) it IS the
    /// save. PS1 card 1 and NES/SNES/GBA/N64 battery all ride SAVE_RAM, so this covers them uniformly.
    /// Erasing the card stays a deliberate action in Manage-my-saves, never a side effect of New game.
    /// Returns false when the user has no stored card for this game (nothing to seed).</summary>
    public bool SeedSramOnly(int userId, int gameId, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        var sramBlob = StoreFile(userId, gameId, "sram.srm");
        if (!File.Exists(sramBlob)) return false;
        CopyGuarded(sramBlob, MountFile(sessionId, ".srm"));
        return true;
    }

    // ── Snapshots (S3): named save-state slots the user creates from the live game ────────────────────

    /// <summary>How long an in-room Save/Snapshot copy waits for the worker's flushed save-state file to
    /// appear before giving up. The client fires a SAVE (t=106) just before calling us, but a cold-boot
    /// <c>retro_serialize</c>+write can lag its fixed pre-wait — and a never-saved game has no seeded
    /// <c>.dat</c> to fall back on. Polling here, instead of trusting a single glance, is what stops the
    /// spurious "no live save yet" on the FIRST save of a freshly-booted game; a warm game's <c>.dat</c>
    /// already exists and returns on the first check with no added latency.</summary>
    private const int SaveFlushWaitMs = 4000;

    /// <summary>Copy the session's CURRENT live state (<c>&lt;sessionId&gt;.dat</c> in the mount) into a
    /// NEW numbered snapshot slot (≥1) with the user's label. The caller should have the client flush a
    /// SAVE (t=106) first so the .dat is current. Returns the new slot's metadata (to mirror into the DB),
    /// or null if no live state lands within <see cref="SaveFlushWaitMs"/>.</summary>
    public Task<SaveMeta?> SnapshotCurrentAsync(
        int userId, int gameId, string system, string sessionId, string? label, CancellationToken ct = default) =>
        SnapshotToSlotAsync(userId, gameId, system, sessionId, NextSnapshotSlot(userId, gameId), label, ct);

    /// <summary>Copy the session's live state into a SPECIFIC slot, replacing whatever is there. This is
    /// how the in-room Save button writes the <see cref="QuickSlot"/>: a deliberate save must land in a
    /// slot the machine never writes. Waits up to <see cref="SaveFlushWaitMs"/> for the client's t=106
    /// flush to reach disk before concluding there's nothing to copy.</summary>
    public async Task<SaveMeta?> SnapshotToSlotAsync(
        int userId, int gameId, string system, string sessionId, int slot, string? label, CancellationToken ct = default)
    {
        var dat = MountFile(sessionId, ".dat");
        if (!await WaitForFileAsync(dat, SaveFlushWaitMs, ct)) return null;
        return await CopyIntoStoreAsync(userId, gameId, system, KindState, slot, label,
            coreName: null, coreVersion: null, src: dat, destName: SlotFile(slot), isAutosave: false, ct);
    }

    /// <summary>Poll for a mount file to appear, up to <paramref name="timeoutMs"/>, returning true as soon
    /// as it exists. Bridges the gap between the client's SAVE (t=106) and the worker's write landing on
    /// disk. When the file only shows up mid-poll it was JUST written, so a short settle lets the worker
    /// close it before <see cref="CopyIntoStoreAsync"/> reads it (a file already present on the first check
    /// is the settled warm case and returns immediately).</summary>
    private static async Task<bool> WaitForFileAsync(string path, int timeoutMs, CancellationToken ct)
    {
        const int stepMs = 150;
        int waited = 0;
        while (true)
        {
            if (File.Exists(path))
            {
                if (waited > 0) { try { await Task.Delay(200, ct); } catch { /* copy will retry-or-fail cleanly */ } }
                return true;
            }
            if (waited >= timeoutMs || ct.IsCancellationRequested) return false;
            try { await Task.Delay(stepMs, ct); } catch { return false; }
            waited += stepMs;
        }
    }

    /// <summary>The system+core a stored STATE slot was captured on ("n64-parallel_n64"), or null when the
    /// slot has no blob/sidecar. Callers use it to refuse a cross-core restore — see SeedSession's
    /// requireSystem for why a state is only ever valid on the core that wrote it.</summary>
    public string? StateSystem(int userId, int gameId, int slotId)
    {
        var blob = StoreFile(userId, gameId, SlotFile(slotId));
        return File.Exists(blob) ? ReadSidecar(SidecarPath(blob))?.System : null;
    }

    /// <summary>Swap a stored slot's bytes into the live mount <c>&lt;sessionId&gt;.dat</c> so an in-room
    /// LOAD (t=107) restores it without restarting the room. Returns false if the slot has no blob.</summary>
    public bool LoadSlotToMount(int userId, int gameId, string sessionId, int slot)
    {
        var blob = StoreFile(userId, gameId, SlotFile(slot));
        if (!File.Exists(blob)) return false;
        CopyGuarded(blob, MountFile(sessionId, ".dat"));
        return true;
    }

    // ── Core save-directory trees (PSP memstick / DC-Naomi VMU / DOS) ─────────────────────────────────

    /// <summary>The shared mount subdir the workers point uniqueSaveDir at: coresaves/&lt;sessionId&gt;/.</summary>
    private string CoreSavesRoot => Path.GetFullPath(Path.Combine(savesMount, "coresaves"));
    private string CoreSaveSessionDir(string sessionId) =>
        Path.GetFullPath(Path.Combine(CoreSavesRoot, sessionId));

    /// <summary>True if a session's core-save dir exists and holds at least one file (so the room booted a
    /// save-dir core and something was written) — used to skip empty/never-saved sessions in the sweep.</summary>
    private static bool DirHasFiles(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();

    /// <summary>
    /// Harvest a session's core save-DIRECTORY tree (coresaves/&lt;sessionId&gt;/**) into the user's store as
    /// a single tar blob (coresave.tar) + sidecar. Idempotent: a content digest over the tree (sorted
    /// relative paths + file bytes, mtimes excluded) is stored in the sidecar's sha, so an unchanged tree
    /// re-harvests to nothing. Returns the metadata to mirror, or null when there's nothing new.
    /// </summary>
    public async Task<SaveMeta?> HarvestCoreSaveDirAsync(
        int userId, int gameId, string system, string sessionId, CancellationToken ct = default)
    {
        var src = CoreSaveSessionDir(sessionId);
        if (!DirHasFiles(src)) return null;

        var (digest, totalBytes) = TreeContentDigest(src);
        var dir = GameDir(userId, gameId);
        var dest = Path.GetFullPath(Path.Combine(dir, CoreSaveBlob));
        if (!IsUnder(storeRoot, dest)) throw new InvalidOperationException($"refusing to write outside store: {dest}");

        var existing = ReadSidecar(SidecarPath(dest));
        if (existing != null && existing.Sha256 == digest && File.Exists(dest)) return null; // unchanged tree

        Directory.CreateDirectory(dir);
        // Tar the subtree to a temp file first, then atomically move into place (a sweep never reads a
        // half-written blob; a crash leaves the previous good blob intact).
        var tmp = dest + ".tmp";
        try
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            await using (var fs = File.Create(tmp))
                await TarFile.CreateFromDirectoryAsync(src, fs, includeBaseDirectory: false, ct);
            File.Move(tmp, dest, overwrite: true);
        }
        finally { if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* */ } } }

        var created = existing?.CreatedUtc ?? now();
        var meta = new SaveMeta(userId, gameId, system, KindCoreSave, ContinueSlot, existing?.Label,
            null, null, RelPath(dest), totalBytes, digest, "online", true, created, now());
        WriteSidecar(dest, meta);
        EnforceCap();
        return meta;
    }

    /// <summary>Extract the user's stored core-save tree back into coresaves/&lt;sessionId&gt;/ before the room
    /// boots, so the core (PSP/DC/…) restores the player's memory stick / VMU. False if nothing stored.</summary>
    public bool SeedCoreSaveDir(int userId, int gameId, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        var blob = StoreFile(userId, gameId, CoreSaveBlob);
        if (!File.Exists(blob)) return false;

        var dest = CoreSaveSessionDir(sessionId);
        if (!IsUnder(CoreSavesRoot, dest)) throw new InvalidOperationException($"refusing to seed outside mount: {dest}");
        Directory.CreateDirectory(dest);
        // TarFile.ExtractToDirectory rejects entries that escape the destination (path traversal), and we
        // overwrite so a re-seed refreshes; dest is re-verified under the coresaves mount above.
        TarFile.ExtractToDirectory(blob, dest, overwriteFiles: true);
        return true;
    }

    /// <summary>Remove a session's core-save mount dir (a "New game" boots clean; also cleans up on close).</summary>
    public void ClearCoreSaveDir(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var dir = CoreSaveSessionDir(sessionId);
        try
        {
            if (IsUnder(CoreSavesRoot, dir) && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) { log.LogWarning(ex, "SaveStore could not clear core-save dir {Dir}", dir); }
    }

    /// <summary>Content digest of a directory tree: SHA-256 over each file's relative path + bytes in
    /// sorted order (mtimes and tar framing excluded), so identical content across sweeps hashes the same.</summary>
    private static (string sha, long bytes) TreeContentDigest(string dir)
    {
        var rels = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        using var sha = SHA256.Create();
        long total = 0;
        foreach (var rel in rels)
        {
            var relBytes = Encoding.UTF8.GetBytes(rel + "\0");
            sha.TransformBlock(relBytes, 0, relBytes.Length, null, 0);
            var content = File.ReadAllBytes(Path.Combine(dir, rel));
            total += content.LongLength;
            sha.TransformBlock(content, 0, content.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return (Convert.ToHexString(sha.Hash!).ToLowerInvariant(), total);
    }

    // ── My Saves management (S3): delete / relabel / read / import ────────────────────────────────────

    /// <summary>Delete a stored save (blob + sidecar) for a (user, game, kind, slot), AND its live-mount
    /// counterpart. The mount copy must go too: the deterministic session id auto-restores any leftover
    /// <c>.dat</c> at the next boot, and the harvest sweep would copy it straight back into the store —
    /// so a store-only delete silently resurrected the save (Snowboard Kids, 2026-07-10). Only the
    /// Continue-slot state / SRAM have mount counterparts (mount ids are always slot 0); snapshot slots
    /// live only in the store. Slot 0 SRAM/Continue is deletable too (it's the user's data).</summary>
    public bool DeleteSave(int userId, int gameId, string kind, int slot)
    {
        var name = BlobName(kind, slot);
        var blob = StoreFile(userId, gameId, name);
        bool existed = File.Exists(blob);
        TryDeleteUnder(storeRoot, blob);
        TryDeleteUnder(storeRoot, SidecarPath(blob));

        // dirzip (heavy lane) never touches the CloudRetro saves mount — its live copy is the
        // emulator's own save dir, which stays untouched on vault delete (never-clobber).
        if (kind != "dirzip" && (kind == KindSram || slot == ContinueSlot))
        {
            var ext = kind == KindSram ? ".srm" : ".dat";
            foreach (var f in MountFilesFor(userId, gameId, ext))
                TryDeleteUnder(savesMount, f);
        }
        return existed;
    }

    /// <summary>This (user, game)'s save files in the live mount, by extension. The session id embeds the
    /// game key, which the store doesn't know — so match by parsing each candidate's id instead of
    /// reconstructing the filename.</summary>
    private IEnumerable<string> MountFilesFor(int userId, int gameId, string ext)
    {
        if (!Directory.Exists(savesMount)) yield break;
        foreach (var f in Directory.EnumerateFiles(savesMount, "sv-*" + ext))
            if (ArcadeSaveId.TryParse(Path.GetFileNameWithoutExtension(f), out var u, out var g, out _, out _, out _)
                && u == userId && g == gameId)
                yield return f;
    }

    /// <summary>Rename a stored save's label (updates the sidecar so a future re-list stays consistent).</summary>
    public bool RelabelSave(int userId, int gameId, string kind, int slot, string? label)
    {
        var name = BlobName(kind, slot);
        var blob = StoreFile(userId, gameId, name);
        var meta = ReadSidecar(SidecarPath(blob));
        if (meta == null) return false;
        WriteSidecar(blob, meta with { Label = label, UpdatedUtc = now() });
        return true;
    }

    /// <summary>Read a stored save's raw bytes (for a tokened download / export). Null if missing.</summary>
    public async Task<byte[]?> ReadSaveAsync(int userId, int gameId, string kind, int slot, CancellationToken ct = default)
    {
        var name = BlobName(kind, slot);
        var blob = StoreFile(userId, gameId, name);
        if (!IsUnder(storeRoot, blob) || !File.Exists(blob)) return null;
        return await File.ReadAllBytesAsync(blob, ct);
    }

    /// <summary>Import raw bytes as a save (upload / EmuDeck). Writes the blob + sidecar into a slot and
    /// returns its metadata to mirror into the DB. For SRAM the destName is the canonical sram.srm.</summary>
    public async Task<SaveMeta> ImportSaveAsync(
        int userId, int gameId, string system, string kind, int slot, string? label, byte[] bytes, CancellationToken ct = default)
    {
        var dir = GameDir(userId, gameId);
        Directory.CreateDirectory(dir);
        var destName = BlobName(kind, slot);
        var dest = Path.GetFullPath(Path.Combine(dir, destName));
        if (!IsUnder(storeRoot, dest)) throw new InvalidOperationException($"refusing to write outside store: {dest}");
        await File.WriteAllBytesAsync(dest, bytes, ct);
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var meta = new SaveMeta(userId, gameId, system, kind, slot, label, null, null, RelPath(dest),
            bytes.LongLength, sha, "imported", false, now(), now());
        WriteSidecar(dest, meta);
        EnforceCap();
        return meta;
    }

    /// <summary>The next free snapshot slot for a (user, game): max existing state slot + 1, never below 1
    /// (slot 0 is the auto "Continue" slot).</summary>
    public int NextSnapshotSlot(int userId, int gameId)
    {
        var dir = GameDir(userId, gameId);
        int max = ContinueSlot;
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "slot-*.dat"))
            {
                var name = Path.GetFileNameWithoutExtension(f); // "slot-NNN"
                if (name.Length > 5 && int.TryParse(name.Substring(5), out var n)) max = Math.Max(max, n);
            }
        return max + 1;
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

    /// <summary>Blob filename for a (kind, slot). "sram" and "dirzip" (heavy-lane directory saves,
    /// HeavyVault) are single-slot canonical files; "state" snapshots are per-slot. Shared by
    /// read/delete/import so every path speaks the same layout — including HeavyVault's writes.</summary>
    internal static string BlobName(string kind, int slot) =>
        kind == KindSram ? "sram.srm" : kind == "dirzip" ? "dirzip.zip" : SlotFile(slot);

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
