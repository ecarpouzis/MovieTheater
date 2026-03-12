import { useEffect, useState } from "react";
import { Select } from "antd";
import { useHistory } from "react-router-dom";
import { MovieAPI } from "../MovieAPI";
import "./UserSettingsPage.css";

function UserSettingsPage({ userData, setUserData }) {
  const history = useHistory();
  const [mpaRatings, setMpaRatings] = useState([]);
  const [ageRestriction, setAgeRestriction] = useState(undefined);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    if (!userData) {
      history.push("/");
      return;
    }
    MovieAPI.getMPARatings()
      .then((r) => {
        if (!r.ok) throw new Error(`GetMPARatings failed: HTTP ${r.status}`);
        return r.json();
      })
      .then((data) => {
        setMpaRatings(Array.isArray(data) ? data : []);
        setAgeRestriction(userData.ageRestriction ?? undefined);
      });
  }, [userData, history]);

  const handleSave = () => {
    setSaving(true);
    setSaved(false);
    const value = ageRestriction != null ? String(ageRestriction) : null;
    MovieAPI.setUserSetting("AgeRestriction", value)
      .then((r) => r.json())
      .then(() => {
        setUserData((prev) => ({ ...prev, ageRestriction }));
        setSaved(true);
      })
      .finally(() => setSaving(false));
  };

  const options = mpaRatings.map((r) => ({ value: r.id, label: r.name }));

  return (
    <div className="settings-page">
      <h2 className="settings-title">User Settings</h2>
      <div className="settings-section">
        <h3 className="settings-section-title">Content Filtering</h3>
        <div className="settings-row">
          <span className="settings-label">Age Restriction</span>
          <Select
            allowClear
            className="settings-select"
            value={ageRestriction}
            onChange={(v) => {
              setAgeRestriction(v);
              setSaved(false);
            }}
            options={options}
            placeholder="No Restriction"
          />
        </div>
        <p className="settings-hint">Movies above this MPA rating will be hidden from all views.</p>
      </div>
      <button className="settings-save-btn" onClick={handleSave} disabled={saving}>
        {saving ? "Saving…" : "Save Settings"}
      </button>
      {saved && <span className="settings-saved-msg">? Saved</span>}
    </div>
  );
}

export default UserSettingsPage;
