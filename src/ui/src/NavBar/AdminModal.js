import { useState, useEffect, useMemo } from "react";
import { Modal, Input, Button, Checkbox, message } from "antd";
import { MovieAPI } from "../MovieAPI";
import "./UserSettingsModal.css";
import "./AdminModal.css";
import "../Components/SheetModal.css";

// One line describing the patched-binary guard, for the footer of this modal.
//
// WHY IT LIVES HERE AND NOT IN A POPUP: the guard's *findings* interrupt (PatchedArtifactAlarm.js
// still throws a modal when a core actually shifted), but its LIVENESS must not. The report is held
// in memory per pod, so every deploy resets it and a "guard is not reporting" toast fired at the
// next admin page load even though nothing was wrong. Pull-when-you-look beats push-after-every-deploy
// for a fact that is only ever acted on deliberately.
function guardStatus(guard) {
  if (!guard) return { tone: "nopw", text: "Patched-binary guard: status unavailable." };
  if (guard.warming) {
    return {
      tone: "nopw",
      text:
        `Patched-binary guard: waiting for its first report (site up ${guard.uptimeMinutes} min, ` +
        `watchdog posts every 30). Normal after a deploy — nothing to do.`,
    };
  }
  if (guard.stale) {
    return {
      tone: "nopw",
      text: guard.reported
        ? `Patched-binary guard: LAST REPORT ${guard.ageMinutes} min ago (expected every 30). The arcade ` +
          `watchdog on Ziggy looks dead, so drift in our patched cores / Jellyfin DLLs would go unnoticed.`
        : `Patched-binary guard: NO report in the ${guard.uptimeMinutes} min this site has been up. The ` +
          `arcade watchdog on Ziggy looks dead, so drift would go unnoticed.`,
    };
  }
  if (!guard.ok) {
    return {
      tone: "nopw",
      text: `Patched-binary guard: ${guard.findingCount} finding(s) — a hand-built binary is not running.`,
    };
  }
  return {
    tone: "haspw",
    text: `Patched-binary guard: all patched cores and Jellyfin DLLs intact (checked ${guard.ageMinutes} min ago).`,
  };
}

// Admin-only tool for managing users: creating an initial streaming password (users can't
// create their own first password) and granting the editor permission. Visibility is driven
// by userData.isAdmin, and every endpoint it calls is independently admin-gated on the server.
function AdminModal({ open, onClose }) {
  const [users, setUsers] = useState([]);
  const [loading, setLoading] = useState(false);
  const [filter, setFilter] = useState("");
  const [guard, setGuard] = useState(null);

  // Per-user editing state keyed by userId: the in-progress password and a busy flag.
  const [passwordDrafts, setPasswordDrafts] = useState({});
  const [busyUserId, setBusyUserId] = useState(null);

  useEffect(() => {
    if (!open) return;
    setFilter("");
    setPasswordDrafts({});

    // Best-effort: the guard line is a footnote, so a failure here must never block the user list.
    setGuard(null);
    MovieAPI.adminGetPatchedArtifacts()
      .then((r) => (r.ok ? r.json() : null))
      .then(setGuard)
      .catch(() => setGuard(null));

    setLoading(true);
    MovieAPI.adminGetUsers()
      .then((r) => {
        if (!r.ok) throw new Error(`Admin/Users failed: HTTP ${r.status}`);
        return r.json();
      })
      .then((data) => setUsers(Array.isArray(data) ? data : []))
      .catch((error) => {
        console.error("Error loading users:", error);
        message.error("Failed to load users.");
      })
      .finally(() => setLoading(false));
  }, [open]);

  const filteredUsers = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    if (!needle) return users;
    return users.filter((u) => (u.username ?? "").toLowerCase().includes(needle));
  }, [users, filter]);

  const guardLine = guardStatus(guard);

  const patchUser = (userId, patch) =>
    setUsers((prev) => prev.map((u) => (u.userId === userId ? { ...u, ...patch } : u)));

  const setDraft = (userId, value) =>
    setPasswordDrafts((prev) => ({ ...prev, [userId]: value }));

  const savePassword = (user, clearing) => {
    const draft = passwordDrafts[user.userId] ?? "";
    if (!clearing && draft.length < 8) {
      message.error("Password must be at least 8 characters.");
      return;
    }
    setBusyUserId(user.userId);
    MovieAPI.adminSetUserPassword(user.userId, clearing ? null : draft)
      .then((r) => r.json().then((body) => ({ ok: r.ok, body })))
      .then(({ ok, body }) => {
        if (!ok) {
          message.error(body?.message ?? "Failed to update password.");
          return;
        }
        patchUser(user.userId, { hasPassword: body.hasPassword });
        setDraft(user.userId, "");
        message.success(clearing ? `Cleared ${user.username}'s password.` : `Set password for ${user.username}.`);
      })
      .catch((error) => {
        console.error("Error setting password:", error);
        message.error("Failed to update password.");
      })
      .finally(() => setBusyUserId(null));
  };

  const toggleEditor = (user, checked) => {
    setBusyUserId(user.userId);
    // A null value deletes the setting; "true" enables it.
    MovieAPI.adminSetUserSetting(user.userId, "CanEditMovies", checked ? "true" : null)
      .then((r) => r.json().then((body) => ({ ok: r.ok, body })))
      .then(({ ok, body }) => {
        if (!ok) {
          message.error(body?.message ?? "Failed to update permission.");
          return;
        }
        patchUser(user.userId, { canEditMovies: checked });
      })
      .catch((error) => {
        console.error("Error updating permission:", error);
        message.error("Failed to update permission.");
      })
      .finally(() => setBusyUserId(null));
  };

  // Family photo album membership (photos-plan.md §2.1). This is the only surface that grants it —
  // it is absent from the self-service settings allow-list — and it is deliberately separate from
  // ADMIN: an administrator is not implicitly in the family photos.
  const toggleFamilyAlbum = (user, checked) => {
    setBusyUserId(user.userId);
    MovieAPI.adminSetUserSetting(user.userId, "FamilyAlbum", checked ? "true" : null)
      .then((r) => r.json().then((body) => ({ ok: r.ok, body })))
      .then(({ ok, body }) => {
        if (!ok) {
          message.error(body?.message ?? "Failed to update access.");
          return;
        }
        patchUser(user.userId, { familyAlbum: checked });
      })
      .catch((error) => {
        console.error("Error updating family album access:", error);
        message.error("Failed to update access.");
      })
      .finally(() => setBusyUserId(null));
  };

  return (
    <Modal
      open={open}
      onCancel={onClose}
      footer={null}
      width={720}
      wrapClassName="sheet-modal user-settings-modal"
      title={null}
      closeIcon={<span className="settings-modal-close">×</span>}
    >
      <div className="settings-modal-content">
        <h2 className="settings-modal-title">User Administration</h2>

        <Input
          className="admin-filter-input"
          placeholder="Filter users…"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          allowClear
        />

        {loading ? (
          <p className="settings-hint">Loading users…</p>
        ) : (
          <div className="admin-user-list">
            {filteredUsers.map((user) => {
              const busy = busyUserId === user.userId;
              return (
                <div key={user.userId} className="admin-user-row">
                  <div className="admin-user-head">
                    <span className="admin-user-name">{user.username}</span>
                    {user.isAdmin && <span className="admin-badge admin-badge--admin">ADMIN</span>}
                    <span className={`admin-badge ${user.hasPassword ? "admin-badge--haspw" : "admin-badge--nopw"}`}>
                      {user.hasPassword ? "🔒 Password set" : "No password"}
                    </span>
                    <Checkbox
                      className="admin-editor-check"
                      checked={user.canEditMovies}
                      disabled={busy}
                      onChange={(e) => toggleEditor(user, e.target.checked)}
                    >
                      Can edit
                    </Checkbox>
                    <Checkbox
                      className="admin-editor-check"
                      checked={user.familyAlbum}
                      disabled={busy}
                      onChange={(e) => toggleFamilyAlbum(user, e.target.checked)}
                    >
                      Family photos
                    </Checkbox>
                  </div>
                  <div className="admin-user-pw-row">
                    <Input.Password
                      className="settings-password-input admin-pw-input"
                      placeholder={user.hasPassword ? "New password" : "Set initial password"}
                      value={passwordDrafts[user.userId] ?? ""}
                      onChange={(e) => setDraft(user.userId, e.target.value)}
                      autoComplete="new-password"
                      disabled={busy}
                    />
                    <Button onClick={() => savePassword(user, false)} loading={busy}>
                      {user.hasPassword ? "Change" : "Set"}
                    </Button>
                    {user.hasPassword && (
                      <Button danger onClick={() => savePassword(user, true)} loading={busy}>
                        Clear
                      </Button>
                    )}
                  </div>
                </div>
              );
            })}
            {filteredUsers.length === 0 && <p className="settings-hint">No users match.</p>}
          </div>
        )}

        <p className="settings-hint admin-footnote">
          A password unlocks streaming for that user. Clearing it returns the account to passwordless login.
          Administrators are defined in server config (AdminUsernames) and can't be granted here.
        </p>

        <p className="settings-hint admin-footnote">
          <span className={`admin-badge admin-badge--${guardLine.tone}`}>GUARD</span> {guardLine.text}
        </p>
      </div>
    </Modal>
  );
}

export default AdminModal;
