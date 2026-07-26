using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Closes out arcade rooms whose players have all gone quiet, in TWO passes on a gentle timer.
    ///
    /// 1. <b>Prompt pass</b> — ask the in-memory <see cref="ArcadeRoomService"/> which rooms just emptied
    ///    (presence TTL expired) and stamp their <c>ArcadeSession.EndedUtc</c> immediately. This is the
    ///    normal, tidy path: it closes a room seconds after the last player leaves.
    ///
    /// 2. <b>Reconcile pass</b> — close rows the prompt pass can never see. The in-memory registry is
    ///    per-process: a pod restart or deploy wipes it, and every session that was live at that moment
    ///    was orphaned as <c>EndedUtc = NULL</c> FOREVER (795 such rows had piled up by 2026-07-14 — some
    ///    "live" for 13+ hours). No amount of in-memory bookkeeping can fix that, because the process that
    ///    held the knowledge is gone. So we reconcile against a DURABLE signal instead:
    ///    <c>ArcadeSession.LastSeenUtc</c>, which the Heartbeat endpoint stamps every ~30 s while a real
    ///    browser is in the room. A row is a corpse when its last durable sign of life is older than
    ///    <see cref="StaleAfter"/> AND the registry isn't serving it right now.
    ///
    /// ⚠ The obvious fix — "at startup, close every NULL row not in the live in-memory set" — is a TRAP,
    /// and it is why this is stamp-based instead. At startup that set is EMPTY by definition, so it would
    /// close the rows of sessions that are still genuinely playing (the emulator and the players' WebRTC
    /// never noticed the deploy) — and those rows are exactly what the Heartbeat path needs in order to
    /// Rehydrate the room. It would turn "the lobby forgot your room for 12 s" into "your room is gone".
    ///
    /// Bounded per tick (<see cref="ReconcileBatch"/>) per the chunked-bulk-jobs rule: the backlog drains
    /// over several ticks with a log line each, and the pass is idempotent — re-running only ever closes
    /// rows that are still stale. Follows the <c>ChannelScheduleMaintenanceService</c> shape: scoped DB
    /// access, a loop that never dies on a transient failure.
    /// </summary>
    public class ArcadeRoomReaperService : BackgroundService
    {
        private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

        /// <summary>How long a row may go without a durable heartbeat before it is presumed dead. Must sit
        /// well above the browser's beat (~12 s) and the persist throttle (30 s) so an alive-but-quiet room
        /// is never closed under a player; 5 min also comfortably covers a slow deploy, during which a live
        /// session keeps heartbeating the moment the new pod is up.</summary>
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

        /// <summary>Rows closed per tick. The 795-row backlog drains in a few ticks rather than one
        /// unbounded UPDATE, and each batch reports what it did.</summary>
        private const int ReconcileBatch = 200;

        private readonly IServiceScopeFactory scopeFactory;
        private readonly ArcadeRoomService rooms;
        private readonly ILogger<ArcadeRoomReaperService> logger;

        public ArcadeRoomReaperService(
            IServiceScopeFactory scopeFactory, ArcadeRoomService rooms, ILogger<ArcadeRoomReaperService> logger)
        {
            this.scopeFactory = scopeFactory;
            this.rooms = rooms;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // When THIS process started. Pass 2 judges a row by how long it has gone unheartbeated, but a
            // row cannot be blamed for silence during a window when there was nobody here to hear it: a
            // deploy takes the pod (and with it every /Heartbeat endpoint) away for a minute or two, and
            // the browsers keep playing throughout — CloudRetro tracks their WebRTC connection, not us.
            // Reaping on a stamp written before we existed closed rooms out from under live players, and
            // that close is a ONE-WAY DOOR: the Heartbeat rehydrate path only accepts a row with a null
            // EndedUtc, so the room could never come back, no fresh save token was ever minted again, and
            // quicksave 403'd for the rest of the night on a session that was otherwise perfect
            // (2026-07-26, Mario BAZR — third repeat of this class of bug). So: give every row a full
            // StaleAfter window after startup to prove itself. A genuine corpse is closed one tick later.
            var startedUtc = DateTime.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var reaped = rooms.ReapExpired();
                    var live = rooms.LiveRoomCodes();

                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<MovieDb>();
                    var now = DateTime.UtcNow;

                    // Pass 1 — rooms the registry just watched go empty.
                    if (reaped.Count > 0)
                    {
                        var sessions = await db.ArcadeSessions
                            .Where(s => reaped.Contains(s.RoomCode) && s.EndedUtc == null)
                            .ToListAsync(stoppingToken);
                        foreach (var s in sessions)
                            s.EndedUtc = now;
                        if (sessions.Count > 0)
                            await db.SaveChangesAsync(stoppingToken);
                    }

                    // Pass 2 — corpses no registry remembers. LastSeenUtc is null for rows written before
                    // the column existed (and for a room abandoned before its first beat), so CreatedUtc is
                    // the floor: an unheartbeated row is judged from its birth.
                    var cutoff = now - StaleAfter;
                    // …but never judge a row on silence from before this pod existed (see startedUtc).
                    if (startedUtc <= cutoff)
                    {
                        var stale = await db.ArcadeSessions
                            .Where(s => s.EndedUtc == null
                                        && (s.LastSeenUtc ?? s.CreatedUtc) < cutoff
                                        && !live.Contains(s.RoomCode))
                            .OrderBy(s => s.CreatedUtc)
                            .Take(ReconcileBatch)
                            .ToListAsync(stoppingToken);
                        if (stale.Count > 0)
                        {
                            foreach (var s in stale)
                                s.EndedUtc = now;
                            await db.SaveChangesAsync(stoppingToken);

                            var remaining = await db.ArcadeSessions
                                .CountAsync(s => s.EndedUtc == null
                                                 && (s.LastSeenUtc ?? s.CreatedUtc) < cutoff
                                                 && !live.Contains(s.RoomCode), stoppingToken);
                            logger.LogInformation(
                                "Arcade reaper reconciled {Closed} stale room row(s) (no heartbeat for {Stale}); {Remaining} still to close.",
                                stale.Count, StaleAfter, remaining);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let a transient failure kill the loop — the next tick retries.
                    logger.LogWarning(ex, "Arcade room reaper tick failed; will retry.");
                }

                try { await Task.Delay(Tick, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
