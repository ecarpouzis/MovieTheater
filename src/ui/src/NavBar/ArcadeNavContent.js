import { useState, useEffect } from "react";
import { Input, Select } from "antd";
import { SearchOutlined } from "@ant-design/icons";
import { useHistory, useLocation } from "react-router-dom";
import { MovieAPI } from "../MovieAPI";
import { systemLabel } from "../Pages/Arcade/arcadeSystems";
import LoginForm from "./LoginForm";
import UserPanelHeader from "./UserPanelHeader";

const inputLabelStyle = {
  display: "block", fontSize: "10px", fontWeight: 600, color: "var(--sidebar-text-muted)",
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
  { value: "release", label: "Official releases" },
  { value: "all", label: "All (include mods)" },
  { value: "modded", label: "Mods & hacks only" },
];

const sortOptions = [
  { value: "", label: "A–Z (title)" },
  { value: "rating", label: "Rating (high → low)" },
  { value: "year", label: "Release date (new → old)" },
  { value: "system", label: "System" },
  { value: "players", label: "Player count (most first)" },
];

// Log Out is not here — it lives in the shared navbar footer, below the theme toggle.
function ArcadeUserPanel({ userData, setSettingsModalOpen, setAdminModalOpen }) {
  return (
    <div className="user-panel">
      <UserPanelHeader userData={userData} setSettingsModalOpen={setSettingsModalOpen} setAdminModalOpen={setAdminModalOpen} />
    </div>
  );
}

// The arcade section's navbar filter panel (mirrors BoardGameNavContent): filters are URL params on
// /arcade so ArcadePage can fetch server-side (system, region, players, variant, q). Facets come from
// /API/Arcade/Filters so the System/Region dropdowns show exactly what's available, with counts.
function ArcadeNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, setAdminModalOpen }) {
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
  const activeRegion = p.get("region") || "english";
  const activePlayers = p.get("players") || "";
  const activeVariant = p.get("variant") || "release";
  const activeGenre = p.get("genre") || "";
  const activeSort = p.get("sort") || "";

  const systemOptions = [
    { value: "", label: facets ? `All systems (${facets.total})` : "All systems" },
    ...((facets?.systems || []).map((s) => ({ value: s.value, label: `${systemLabel(s.value)} (${s.count})` }))),
  ];
  const regionOptions = [
    { value: "english", label: "English (default)" },
    { value: "all", label: "All regions" },
    ...((facets?.regions || []).map((r) => ({ value: r.value, label: `${r.value} (${r.count})` }))),
  ];
  const genreOptions = [
    { value: "", label: "All genres" },
    ...((facets?.genres || []).map((g) => ({ value: g.value, label: `${g.value} (${g.count})` }))),
  ];

  return (
    <>
      {userData ? (
        <ArcadeUserPanel userData={userData} setSettingsModalOpen={setSettingsModalOpen} setAdminModalOpen={setAdminModalOpen} />
      ) : (
        <LoginForm onUserLoggedIn={onUserLoggedIn} popupClassName="arcade-login-dropdown" />
      )}

      <div id="SearchToolContainer" style={{ padding: "8px 16px 24px", color: "white" }}>
        <span className="arcade-filter-heading">FILTER LIBRARY</span>
        {/* A magnifier INSIDE the field, per the design — antd's <Input.Search> always renders a
            separate addon button, which this rail doesn't want. Enter searches; the clear "×"
            (or emptying the box and pressing Enter) drops the filter. */}
        <Input
          placeholder="Search title…"
          prefix={<SearchOutlined />}
          allowClear
          defaultValue={p.get("q") || ""}
          style={{ width: "100%" }}
          onPressEnter={(e) => updateParam("q", e.target.value.trim())}
          onChange={(e) => { if (!e.target.value) updateParam("q", ""); }}
        />

        <span style={inputLabelStyle}>Sort by</span>
        <Select style={{ width: "100%" }} value={activeSort} onChange={(v) => updateParam("sort", v)}
          options={sortOptions} popupClassName="arcade-login-dropdown" getPopupContainer={getPopup} />

        <span style={inputLabelStyle}>System</span>
        <Select style={{ width: "100%" }} value={activeSystem} onChange={(v) => updateParam("system", v)}
          options={systemOptions} popupClassName="arcade-login-dropdown" getPopupContainer={getPopup} />

        <span style={inputLabelStyle}>Region</span>
        <Select style={{ width: "100%" }} value={activeRegion} onChange={(v) => updateParam("region", v)}
          options={regionOptions} popupClassName="arcade-login-dropdown" getPopupContainer={getPopup} />

        <span style={inputLabelStyle}>Players</span>
        <Select style={{ width: "100%" }} value={activePlayers} onChange={(v) => updateParam("players", v)}
          options={playerOptions} popupClassName="arcade-login-dropdown" getPopupContainer={getPopup} />

        <span style={inputLabelStyle}>Genre</span>
        <Select style={{ width: "100%" }} value={activeGenre} onChange={(v) => updateParam("genre", v)}
          options={genreOptions} showSearch optionFilterProp="label"
          popupClassName="arcade-login-dropdown" getPopupContainer={getPopup} />

        <span style={inputLabelStyle}>Mods &amp; Hacks</span>
        <Select style={{ width: "100%" }} value={activeVariant} onChange={(v) => updateParam("variant", v)}
          options={modOptions} popupClassName="arcade-login-dropdown" getPopupContainer={getPopup} />

        {(activeSystem || activeRegion !== "english" || activePlayers || activeVariant !== "release" || activeGenre || activeSort || p.get("q")) && (
          <button type="button" className="arcade-clear-filters" onClick={() => history.push({ pathname: "/arcade" })}>
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
              <path d="M18 6 6 18M6 6l12 12" />
            </svg>
            Clear filters
          </button>
        )}
      </div>
    </>
  );
}

export default ArcadeNavContent;
