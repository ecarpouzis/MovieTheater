import { useCallback, useEffect, useState } from "react";
import { Modal, Spin, Button, Input, Tag, Empty, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import ArcadeAchievements from "./ArcadeAchievements";
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

// The RetroAchievements hub — account link/profile on top, the trophy room below. Opened from the lobby.
export default function RetroAchievementsModal({ open, onClose }) {
  return (
    <Modal
      open={open}
      onCancel={onClose}
      footer={null}
      width={720}
      title="🏆 RetroAchievements"
      className="ra-modal"
      destroyOnClose
    >
      <RaAccount />
      <div className="ra-modal__section-h">Your trophy room</div>
      <TrophyRoom />
    </Modal>
  );
}
