import { useEffect, useState } from "react";
import { Spin, Tag, Tooltip, Collapse } from "antd";
import { MovieAPI } from "../../MovieAPI";

// Run-legitimacy markers for a board/achievement entry. A CLEAN run gets the trophy; anything tainted
// shows a why-icon per reason (a save-scummed record can still hold the top slot, so we say WHY rather
// than hide it). `legit` is OBSERVED, derived server-side from the taints alone (ArcadeAchievementUnlock
// .Clean, a computed column) — a casual room earns the trophy too, right up until something dirties the
// run. `competitive` is only which mode the room was in. Reused by the room toast.
export const LEGIT_REASONS = [
  { key: "cheat", icon: "🔧", label: "Cheat codes were enabled" },
  { key: "savescum", icon: "💾", label: "A save state was loaded mid-run" },
  { key: "timeplay", icon: "⏩", label: "Fast-forward / rewind was used" },
];

export function LegitTags({ entry }) {
  if (!entry) return null;
  const reasons = LEGIT_REASONS.filter((r) => entry[r.key]);
  if (reasons.length > 0) {
    return (
      <>
        {reasons.map((r) => (
          <Tooltip key={r.key} title={r.label}>
            <Tag color="gold" className="agm-lb__why">{r.icon}</Tag>
          </Tooltip>
        ))}
      </>
    );
  }
  if (entry.legit) {
    return (
      <Tooltip title={entry.competitive
        ? "Clean run, competitive room — no cheats, save-scumming, or fast-forward"
        : "Clean run — no cheats, save-scumming, or fast-forward"}>
        <Tag color="volcano" className="agm-lb__hc">{entry.competitive ? "🏆 🏁" : "🏆"}</Tag>
      </Tooltip>
    );
  }
  return null;
}

// Format a leaderboard value for display by its RA format token. Time boards come in as frames/ms/etc.;
// score boards are plain numbers. Mirrors RetroAchievements' own display conventions closely enough to read.
function formatValue(value, format) {
  const f = (format || "SCORE").toUpperCase();
  if (f === "FRAMES") {
    // 60 fps is RA's canonical assumption for FRAMES boards.
    const totalMs = Math.round((value / 60) * 1000);
    return formatMs(totalMs);
  }
  if (f === "MILLISECS") return formatMs(value);
  if (f === "CENTISECS") return formatMs(value * 10);
  if (f === "SECONDS" || f === "TIMESECS" || f === "TIME") return formatMs(value * 1000);
  if (f === "MINUTES") return `${value} min`;
  // SCORE / VALUE / anything else: grouped integer.
  return Number(value).toLocaleString();
}

function formatMs(ms) {
  const totalSec = Math.floor(ms / 1000);
  const m = Math.floor(totalSec / 60);
  const s = totalSec % 60;
  const cs = Math.floor((ms % 1000) / 10);
  const pad = (n, w = 2) => String(n).padStart(w, "0");
  return m > 0 ? `${m}:${pad(s)}.${pad(cs)}` : `${s}.${pad(cs)}`;
}

/**
 * The friends-only leaderboards for a game card — our mirror of RetroAchievements submissions, grouped by
 * board and ranked. Speedrun boards (time formats) sort ascending, score boards descending; the server
 * already ranked them, so we render in order. Each board links out to the global RA board. Renders nothing
 * (an empty hint) until someone in the group has posted a run.
 */
export default function ArcadeLeaderboards({ gameId }) {
  const [loading, setLoading] = useState(true);
  const [boards, setBoards] = useState([]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    MovieAPI.getArcadeLeaderboards(gameId)
      .then((d) => { if (!cancelled) setBoards(Array.isArray(d?.boards) ? d.boards : []); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [gameId]);

  if (loading) return <div className="agm-lb agm-lb--loading"><Spin size="small" /></div>;

  // Friends first — this is a site between friends. Boards our friends have runs on lead; RA's own boards
  // (no friend run yet) are tucked into a secondary section purely as a reference for what RA tracks.
  const friendBoards = boards.filter((b) => b.hasEntries);
  const raOnly = boards.filter((b) => !b.hasEntries);

  if (friendBoards.length === 0 && raOnly.length === 0) {
    return (
      <div className="agm-lb agm-lb--empty">
        No leaderboard runs yet. Play a competitive room to set the first friend score.
      </div>
    );
  }

  return (
    <div className="agm-lb">
      {friendBoards.map((b) => (
        <div key={b.leaderboardId} className="agm-lb__board">
          <div className="agm-lb__board-title">
            {b.title || `Leaderboard ${b.leaderboardId}`}
            <a className="agm-lb__ra" href={b.raUrl} target="_blank" rel="noreferrer" title="View the global board on RetroAchievements">RA ↗</a>
          </div>
          <ol className="agm-lb__list">
            {b.entries.map((e) => (
              <li key={e.userId} className={e.you ? "agm-lb__row agm-lb__row--you" : "agm-lb__row"}>
                <span className="agm-lb__rank">{e.rank}</span>
                <span className="agm-lb__user">{e.username}{e.you ? " (you)" : ""}</span>
                <span className="agm-lb__value">{formatValue(e.value, b.format)}</span>
                <LegitTags entry={e} />
              </li>
            ))}
          </ol>
          {b.raTopScore && (
            <div className="agm-lb__ra-ref" title="The world-record entry on RetroAchievements">
              🌍 RA best: <strong>{b.raTopScore}</strong>{b.raTopUser ? ` — ${b.raTopUser}` : ""}
            </div>
          )}
        </div>
      ))}

      {friendBoards.length === 0 && (
        <div className="agm-lb__hint">No friend runs yet — here's what RetroAchievements tracks for this game.</div>
      )}

      {raOnly.length > 0 && (
        <Collapse
          ghost
          className="agm-lb__ra-boards"
          defaultActiveKey={friendBoards.length === 0 ? ["ra"] : []}
          items={[{
            key: "ra",
            label: `Other RA boards (${raOnly.length}) — no friend runs yet`,
            children: raOnly.map((b) => (
              <div key={b.leaderboardId} className="agm-lb__ra-board">
                <a className="agm-lb__ra-board-title" href={b.raUrl} target="_blank" rel="noreferrer">
                  {b.title || `Leaderboard ${b.leaderboardId}`} ↗
                </a>
                {b.raTopScore && (
                  <span className="agm-lb__ra-ref">🌍 {b.raTopScore}{b.raTopUser ? ` — ${b.raTopUser}` : ""}</span>
                )}
              </div>
            )),
          }]}
        />
      )}
    </div>
  );
}
