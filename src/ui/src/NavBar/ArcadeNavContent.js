import { useState, useEffect } from "react";
import { Input, Button, AutoComplete, Tooltip, Select } from "antd";
import { InfoCircleOutlined, UserOutlined } from "@ant-design/icons";
import { useHistory, useLocation } from "react-router-dom";
import { MovieAPI } from "../MovieAPI";

const { Search } = Input;

// Friendly system labels (mirror ArcadePage). The facet endpoint returns the raw codes.
const SYSTEM_LABEL = {
  nes: "NES", snes: "SNES", genesis: "Genesis", gb: "Game Boy", gbc: "Game Boy Color",
  gba: "Game Boy Advance", n64: "Nintendo 64", ps1: "PlayStation", arcade: "Arcade",
};
const systemLabel = (s) => SYSTEM_LABEL[s] || (s ? s.toUpperCase() : "");

const sectionHeaderStyle = {
  display: "block", fontSize: "10px", fontWeight: 700, color: "#d8a7ff",
  textTransform: "uppercase", letterSpacing: "1.5px", marginBottom: "12px",
  paddingBottom: "8px", borderBottom: "1px solid #4a2d6b",
};
const inputLabelStyle = {
  display: "block", fontSize: "10px", fontWeight: 600, color: "#c3a3e6",
  textTransform: "uppercase", letterSpacing: "0.8px", marginBottom: "5px", marginTop: "14px",
};

const playerOptions = [
  { value: "", label: "Any player count" },
  { value: "2", label: "2+ players" },
  { value: "3", label: "3+ players" },
  { value: "4", label: "4+ players" },
  { value: "5", label: "5 players" },
];

const modOptions = [
  { value: "", label: "All games" },
  { value: "release", label: "Official releases only" },
  { value: "modded", label: "Mods & hacks only" },
];

function ArcadeUserPanel({ userData, setSettingsModalOpen, setAdminModalOpen, setUserData }) {
  const history = useHistory();
  function logout() {
    fetch("/API/Logout", { method: "POST" }).finally(() => {
      setUserData();
      window.localStorage.clear();
    });
  }
  return (
    <div className="user-panel">
      <div className="user-panel-header">
        <div className="user-avatar"><UserOutlined /></div>
        <span className="user-username">{userData.username}</span>
        <button className="settings-icon-btn" onClick={() => setSettingsModalOpen(true)} title="User Settings">⚙️</button>
        {userData.canEditMovies && (
          <button className="settings-icon-btn" onClick={() => history.push("/review-ingest")} title="Library Review">🗂️</button>
        )}
        {userData.isAdmin && (
          <button className="settings-icon-btn" onClick={() => setAdminModalOpen(true)} title="User Administration">🛡️</button>
        )}
      </div>
      <button className="logout-button" onClick={logout}>Log Out</button>
    </div>
  );
}

function ArcadeLoginForm({ onUserLoggedIn }) {
  const [userlist, setUserlist] = useState([]);
  const [filtered, setFiltered] = useState([]);
  const [inputValue, setInputValue] = useState(null);

  useEffect(() => {
    MovieAPI.getUsers().then((r) => r.json()).then((data) => {
      const mapped = data.map((x) => ({ value: x }));
      setUserlist(mapped);
      setFiltered(mapped);
    });
  }, []);

  function handleLogin() {
    const match = userlist.find((x) => x.value === inputValue);
    if (match) onUserLoggedIn(match.value);
  }

  return (
    <div id="LoginContainer" className="login-container">
      <span className="login-title">LOG IN</span>
      <br /><br />
      <AutoComplete
        options={filtered}
        className="login-autocomplete"
        popupClassName="login-user-dropdown arcade-login-dropdown"
        onSelect={onUserLoggedIn}
        onSearch={(v) => setFiltered(userlist.filter((e) => e.value.toLowerCase().includes(v.toLowerCase())))}
        getPopupContainer={(t) => t.parentElement}
      >
        <div style={{ display: "flex", gap: 0, alignItems: "stretch" }}>
          <Input
            placeholder="Username"
            prefix={<UserOutlined />}
            className="login-input"
            onChange={(e) => setInputValue(e.target.value)}
            value={inputValue}
            suffix={
              <Tooltip title="This website purposely requires no password to log in.">
                <InfoCircleOutlined className="login-tooltip-icon" />
              </Tooltip>
            }
          />
          <Button type="primary" className="login-button" onClick={handleLogin}>{">"}</Button>
        </div>
      </AutoComplete>
    </div>
  );
}

// The arcade section's navbar filter panel (mirrors BoardGameNavContent): filters are URL params on
// /arcade so ArcadePage can fetch server-side (system, region, players, variant, q). Facets come from
// /API/Arcade/Filters so the System/Region dropdowns show exactly what's available, with counts.
function ArcadeNavContent({ userData, setUserData, onUserLoggedIn, setSettingsModalOpen, setAdminModalOpen }) {
  const history = useHistory();
  const location = useLocation();
  const [facets, setFacets] = useState(null);
  const getPopup = (t) => t.parentElement;

  useEffect(() => {
    let alive = true;
    MovieAPI.getArcadeFilters()
      .then((r) => (r.ok ? r.json() : null))
      .then((f) => { if (alive) setFacets(f); })
      .catch(() => {});
    return () => { alive = false; };
  }, []);

  function updateParam(key, value) {
    const params = new URLSearchParams(location.search);
    if (value != null && value !== "") params.set(key, value); else params.delete(key);
    history.push({ pathname: "/arcade", search: params.toString() ? `?${params.toString()}` : "" });
  }

  const p = new URLSearchParams(location.search);
  const activeSystem = p.get("system") || "";
  const activeRegion = p.get("region") || "";
  const activePlayers = p.get("players") || "";
  const activeVariant = p.get("variant") || "";

  const systemOptions = [
    { value: "", label: facets ? `All systems (${facets.total})` : "All systems" },
    ...((facets?.systems || []).map((s) => ({ value: s.value, label: `${systemLabel(s.value)} (${s.count})` }))),
  ];
  const regionOptions = [
    { value: "", label: "Any region" },
    ...((facets?.regions || []).map((r) => ({ value: r.value, label: `${r.value} (${r.count})` }))),
  ];

  return (
    <>
      {userData ? (
        <ArcadeUserPanel userData={userData} setUserData={setUserData} setSettingsModalOpen={setSettingsModalOpen} setAdminModalOpen={setAdminModalOpen} />
      ) : (
        <ArcadeLoginForm onUserLoggedIn={onUserLoggedIn} />
      )}

      <div style={{ padding: "16px 16px 24px", color: "white", borderTop: "1px solid #4a2d6b" }}>
        <span style={sectionHeaderStyle}>Filter Games</span>

        <span style={{ ...inputLabelStyle, marginTop: 0 }}>Title</span>
        <Search
          placeholder="Search title"
          defaultValue={p.get("q") || ""}
          style={{ width: "100%" }}
          onSearch={(v) => updateParam("q", v && v.trim() ? v.trim() : "")}
          enterButton
        />

        <span style={inputLabelStyle}>System</span>
        <Select style={{ width: "100%" }} value={activeSystem} onChange={(v) => updateParam("system", v)}
          options={systemOptions} popupClassName="arcade-login-dropdown" getPopupContainer={getPopup} />

        <span style={inputLabelStyle}>Region</span>
        <Select style={{ width: "100%" }} value={activeRegion} onChange={(v) => updateParam("region", v)}
          options={regionOptions} popupClassName="arcade-login-dropdown" getPopupContainer={getPopup} />

        <span style={inputLabelStyle}>Players</span>
        <Select style={{ width: "100%" }} value={activePlayers} onChange={(v) => updateParam("players", v)}
          options={playerOptions} popupClassName="arcade-login-dropdown" getPopupContainer={getPopup} />

        <span style={inputLabelStyle}>Mods &amp; Hacks</span>
        <Select style={{ width: "100%" }} value={activeVariant} onChange={(v) => updateParam("variant", v)}
          options={modOptions} popupClassName="arcade-login-dropdown" getPopupContainer={getPopup} />

        {(activeSystem || activeRegion || activePlayers || activeVariant || p.get("q")) && (
          <Button
            block
            style={{ marginTop: 18, background: "rgba(199,64,224,0.12)", borderColor: "rgba(199,64,224,0.4)", color: "#e0b6ff" }}
            onClick={() => history.push({ pathname: "/arcade" })}
          >
            Clear filters
          </Button>
        )}
      </div>
    </>
  );
}

export default ArcadeNavContent;
