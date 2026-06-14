import { useState, useEffect, useMemo } from "react";
import { Modal, Input, Button, Checkbox, message } from "antd";
import { MovieAPI } from "../MovieAPI";
import "./UserSettingsModal.css";
import "./AdminModal.css";

// Admin-only tool for managing users: creating an initial streaming password (users can't
// create their own first password) and granting the editor permission. Visibility is driven
// by userData.isAdmin, and every endpoint it calls is independently admin-gated on the server.
function AdminModal({ open, onClose }) {
  const [users, setUsers] = useState([]);
  const [loading, setLoading] = useState(false);
  const [filter, setFilter] = useState("");

  // Per-user editing state keyed by userId: the in-progress password and a busy flag.
  const [passwordDrafts, setPasswordDrafts] = useState({});
  const [busyUserId, setBusyUserId] = useState(null);

  useEffect(() => {
    if (!open) return;
    setFilter("");
    setPasswordDrafts({});
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

  return (
    <Modal
      open={open}
      onCancel={onClose}
      footer={null}
      width={720}
      wrapClassName="user-settings-modal"
      title={null}
      closeIcon={<span className="settings-modal-close">×</span>}
      getContainer={false}
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
      </div>
    </Modal>
  );
}

export default AdminModal;
