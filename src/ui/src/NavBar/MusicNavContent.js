import { Input } from "antd";
import { useHistory, useLocation } from "react-router-dom";
import LoginForm from "./LoginForm";
import UserPanelHeader from "./UserPanelHeader";

const { Search } = Input;

const inputLabelStyle = {
  display: "block",
  fontSize: "10px",
  fontWeight: "600",
  color: "var(--sidebar-text-muted)",
  textTransform: "uppercase",
  letterSpacing: "0.8px",
  marginBottom: "5px",
  marginTop: "14px",
};

// Music rail (music-plan.md §2.6): search + the artists/albums view toggle. Filters live in the
// URL (?view=, ?q=) — the arcade convention — so back/forward and reloads restore the same view.
function MusicNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, setAdminModalOpen }) {
  const history = useHistory();
  const location = useLocation();

  const params = new URLSearchParams(location.search);
  const activeView = params.get("view") === "artists" ? "artists" : "albums";
  const activeQ = params.get("q") || "";

  function updateParam(key, value) {
    const p = new URLSearchParams(location.search);
    if (value != null && value !== "") p.set(key, value);
    else p.delete(key);
    if (key === "view") p.delete("artist"); // leaving a drilled-in artist when switching views
    history.push({ pathname: "/music", search: p.toString() ? `?${p.toString()}` : "" });
  }

  const viewButtonStyle = (view) => ({
    flex: 1,
    padding: "6px 0",
    borderRadius: "6px",
    border: "1px solid var(--sidebar-input-border)",
    cursor: "pointer",
    fontSize: "12px",
    background: activeView === view ? "var(--accent)" : "var(--sidebar-pill-bg)",
    color: activeView === view ? "#fff" : "var(--sidebar-text-muted)",
  });

  return (
    <>
      {userData ? (
        <div className="user-panel">
          <UserPanelHeader userData={userData} setSettingsModalOpen={setSettingsModalOpen} setAdminModalOpen={setAdminModalOpen} />
        </div>
      ) : (
        <LoginForm onUserLoggedIn={onUserLoggedIn} />
      )}

      <div style={{ padding: "16px 16px 8px", borderTop: "1px solid var(--sidebar-border)" }}>
        <span style={{ ...inputLabelStyle, marginTop: 0 }}>Search</span>
        <form onSubmit={(e) => e.preventDefault()}>
          <Search
            placeholder="Artist, album, song"
            style={{ width: "100%" }}
            enterKeyHint="search"
            defaultValue={activeQ}
            allowClear
            onSearch={(v) => updateParam("q", v && v.trim())}
            enterButton
          />
        </form>

        <span style={inputLabelStyle}>Browse</span>
        <div style={{ display: "flex", gap: "6px" }}>
          <button style={viewButtonStyle("albums")} onClick={() => updateParam("view", null)}>
            Albums
          </button>
          <button style={viewButtonStyle("artists")} onClick={() => updateParam("view", "artists")}>
            Artists
          </button>
        </div>
      </div>
    </>
  );
}

export default MusicNavContent;
