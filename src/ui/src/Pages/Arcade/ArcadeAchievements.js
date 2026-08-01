import { useEffect, useState } from "react";
import { Spin, Tooltip, Progress, Empty } from "antd";
import { MovieAPI } from "../../MovieAPI";
import { LegitTags } from "./ArcadeLeaderboards";
import "./RetroAchievements.css";

// Every achievement that EXISTS for a game (from RetroAchievements), with the signed-in user's earned
// ones lit and their badge in colour, the rest greyed (RA serves a "_lock" badge). Earned achievements
// carry the same run-legitimacy why-icons as the boards. Used both in the game modal and when a trophy-
// room tile is expanded. Renders a friendly hint when RA has no set for the game / isn't configured.
export default function ArcadeAchievements({ gameId }) {
  const [loading, setLoading] = useState(true);
  const [data, setData] = useState(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    MovieAPI.getArcadeGameAchievements(gameId)
      .then((d) => { if (!cancelled) setData(d); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [gameId]);

  if (loading) return <div className="agm-ach agm-ach--loading"><Spin size="small" /></div>;
  if (!data || !data.available || !Array.isArray(data.achievements) || data.achievements.length === 0) {
    return (
      <div className="agm-ach agm-ach--empty">
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description={data && data.configured === false
            ? "RetroAchievements isn't configured on this server yet."
            : "No RetroAchievements set for this game."}
        />
      </div>
    );
  }

  const pct = data.pointsTotal > 0 ? Math.round((data.pointsEarned / data.pointsTotal) * 100) : 0;

  return (
    <div className="agm-ach">
      <div className="agm-ach__head">
        <div className="agm-ach__stat">
          {/* `partial` = the server had no RA set to show, so these are the unlocks from our own mirror
              and nothing else. Showing "3 / 3 earned · 100%" there would claim a completed game. */}
          {data.partial
            ? <><strong>{data.earnedCount}</strong> earned</>
            : <><strong>{data.earnedCount}</strong> / {data.numAchievements} earned</>}
          <span className="agm-ach__pts"> · {data.pointsEarned}{data.partial ? "" : `/${data.pointsTotal}`} pts</span>
        </div>
        {/* No bar when partial: pointsTotal is just pointsEarned there, so it would sit at 100%. */}
        {data.partial
          ? <Tooltip title="This game isn't matched to its RetroAchievements set on the server, so only the achievements you've already unlocked here can be listed — the rest of the set, the descriptions and the badge art are missing. Everything you earned is safe; it's the catalogue link that's absent.">
              <span className="agm-ach__partial">earned only ⓘ</span>
            </Tooltip>
          : <Progress percent={pct} size="small" showInfo={false} strokeColor="#e0a800" className="agm-ach__bar" />}
        {data.raUrl && (
          <a className="agm-ach__ra" href={data.raUrl} target="_blank" rel="noreferrer" title="View this game on RetroAchievements">RA ↗</a>
        )}
      </div>
      <div className="agm-ach__grid">
        {data.achievements.map((a) => (
          <Tooltip
            key={a.id}
            // Tapping a badge shows its details — it is NOT a link off to RA. (The one RA link is in
            // the header.) "click" is what makes the tooltip usable on touch, where there's no hover.
            trigger={["hover", "focus", "click"]}
            title={
              <div className="agm-ach__tip">
                <div className="agm-ach__tip-title">{a.title} · {a.points} pts</div>
                {a.description && <div>{a.description}</div>}
                {a.earned && a.earnedUtc && <div className="agm-ach__tip-when">Earned {new Date(a.earnedUtc).toLocaleDateString()}</div>}
                {!a.earned && <div className="agm-ach__tip-when">Locked</div>}
              </div>
            }
          >
            <span
              role="button"
              tabIndex={0}
              className={a.earned ? "agm-ach__cell agm-ach__cell--earned" : "agm-ach__cell agm-ach__cell--locked"}
            >
              {a.badgeUrl
                ? <img src={a.badgeUrl} alt={a.title} loading="lazy" className="agm-ach__badge" />
                : <span className="agm-ach__badge agm-ach__badge--none">🎖️</span>}
              {a.earned && (
                <span className="agm-ach__why">
                  <LegitTags entry={{ competitive: a.earnedCompetitive, cheat: a.cheat, savescum: a.savescum, timeplay: a.timeplay, legit: a.legit }} />
                </span>
              )}
            </span>
          </Tooltip>
        ))}
      </div>
    </div>
  );
}
