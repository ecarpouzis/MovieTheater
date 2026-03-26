import { useEffect, useState } from "react";
import { Select, Checkbox } from "antd";
import { useHistory } from "react-router-dom";
import { MovieAPI } from "../MovieAPI";
import "./UserSettingsPage.css";

const cardStyleOptions = [
  { value: "standard", label: "Standard" },
  { value: "simple", label: "Simple" },
];

function UserSettingsPage({ userData, setUserData }) {
  const history = useHistory();
  const [mpaRatings, setMpaRatings] = useState([]);
  const [ageRestriction, setAgeRestriction] = useState(undefined);
  const [cardStyle, setCardStyle] = useState("standard");
  const [canEditMovies, setCanEditMovies] = useState(false);
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
        setCardStyle(userData.cardStyle ?? "standard");
        setCanEditMovies(userData.canEditMovies ?? false);
      });
  }, [userData, history]);

  const handleSave = () => {
    setSaving(true);
    setSaved(false);
    const ageValue = ageRestriction != null ? String(ageRestriction) : null;
    Promise.all([
      MovieAPI.setUserSetting("AgeRestriction", ageValue).then((r) => r.json()),
      MovieAPI.setUserSetting("CardStyle", cardStyle).then((r) => r.json()),
    ])
      .then(() => {
        setUserData((prev) => ({ ...prev, ageRestriction, cardStyle }));
        window.localStorage.setItem("CardStyle", cardStyle ?? "standard");
        setSaved(true);
      })
      .finally(() => setSaving(false));
  };

  const mpaOptions = mpaRatings.map((r) => ({ value: r.id, label: r.name }));

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
            options={mpaOptions}
            placeholder="No Restriction"
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
            value={cardStyle}
            onChange={(v) => {
              setCardStyle(v);
              setSaved(false);
            }}
            options={cardStyleOptions}
          />
        </div>
        <p className="settings-hint">Simple shows a compact two-column layout. Standard shows a full row with plot and actors.</p>
      </div>
      <div className="settings-section">
        <h3 className="settings-section-title">Permissions</h3>
        <div className="settings-row">
          <Checkbox
            checked={canEditMovies}
            disabled
          >
            <span style={{ color: "inherit" }}>Can Edit Movies</span>
          </Checkbox>
        </div>
        <p className="settings-hint">This permission is managed by an administrator.</p>
      </div>
      <button className="settings-save-btn" onClick={handleSave} disabled={saving}>
        {saving ? "Saving…" : "Save Settings"}
      </button>
      {saved && <span className="settings-saved-msg">? Saved</span>}
    </div>
  );
}

export default UserSettingsPage;
