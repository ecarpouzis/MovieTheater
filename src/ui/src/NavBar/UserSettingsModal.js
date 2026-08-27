import { useState, useEffect, useMemo } from "react";
import { Modal, Select, Checkbox, Button, Input, message } from "antd";
import { MovieAPI } from "../MovieAPI";
import "./UserSettingsModal.css";
import { writeStored } from "../utils/storage";
import "../Components/SheetModal.css";
import { SHEET_Z } from "../Components/sheetModal";

const cardStyleOptions = [
  { value: "standard", label: "Standard" },
  { value: "simple", label: "Simple" },
];

// MPA ratings are static lookup data — cache them for the lifetime of the page
// so every modal open doesn't trigger a redundant network round-trip.
let mpaRatingsCache = null;

function UserSettingsModal({ open, onClose, userData, setUserData }) {
  const [mpaRatings, setMpaRatings] = useState([]);
  const [ageRestriction, setAgeRestriction] = useState(undefined);
  const [cardStyle, setCardStyle] = useState("standard");
  const [canEditMovies, setCanEditMovies] = useState(false);
  const [showBoardgameExpansions, setShowBoardgameExpansions] = useState(false);
  const [saving, setSaving] = useState(false);
  const [currentPassword, setCurrentPassword] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [passwordSaving, setPasswordSaving] = useState(false);

  const hasPassword = userData?.hasPassword ?? false;
  const isAdmin = userData?.isAdmin ?? false;
  // Users can't create their own first password — streaming access is provisioned by an admin.
  // They can still change/remove a password they already have. Admins may set their own first one.
  const canSetPassword = hasPassword || isAdmin;

  useEffect(() => {
    if (!open || !userData) return;

    // Initialize form fields synchronously from userData so the form is
    // immediately populated — no waiting for the ratings API call.
    setAgeRestriction(userData.ageRestriction ?? undefined);
    setCardStyle(userData.cardStyle ?? "standard");
    setCanEditMovies(userData.canEditMovies ?? false);
    setShowBoardgameExpansions(userData.showBoardgameExpansions ?? false);
    setCurrentPassword("");
    setNewPassword("");
    setConfirmPassword("");

    // Use the cached ratings if already fetched; otherwise fetch once and cache.
    if (mpaRatingsCache) {
      setMpaRatings(mpaRatingsCache);
      return;
    }

    MovieAPI.getMPARatings()
      .then((r) => {
        if (!r.ok) throw new Error(`GetMPARatings failed: HTTP ${r.status}`);
        return r.json();
      })
      .then((data) => {
        mpaRatingsCache = Array.isArray(data) ? data : [];
        setMpaRatings(mpaRatingsCache);
      })
      .catch((error) => {
        console.error("Error loading MPA ratings:", error);
      });
  }, [open, userData]);

  const handleSave = () => {
    setSaving(true);
    const ageValue = ageRestriction != null ? String(ageRestriction) : null;
    Promise.all([
      MovieAPI.setUserSetting("AgeRestriction", ageValue).then((r) => r.json()),
      MovieAPI.setUserSetting("CardStyle", cardStyle).then((r) => r.json()),
      MovieAPI.setUserSetting("ShowBoardgameExpansions", showBoardgameExpansions ? "true" : "false").then((r) => r.json()),
    ])
      .then(() => {
        setUserData((prev) => ({ ...prev, ageRestriction, cardStyle, showBoardgameExpansions }));
        writeStored("CardStyle", cardStyle ?? "standard");
        message.success("Settings Saved!");
        setTimeout(() => {
          onClose();
        }, 500);
      })
      .catch((error) => {
        console.error("Error saving settings:", error);
        message.error("Failed to save settings");
      })
      .finally(() => setSaving(false));
  };

  // Setting/changing/removing the password is its own server call, separate from
  // the settings save. Removing sends an empty new password.
  const submitPassword = (removing) => {
    if (!removing) {
      if (!newPassword || newPassword.length < 8) {
        message.error("Password must be at least 8 characters.");
        return;
      }
      if (newPassword !== confirmPassword) {
        message.error("Passwords do not match.");
        return;
      }
    }
    setPasswordSaving(true);
    MovieAPI.setPassword(hasPassword ? currentPassword : null, removing ? null : newPassword)
      .then((r) => r.json().then((body) => ({ ok: r.ok, body })))
      .then(({ ok, body }) => {
        if (!ok) {
          message.error(body.message ?? "Failed to update password.");
          return;
        }
        message.success(removing ? "Password removed." : "Password saved!");
        setUserData((prev) => ({ ...prev, hasPassword: body.hasPassword }));
        setCurrentPassword("");
        setNewPassword("");
        setConfirmPassword("");
      })
      .catch((error) => {
        console.error("Error updating password:", error);
        message.error("Failed to update password.");
      })
      .finally(() => setPasswordSaving(false));
  };

  // Re-derive options only when the ratings array reference changes.
  const mpaOptions = useMemo(
    () => mpaRatings.map((r) => ({ value: r.id, label: r.name })),
    [mpaRatings],
  );

  return (
    <Modal
      open={open}
      onCancel={onClose}
      footer={null}
      width={560}
      // The site's dialog layer (Components/sheetModal.js). The dialog used to reach 1400 through a
      // `z-index: … !important` on its wrap class, which left the MASK at antd's default 1000 —
      // under the fixed phone top bar, so the bar stayed lit and clickable over an open dialog.
      zIndex={SHEET_Z}
      wrapClassName="sheet-modal user-settings-modal"
      title={null}
      closeIcon={<span className="settings-modal-close">×</span>}
    >
      <div className="settings-modal-content">
        <h2 className="settings-modal-title">User Settings</h2>

        <div className="settings-section">
          <h3 className="settings-section-title">Content Filtering</h3>
          <div className="settings-row">
            <span className="settings-label">Age Restriction</span>
            <Select
              className="settings-select"
              classNames={{ popup: { root: "settings-select-dropdown" } }}
              value={ageRestriction}
              onChange={(v) => setAgeRestriction(v)}
              options={mpaOptions}
              placeholder="No Restriction"
              getPopupContainer={(trigger) => trigger.parentElement}
            />
          </div>
          <p className="settings-hint">Movies above this MPA rating will be hidden from all views.</p>
        </div>

        <div className="settings-section">
          <h3 className="settings-section-title">Display</h3>
          <div className="settings-row">
            <span className="settings-label">Card Style</span>
            <Select
              className="settings-select"
              classNames={{ popup: { root: "settings-select-dropdown" } }}
              value={cardStyle}
              onChange={(v) => setCardStyle(v)}
              options={cardStyleOptions}
              getPopupContainer={(trigger) => trigger.parentElement}
            />
          </div>
          <p className="settings-hint">Simple shows a compact two-column layout. Standard shows a full row with plot and actors.</p>
        </div>

        <div className="settings-section">
          <h3 className="settings-section-title">Board Games</h3>
          <div className="settings-row settings-row--push">
            <Checkbox checked={showBoardgameExpansions} onChange={(e) => setShowBoardgameExpansions(e.target.checked)}>
              Show Expansions
            </Checkbox>
          </div>
          <p className="settings-hint">When enabled, boardgame expansions appear in the list alongside base games.</p>
        </div>

        <div className="settings-section">
          <h3 className="settings-section-title">Account Security</h3>
          {canSetPassword ? (
            <>
              {hasPassword && (
                <div className="settings-row">
                  <span className="settings-label">Current Password</span>
                  <Input.Password
                    className="settings-password-input"
                    value={currentPassword}
                    onChange={(e) => setCurrentPassword(e.target.value)}
                    autoComplete="current-password"
                  />
                </div>
              )}
              <div className="settings-row">
                <span className="settings-label">New Password</span>
                <Input.Password
                  className="settings-password-input"
                  value={newPassword}
                  onChange={(e) => setNewPassword(e.target.value)}
                  autoComplete="new-password"
                />
              </div>
              <div className="settings-row">
                <span className="settings-label">Confirm Password</span>
                <Input.Password
                  className="settings-password-input"
                  value={confirmPassword}
                  onChange={(e) => setConfirmPassword(e.target.value)}
                  autoComplete="new-password"
                />
              </div>
              <div className="settings-row settings-row--push">
                <Button onClick={() => submitPassword(false)} loading={passwordSaving}>
                  {hasPassword ? "Change Password" : "Set Password"}
                </Button>
                {hasPassword && (
                  <Button danger onClick={() => submitPassword(true)} loading={passwordSaving}>
                    Remove Password
                  </Button>
                )}
              </div>
              <p className="settings-hint">
                Once set, you'll be asked for it when logging in. At least 8 characters.
              </p>
            </>
          ) : (
            <p className="settings-hint">
              Your account has no password. Passwords are set up by an administrator.
            </p>
          )}
        </div>

        <div className="settings-section">
          <h3 className="settings-section-title">Permissions</h3>
          <div className="settings-row">
            <Checkbox checked={canEditMovies} disabled>
              <span style={{ color: "inherit" }}>Can Edit Movies</span>
            </Checkbox>
          </div>
          <p className="settings-hint">This permission is managed by an administrator.</p>
        </div>

        <div className="settings-modal-footer">
          <Button type="primary" onClick={handleSave} loading={saving}>
            Save Settings
          </Button>
          <Button className="btn-cancel" onClick={onClose}>
            Cancel
          </Button>
        </div>
      </div>
    </Modal>
  );
}

export default UserSettingsModal;
