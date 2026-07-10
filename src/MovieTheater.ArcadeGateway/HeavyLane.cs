using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MovieTheater.ArcadeGateway;

/// <summary>
/// Heavy lane (docs/arcade-heavy-lane-plan.md): Moonlight/Apollo-streamed emulators (Switch/PS3/PS4/
/// Wii U/X360) as first-class arcade catalog entries. The gateway is the heavy lane's control plane
/// on Ziggy: it owns the HeavyApp descriptor registry (§4), the one-session-at-a-time lock (§7.4),
/// the pre-staged big-title cache (§5, <see cref="HeavyStager"/>), and the only channel to Apollo's
/// admin API (§7.2/7.3, <see cref="ApolloAdmin"/>). The k8s site pod reaches all of it through
/// secret-gated /heavy/* endpoints — it can never talk to Apollo (localhost:47990) directly.
///
/// <para>Descriptors are one JSON file per app under <see cref="HeavyOptions.AppsDir"/> — they carry
/// personal filesystem paths, so they live on Ziggy and are NEVER committed to the repo
/// (no-name-or-fs-details-in-code). Adding a title = writing one file; the registry hot-reloads.</para>
/// </summary>
public sealed class HeavyOptions
{
    /// <summary>Directory of HeavyApp descriptor JSONs (e.g. D:\ArcadeStorage\heavy\apps).</summary>
    public string? AppsDir { get; set; }

    /// <summary>Root of the pre-staged big-title cache (plan §5, e.g. E:\Games\_heavycache).</summary>
    public string? CacheDir { get; set; }

    /// <summary>Disk cap for the heavy cache tier. Default 300 GB. When exceeded the stager REFUSES
    /// new work with a clear error — it never auto-deletes (heavy titles are 5–45 GB each and hand
    /// curated; eviction stays a deliberate human/admin act, per the destructive-bulk house rule).</summary>
    public long CacheMaxBytes { get; set; } = 300L * 1024 * 1024 * 1024;

    /// <summary>Bytes copied per stage call. The caller (card UI / admin loop) drives the copy to
    /// completion one bounded chunk at a time — never one giant call (bulk-job house rule).</summary>
    public long ChunkBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Apollo admin API (https://localhost:47990). Creds from the Apollo web UI login.</summary>
    public string ApolloBaseUrl { get; set; } = "https://localhost:47990";
    public string? ApolloUser { get; set; }
    public string? ApolloPassword { get; set; }

    /// <summary>The launch-contract script compiled into every synced Apollo app's cmd
    /// (plan §4: prepare → run → finish). Absent = sync emits the raw emulator command line.</summary>
    public string? LaunchScript { get; set; }

    /// <summary>A prepare lock with no live process attached auto-expires after this long, so a
    /// launch script that died before attaching its PID can't wedge the lane (§7.4 watchdog).</summary>
    public int StaleLockMinutes { get; set; } = 15;

    public bool Enabled => !string.IsNullOrWhiteSpace(AppsDir);
}

/// <summary>One streamable app (plan §4). Deserialized from <c>&lt;AppsDir&gt;\&lt;id&gt;.json</c>.</summary>
public sealed class HeavyApp
{
    public string Id { get; set; } = default!;
    public string Title { get; set; } = default!;
    /// <summary>Catalog System key ('switch','ps3','ps4','wiiu','x360'); null for non-catalog apps (Big Box).</summary>
    public string? System { get; set; }
    /// <summary>FK to ArcadeGame when card-integrated; null for v0 apps.</summary>
    public int? ArcadeGameId { get; set; }
    public string Exe { get; set; } = default!;
    /// <summary>Emulator arguments; <c>{rom}</c> is replaced with the staged (or literal) ROM path.</summary>
    public string? ArgsTemplate { get; set; }
    public string? WorkingDir { get; set; }
    public HeavyStaging? Staging { get; set; }
    public HeavySave? Save { get; set; }
    public HeavyInput? Input { get; set; }
    /// <summary>Box art image for Apollo's own grid (absolute path on Ziggy, or null).</summary>
    public string? BoxArt { get; set; }
    public int RatingCeiling { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>The resolved ROM argument: the staged cache path for staged apps, else the template
    /// verbatim (already-local titles put the literal path in <see cref="ArgsTemplate"/>).</summary>
    public bool NeedsStaging => Staging != null && !string.IsNullOrEmpty(Staging.Source);
}

public sealed class HeavyStaging
{
    /// <summary>Source file on the library drive (explicit path — never a scan). v1 stages single
    /// files (Switch xci/nsp); folder/PKG systems (PS3/PS4) install once by hand and use staging=null.</summary>
    public string Source { get; set; } = default!;
    /// <summary>Update/DLC files installed once into the emulator NAND at H1 — recorded here as the
    /// ledger of what was installed; the stager itself copies only the base ROM.</summary>
    public string[]? Updates { get; set; }
    public string? CacheTier { get; set; }
}

public sealed class HeavySave
{
    public string Kind { get; set; } = "dir";
    public string? LivePath { get; set; }
    public string? TitleId { get; set; }
}

public sealed class HeavyInput
{
    /// <summary>'x360' (default) or 'ds4' (gyro titles — motion only exists on the DS4 HID, §6.2).</summary>
    public string? Gamepad { get; set; }
    public int? MaxPads { get; set; }
}

/// <summary>
/// Loads and hot-reloads the descriptor directory. Same discipline as RomCache's manifest: reload is
/// mtime-driven and cheap, so every read sees current descriptors without a restart.
/// </summary>
public sealed class HeavyAppRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string appsDir;
    private readonly ILogger log;
    private readonly object gate = new();
    private Dictionary<string, HeavyApp> byId = new(StringComparer.OrdinalIgnoreCase);
    private DateTime lastStamp;
    private DateTime lastCheck;

    public HeavyAppRegistry(string appsDir, ILogger log)
    {
        this.appsDir = appsDir;
        this.log = log;
        Reload();
    }

    public IReadOnlyList<HeavyApp> All()
    {
        MaybeReload();
        lock (gate) return byId.Values.OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public HeavyApp? Get(string appId)
    {
        MaybeReload();
        lock (gate) return byId.GetValueOrDefault(appId);
    }

    public HeavyApp? GetByArcadeGameId(int gameId)
    {
        MaybeReload();
        lock (gate) return byId.Values.FirstOrDefault(a => a.ArcadeGameId == gameId);
    }

    private void MaybeReload()
    {
        // Debounced directory-stamp check: the newest write time across the dir + its jsons.
        lock (gate) { if ((DateTime.UtcNow - lastCheck).TotalSeconds < 5) return; lastCheck = DateTime.UtcNow; }
        try
        {
            var stamp = DirStamp();
            lock (gate) { if (stamp == lastStamp) return; }
            Reload();
        }
        catch (Exception ex) { log.LogWarning(ex, "Heavy descriptor reload check failed"); }
    }

    private DateTime DirStamp()
    {
        if (!Directory.Exists(appsDir)) return DateTime.MinValue;
        var stamp = Directory.GetLastWriteTimeUtc(appsDir);
        foreach (var f in Directory.EnumerateFiles(appsDir, "*.json"))
        {
            var m = File.GetLastWriteTimeUtc(f);
            if (m > stamp) stamp = m;
        }
        return stamp;
    }

    private void Reload()
    {
        var next = new Dictionary<string, HeavyApp>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(appsDir))
        {
            foreach (var f in Directory.EnumerateFiles(appsDir, "*.json"))
            {
                try
                {
                    var app = JsonSerializer.Deserialize<HeavyApp>(File.ReadAllText(f), JsonOpts);
                    if (app == null || string.IsNullOrWhiteSpace(app.Id) || string.IsNullOrWhiteSpace(app.Title))
                    { log.LogWarning("Heavy descriptor {File} missing id/title — skipped", Path.GetFileName(f)); continue; }
                    if (!next.TryAdd(app.Id, app))
                        log.LogWarning("Heavy descriptor {File}: duplicate id {Id} — skipped", Path.GetFileName(f), app.Id);
                }
                catch (Exception ex) { log.LogWarning(ex, "Heavy descriptor {File} unreadable — skipped", Path.GetFileName(f)); }
            }
        }
        lock (gate) { byId = next; lastStamp = DirStamp(); }
        log.LogInformation("Heavy descriptors loaded: {Count}", next.Count);
    }
}

/// <summary>
/// The one-heavy-session-at-a-time lock (plan §7.4). Ziggy-local (survives site deploys), taken by
/// prepare, released by finish. Crash-safe without a timer: staleness is evaluated ON READ — a lock
/// whose attached emulator PID is dead, or that never attached a PID within the stale window, is
/// silently reclaimable. Apollo itself refuses overlapping streams; this lock exists so the SITE can
/// say who/what holds the lane, and so H4's save seed/harvest can never interleave two users.
/// </summary>
public sealed class HeavyLock
{
    public sealed record LockState(string AppId, string? ClientName, DateTime SinceUtc, int? Pid);

    private readonly object gate = new();
    private readonly int staleMinutes;
    private LockState? state;

    public HeavyLock(int staleMinutes = 15) => this.staleMinutes = Math.Max(1, staleMinutes);

    /// <summary>Current holder, or null. Self-heals: a stale/dead holder is dropped on read.</summary>
    public LockState? Current()
    {
        lock (gate)
        {
            if (state != null && IsStale(state)) state = null;
            return state;
        }
    }

    /// <summary>Take the lock for an app. Re-entrant per app (a retried prepare for the SAME app
    /// refreshes rather than deadlocks — Moonlight relaunches after a hiccup hit this).</summary>
    public bool TryAcquire(string appId, string? clientName, out LockState holder)
    {
        lock (gate)
        {
            if (state != null && IsStale(state)) state = null;
            if (state != null && !string.Equals(state.AppId, appId, StringComparison.OrdinalIgnoreCase))
            { holder = state; return false; }
            state = new LockState(appId, clientName ?? state?.ClientName, state?.SinceUtc ?? DateTime.UtcNow,
                state?.AppId == appId ? state.Pid : null);
            holder = state;
            return true;
        }
    }

    /// <summary>Record the launched emulator PID — from then on liveness IS the process.</summary>
    public bool Attach(string appId, int pid)
    {
        lock (gate)
        {
            if (state == null || !string.Equals(state.AppId, appId, StringComparison.OrdinalIgnoreCase)) return false;
            state = state with { Pid = pid };
            return true;
        }
    }

    public bool Release(string appId)
    {
        lock (gate)
        {
            if (state == null || !string.Equals(state.AppId, appId, StringComparison.OrdinalIgnoreCase)) return false;
            state = null;
            return true;
        }
    }

    private bool IsStale(LockState s)
    {
        if (s.Pid is int pid)
        {
            try { System.Diagnostics.Process.GetProcessById(pid); return false; } // alive
            catch (ArgumentException) { return true; } // exited — reclaim
            catch { return false; } // access issues ≠ dead
        }
        // No PID ever attached: the launch script died between prepare and attach (or is still
        // staging/seeding). Give it the stale window, then self-heal.
        return DateTime.UtcNow - s.SinceUtc > TimeSpan.FromMinutes(staleMinutes);
    }
}
