import { useState, useEffect } from "react";
import { Modal, Select, Checkbox, Button, message } from "antd";
import { MovieAPI } from "../MovieAPI";
import "./UserSettingsModal.css";

const cardStyleOptions = [
  { value: "standard", label: "Standard" },
  { value: "simple", label: "Simple" },
];

function UserSettingsModal({ open, onClose, userData, setUserData }) {
  const [mpaRatings, setMpaRatings] = useState([]);
  const [ageRestriction, setAgeRestriction] = useState(undefined);
  const [cardStyle, setCardStyle] = useState("standard");
  const [canEditMovies, setCanEditMovies] = useState(false);
  const [enablePagination, setEnablePagination] = useState(false);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (open && userData) {
      MovieAPI.getMPARatings()
        .then((r) => {
          if (!r.ok) throw new Error(`GetMPARatings failed: HTTP ${r.status}`);
          return r.json();
        })
        .then((data) => {
          setMpaRatings(Array.isArray(data) ? data : []);
          setAgeRestriction(userData.ageRestriction ?? undefined);
          setCardStyle(userData.cardStyle ?? "standard");
          setCanEditMovies(userData.canEditMovies ?? false);
          setEnablePagination(
            userData.enablePagination === undefined || userData.enablePagination === null ? false : Boolean(userData.enablePagination),
          );
        })
        .catch((error) => {
          console.error("Error loading MPA ratings:", error);
        });
    }
  }, [open, userData]);

  const handleSave = () => {
    setSaving(true);
    const ageValue = ageRestriction != null ? String(ageRestriction) : null;
    Promise.all([
      MovieAPI.setUserSetting("AgeRestriction", ageValue).then((r) => r.json()),
      MovieAPI.setUserSetting("CardStyle", cardStyle).then((r) => r.json()),
      MovieAPI.setUserSetting("EnablePagination", enablePagination ? "true" : "false").then((r) => r.json()),
    ])
      .then(() => {
        setUserData((prev) => ({ ...prev, ageRestriction, cardStyle, enablePagination }));
        window.localStorage.setItem("CardStyle", cardStyle ?? "standard");
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

  const mpaOptions = mpaRatings.map((r) => ({ value: r.id, label: r.name }));

  return (
    <Modal
      open={open}
      onCancel={onClose}
      footer={null}
      width={560}
      wrapClassName="user-settings-modal"
      title={null}
      closeIcon={<span className="settings-modal-close">×</span>}
      getContainer={false}
    >
      <div className="settings-modal-content">
        <h2 className="settings-modal-title">User Settings</h2>

        <div className="settings-section">
          <h3 className="settings-section-title">Content Filtering</h3>
          <div className="settings-row">
            <span className="settings-label">Age Restriction</span>
            <Select
              className="settings-select"
              popupClassName="settings-select-dropdown"
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
              popupClassName="settings-select-dropdown"
              value={cardStyle}
              onChange={(v) => setCardStyle(v)}
              options={cardStyleOptions}
              getPopupContainer={(trigger) => trigger.parentElement}
            />
          </div>
          <p className="settings-hint">Simple shows a compact two-column layout. Standard shows a full row with plot and actors.</p>
          <div className="settings-row" style={{ marginTop: 12 }}>
            <Checkbox checked={enablePagination} onChange={(e) => setEnablePagination(e.target.checked)}>
              Enable Pagination
            </Checkbox>
          </div>
          <p className="settings-hint">If pagination is disabled, all movies will be loaded at once (may be slow for large libraries).</p>
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
