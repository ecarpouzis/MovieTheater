import { useEffect, useState } from "react";
import { Button, Input, Modal, Popconfirm, Spin, message } from "antd";
import { MovieAPI } from "../../MovieAPI";

/**
 * Link (or unlink) the signed-in user's OWN RetroAchievements account. RA's ToS is one account per human,
 * so we never share a login — each user brings their own. We perform RA's one-time username+password login
 * server-side to obtain a durable connect token (the password is never stored); after that, rooms this user
 * CREATES run rcheevos under their account so their play earns achievements and leaderboard runs.
 *
 * A password field over HTTPS to our own server, used once and discarded — same trust model as the site
 * login. We never see or keep the RA password; only the token, encrypted at rest.
 */
export default function RetroAchievementsPanel({ open, onClose }) {
  const [loading, setLoading] = useState(true);
  const [linked, setLinked] = useState(false);
  const [raUser, setRaUser] = useState(null);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setLoading(true);
    MovieAPI.getRetroAchievementsStatus()
      .then((s) => { if (!cancelled) { setLinked(!!s.linked); setRaUser(s.raUser || null); } })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [open]);

  async function submit() {
    if (!username.trim() || !password) return;
    setBusy(true);
    try {
      const r = await MovieAPI.linkRetroAchievements(username.trim(), password);
      const j = await r.json().catch(() => ({}));
      if (r.ok && j.linked) {
        setLinked(true);
        setRaUser(j.raUser);
        setPassword("");
        message.success(`Linked RetroAchievements as ${j.raUser}`);
      } else {
        message.error(j.message || "Couldn't link that account.");
      }
    } catch {
      message.error("Couldn't reach the server.");
    } finally {
      setBusy(false);
    }
  }

  async function unlink() {
    setBusy(true);
    try {
      await MovieAPI.unlinkRetroAchievements();
      setLinked(false);
      setRaUser(null);
      message.success("Unlinked RetroAchievements.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open={open} onCancel={onClose} footer={null} title="RetroAchievements" width={460} zIndex={1500}>
      {loading ? (
        <div style={{ textAlign: "center", padding: 24 }}><Spin /></div>
      ) : linked ? (
        <div>
          <p>
            Linked as <b>{raUser}</b>. Rooms you start earn achievements on your RetroAchievements account —
            softcore in a normal room, <b>hardcore</b> in a competitive one.
          </p>
          <Popconfirm title="Unlink your RetroAchievements account?" okText="Unlink" onConfirm={unlink}>
            <Button danger loading={busy}>Unlink</Button>
          </Popconfirm>
        </div>
      ) : (
        <div>
          <p style={{ marginBottom: 12 }}>
            Link your own <a href="https://retroachievements.org" target="_blank" rel="noreferrer">RetroAchievements</a>{" "}
            account to earn achievements and post leaderboard runs while you play here. Your password is used
            once to get a login token and is never stored.
          </p>
          <Input
            placeholder="RetroAchievements username"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            style={{ marginBottom: 8 }}
            autoComplete="off"
          />
          <Input.Password
            placeholder="Password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            onPressEnter={submit}
            style={{ marginBottom: 12 }}
            autoComplete="off"
          />
          <Button type="primary" loading={busy} disabled={!username.trim() || !password} onClick={submit}>
            Link account
          </Button>
        </div>
      )}
    </Modal>
  );
}
