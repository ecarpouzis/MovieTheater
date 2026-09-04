import { useCallback, useEffect, useState } from "react";
import { Spin, Button, Input, Tag, Empty, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import ArcadeAchievements from "./ArcadeAchievements";
import "./ArcadePage.css";
import "./RetroAchievements.css";

// ── Account panel: link/unlink the user's real retroachievements.org account + show their pulled RA
// profile (points, rank, recent). The link is optional — the friends boards work without it; linking just
// surfaces your real RA activity here.
function RaAccount() {
  const [loading, setLoading] = useState(true);
  const [status, setStatus] = useState({ linked: false });
  const [profile, setProfile] = useState(null);
  const [form, setForm] = useState({ username: "", password: "" });
  const [busy, setBusy] = useState(false);

  const refresh = useCallback(() => {
    setLoading(true);
    Promise.all([MovieAPI.getRetroAchievementsStatus(), MovieAPI.getRetroAchievementsProfile()])
      .then(([s, p]) => { setStatus(s || { linked: false }); setProfile(p || null); })
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  const link = () => {
    if (!form.username || !form.password) { message.warning("Enter your RetroAchievements username and password."); return; }
    setBusy(true);
    MovieAPI.linkRetroAchievements(form.username.trim(), form.password)
      .then(async (r) => {
        if (r.ok) { message.success("RetroAchievements linked."); setForm({ username: "", password: "" }); refresh(); }
        else { const b = await r.json().catch(() => ({})); message.error(b.message || "Couldn't link that account."); }
      })
      .catch(() => message.error("Couldn't reach the server."))
      .finally(() => setBusy(false));
  };

  const unlink = () => {
    setBusy(true);
    MovieAPI.unlinkRetroAchievements()
      .then(() => { message.success("Unlinked."); refresh(); })
      .finally(() => setBusy(false));
  };

  if (loading) return <div className="ra-acct ra-acct--loading"><Spin size="small" /></div>;

  if (!status.linked) {
    return (
      <div className="ra-acct">
        <div className="ra-acct__blurb">
          Link your <strong>RetroAchievements</strong> account to see your real RA points and recent unlocks
          here. Optional — your friends-board scores are tracked either way. Your password is used once to
          get a login token and is never stored.
        </div>
        <div className="ra-acct__form">
          <Input
            placeholder="RA username"
            value={form.username}
            onChange={(e) => setForm((f) => ({ ...f, username: e.target.value }))}
            onPressEnter={link}
            disabled={busy}
          />
          <Input.Password
            placeholder="RA password"
            value={form.password}
            onChange={(e) => setForm((f) => ({ ...f, password: e.target.value }))}
            onPressEnter={link}
            disabled={busy}
          />
          <Button type="primary" onClick={link} loading={busy}>Link</Button>
        </div>
      </div>
    );
  }

  const avail = profile && profile.available;
  return (
    <div className="ra-acct">
      <div className="ra-acct__linked">
        <span>Linked as <strong>{status.raUser}</strong></span>
        <Button size="small" onClick={unlink} loading={busy} danger>Unlink</Button>
      </div>
      {avail ? (
        <div className="ra-acct__profile">
          <div className="ra-acct__nums">
            <div className="ra-acct__num"><strong>{(profile.totalPoints || 0).toLocaleString()}</strong><span>points</span></div>
            {profile.rank > 0 && <div className="ra-acct__num"><strong>#{profile.rank.toLocaleString()}</strong><span>global rank</span></div>}
            {profile.profileUrl && <a className="ra-acct__link" href={profile.profileUrl} target="_blank" rel="noreferrer">on RA ↗</a>}
          </div>
          {Array.isArray(profile.recent) && profile.recent.length > 0 && (
            <div className="ra-acct__recent">
              <div className="ra-acct__recent-h">Recent on RetroAchievements</div>
              <ul>
                {profile.recent.slice(0, 8).map((a, i) => (
                  <li key={a.id || i}>
                    <a href={a.raUrl} target="_blank" rel="noreferrer">{a.title}</a>
                    <span className="ra-acct__recent-pts">{a.hardcore ? "🏆" : "🎖️"} {a.points} pts</span>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      ) : (
        <div className="ra-acct__note">
          {profile && profile.configured === false
            ? "RA profile sync isn't configured on this server yet."
            : "Couldn't load your RA profile right now."}
        </div>
      )}
    </div>
  );
}

// ── Trophy room: the games you've earned achievements in (our own arcade mirror), with per-game counts.
// Click a game to see its full achievement set with your earned ones lit.
function TrophyRoom() {
  const [loading, setLoading] = useState(true);
  const [data, setData] = useState(null);
  const [openGame, setOpenGame] = useState(null); // { id, title }

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    MovieAPI.getMyArcadeTrophies()
      .then((d) => { if (!cancelled) setData(d); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, []);

  if (loading) return <div className="ra-trophy ra-trophy--loading"><Spin size="small" /></div>;

  if (openGame) {
    return (
      <div className="ra-trophy">
        <button type="button" className="ra-trophy__back" onClick={() => setOpenGame(null)}>← All trophies</button>
        <div className="ra-trophy__game-title">{openGame.title}</div>
        <ArcadeAchievements gameId={openGame.id} />
      </div>
    );
  }

  const games = (data && data.games) || [];
  if (games.length === 0) {
    return (
      <div className="ra-trophy ra-trophy--empty">
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description="No trophies yet. Play a game that tracks achievements and they'll show up here."
        />
      </div>
    );
  }

  return (
    <div className="ra-trophy">
      <div className="ra-trophy__totals">
        <div className="ra-trophy__total"><strong>{(data.totalPoints || 0).toLocaleString()}</strong><span>points</span></div>
        <div className="ra-trophy__total"><strong>{data.totalEarned}</strong><span>achievements</span></div>
        <div className="ra-trophy__total"><strong>{data.gameCount}</strong><span>games</span></div>
      </div>
      <div className="ra-trophy__grid">
        {games.map((g) => (
          <button type="button" key={g.gameId} className="ra-trophy__tile" onClick={() => setOpenGame({ id: g.gameId, title: g.title })}>
            <div className="ra-trophy__tile-title">{g.title}</div>
            <div className="ra-trophy__tile-sys">{g.system}</div>
            <div className="ra-trophy__tile-stats">
              <span title="Achievements earned">🎖️ {g.earnedCount}</span>
              <span title="Points">{g.points} pts</span>
              {g.legitCount > 0 && <Tag color="volcano" title="Legit hardcore earns">🏆 {g.legitCount}</Tag>}
            </div>
          </button>
        ))}
      </div>
    </div>
  );
}

/**
 * `/arcade/trophies` — the RetroAchievements hub: the account link and profile on top, the trophy
 * room below.
 *
 * It was a 720px antd Modal rendered OVER the lobby, because the bar draws Trophies as a tab and the
 * site's rule is that a modal lives in the URL. The rule is right; the reading of it was not. A tab
 * is navigation, and everywhere else on this site a tab is a page (`/arcade/explore`, `/music/rate`,
 * every admin tab) — so this one made choosing Trophies mount the whole lobby as a backdrop: the
 * catalog browse over ~13k cards, the renderer map, and a 12-second live-rooms poll running behind a
 * dialog nobody could see past. The hub is a destination; it is a page now.
 */
export default function ArcadeTrophiesPage({ userData }) {
  // The Trophies tab is not gated, so an anonymous visitor can land here. Every endpoint below is
  // self-scoped and its MovieAPI wrapper swallows the 401 into an empty payload — which would draw
  // "No trophies yet. Play a game that tracks achievements…" at someone who simply is not signed in.
  if (!userData) {
    return (
      <div className="arcade-page">
        <div className="arcade-page__inner" style={{ padding: 48 }}>
          <Empty description="Sign in to see your trophies." />
        </div>
      </div>
    );
  }
  return (
    <div className="arcade-page">
      <div className="arcade-page__inner">
        <header className="arcade-header">
          <div className="arcade-header__lede">
            <h1 className="arcade-title">🏆 RetroAchievements</h1>
            <p className="arcade-subtitle">
              Everything you have unlocked in the arcade, game by game — and, if you link the
              account below, what retroachievements.org has you down for as well.
            </p>
          </div>
        </header>
        <RaAccount />
        <div className="ra-section-h">Your trophy room</div>
        <TrophyRoom />
      </div>
    </div>
  );
}
