import { useState, useEffect, useRef } from "react";
import { Input, Select } from "antd";
import { SearchOutlined } from "@ant-design/icons";
import { useHistory, useLocation } from "react-router-dom";
import { systemLabel } from "../Pages/Arcade/arcadeSystems";
import { parseSystems, serializeSystems } from "../Pages/Arcade/arcadeSystemFilter";
import useArcadeFilters from "../Pages/Arcade/useArcadeFilters";
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
  { value: "all", label: "All Games (default)" },
  { value: "release", label: "Official releases only" },
  { value: "modded", label: "Mods & hacks only" },
  { value: "romhacks", label: "Our Romhacks (curated)" },
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
  const [query, setQuery] = useState(() => new URLSearchParams(location.search).get("q") || "");
  const searchRef = useRef(null);
  const getPopup = (t) => t.parentElement;

  function updateParam(key, value) {
    const params = new URLSearchParams(location.search);
    if (value != null && value !== "") params.set(key, value); else params.delete(key);
    history.push({ pathname: "/arcade", search: params.toString() ? `?${params.toString()}` : "" });
  }

  const p = new URLSearchParams(location.search);

  // Facets are FACETED against the current scope (so e.g. a Japan-only system isn't offered under the
  // default English region). The shared hook keys on exactly the scope-affecting params — not sort or
  // paging, which don't change what's available — and dedupes the request with the browse page's
  // console carousel, which needs the identical response.
  const facets = useArcadeFilters({
    system: p.get("system") || "",
    hideRegions: p.get("hideRegions") || "",
    maxPlayers: p.get("players") || "",
    variant: p.get("variant") || "",
    genre: p.get("genre") || "",
    search: p.get("q") || "",
    ra: p.get("ra") || "",
  });

  const activeQuery = p.get("q") || "";
  // Follow the URL when q changes from anywhere but this box — Clear filters, browser back, or coming
  // back out of a room with the filters restored. (The old uncontrolled field kept showing stale text.)
  useEffect(() => { setQuery(activeQuery); }, [activeQuery]);

  const activeSystems = parseSystems(p);
  const activePlayers = p.get("players") || "";
  const activeVariant = p.get("variant") || "all";
  const activeGenre = p.get("genre") || "";
  const activeRa = p.get("ra") || "";
  const activeSort = p.get("sort") || "";

  // RetroAchievements filter: find games that track achievements / have high-score or speedrun boards.
  // Counts are faceted (they exclude the RA filter itself), so each label shows how many games qualify.
  const raOptions = [
    { value: "", label: "Any RA support" },
    { value: "achievements", label: `🏆 Achievements${facets?.ra ? ` (${facets.ra.achievements})` : ""}` },
    { value: "highscores", label: `🥇 High scores${facets?.ra ? ` (${facets.ra.highScores})` : ""}` },
    { value: "speedruns", label: `⏱️ Speedruns${facets?.ra ? ` (${facets.ra.speedruns})` : ""}` },
  ];

  // System is a multi-select sharing the ?system= param with the browse page's console carousel:
  // picking several shows every one of them (a union), and picking none means all. There is no
  // "All systems" OPTION any more — an empty box IS all systems — so the total moves into the label,
  // where it still answers "how big is the catalog?" without pretending to be a selectable value.
  const systemOptions = (facets?.systems || [])
    .map((s) => ({ value: s.value, label: `${systemLabel(s.value)} (${s.count})` }));
  // Region is a DESELECT multi-select: every KNOWN region (the server omits Unknown/NULL) starts selected,
  // and turning one OFF hides cards whose versions are ALL from switched-off regions. The URL carries only
  // the OFF set (hideRegions); empty = everything shown. Unknown-region cards are never hidden.
  const knownRegions = (facets?.regions || []).map((r) => r.value);
  const hiddenRegions = (p.get("hideRegions") || "").split(",").map((s) => s.trim()).filter(Boolean);
  const selectedRegions = knownRegions.filter((r) => !hiddenRegions.includes(r));
  const regionOptions = (facets?.regions || []).map((r) => ({ value: r.value, label: `${r.value} (${r.count})` }));
  const onRegionChange = (selected) => {
    const nowHidden = knownRegions.filter((r) => !selected.includes(r));
    updateParam("hideRegions", nowHidden.length ? nowHidden.join(",") : "");
  };
  const genreOptions = [
    { value: "", label: "All genres" },
    ...((facets?.genres || []).map((g) => ({ value: g.value, label: `${g.value} (${g.count})` }))),
  ];

  function submitSearch(e) {
    e.preventDefault();
    // Dismiss the on-screen keyboard — on a tablet the rail is a drawer that closes on navigation, so
    // leaving the field focused parks the keyboard over the results the user just asked for.
    searchRef.current?.blur();
    updateParam("q", query.trim());
  }

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
            (or emptying the box) drops the filter.

            The <form> is load-bearing, not decoration. A bare input followed by more focusable fields
            makes a tablet keyboard render its Enter key as "Next" — it moves focus to the Sort/System
            dropdown instead of searching. A single-field form gets implicit submission, so the key
            becomes "Go"/"Search" (enterKeyHint names it) and Enter runs the search. */}
        <form onSubmit={submitSearch}>
          <Input
            ref={searchRef}
            placeholder="Search title…"
            prefix={<SearchOutlined />}
            allowClear
            enterKeyHint="search"
            value={query}
            style={{ width: "100%" }}
            onChange={(e) => {
              setQuery(e.target.value);
              if (!e.target.value && activeQuery) updateParam("q", "");
            }}
          />
        </form>

        <span style={inputLabelStyle}>Sort by</span>
        <Select style={{ width: "100%" }} value={activeSort} onChange={(v) => updateParam("sort", v)}
          options={sortOptions} classNames={{ popup: { root: "arcade-login-dropdown" } }} getPopupContainer={getPopup} />

        <span style={inputLabelStyle}>
          System{facets ? <span style={{ opacity: 0.6, fontWeight: 400 }}> — all {facets.total} by default</span> : null}
        </span>
        <Select mode="multiple" allowClear style={{ width: "100%" }} value={activeSystems}
          onChange={(v) => updateParam("system", serializeSystems(v))}
          options={systemOptions} placeholder="All systems" maxTagCount="responsive"
          showSearch optionFilterProp="label"
          classNames={{ popup: { root: "arcade-login-dropdown" } }} getPopupContainer={getPopup} />

        <span style={inputLabelStyle}>Region <span style={{ opacity: 0.6, fontWeight: 400 }}>— deselect to hide</span></span>
        <Select mode="multiple" allowClear style={{ width: "100%" }} value={selectedRegions}
          onChange={onRegionChange} options={regionOptions} placeholder="All regions" maxTagCount="responsive"
          classNames={{ popup: { root: "arcade-login-dropdown" } }} getPopupContainer={getPopup} />

        <span style={inputLabelStyle}>Players</span>
        <Select style={{ width: "100%" }} value={activePlayers} onChange={(v) => updateParam("players", v)}
          options={playerOptions} classNames={{ popup: { root: "arcade-login-dropdown" } }} getPopupContainer={getPopup} />

        <span style={inputLabelStyle}>Genre</span>
        <Select style={{ width: "100%" }} value={activeGenre} onChange={(v) => updateParam("genre", v)}
          options={genreOptions} showSearch optionFilterProp="label"
          classNames={{ popup: { root: "arcade-login-dropdown" } }} getPopupContainer={getPopup} />

        <span style={inputLabelStyle}>Mods &amp; Hacks</span>
        {/* "all" is the default, so choosing it CLEARS the param rather than writing ?variant=all — a
            URL that says it carries a filter when it doesn't, and which the API drops anyway. */}
        <Select style={{ width: "100%" }} value={activeVariant} onChange={(v) => updateParam("variant", v === "all" ? "" : v)}
          options={modOptions} classNames={{ popup: { root: "arcade-login-dropdown" } }} getPopupContainer={getPopup} />

        <span style={inputLabelStyle}>RetroAchievements</span>
        <Select style={{ width: "100%" }} value={activeRa} onChange={(v) => updateParam("ra", v)}
          options={raOptions} classNames={{ popup: { root: "arcade-login-dropdown" } }} getPopupContainer={getPopup} />

        {/* Region counts as an active filter when any region has been switched OFF — under the deselect
            model "everything selected" IS the default (there is no activeRegion any more). */}
        {(activeSystems.length > 0 || hiddenRegions.length > 0 || activePlayers || activeVariant !== "all" || activeGenre || activeRa || activeSort || p.get("q")) && (
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
