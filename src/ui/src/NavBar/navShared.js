import { Button } from "antd";
import { useHistory, useLocation } from "react-router-dom";
import LoginForm from "./LoginForm";
import UserPanelHeader from "./UserPanelHeader";

// Pieces every section rail repeats. These existed as four hand-kept copies (movies, boardgames,
// arcade, music) before being consolidated here — a new section's rail should start from these.

// The 10px uppercase field label above each rail input.
export const inputLabelStyle = {
  display: "block",
  fontSize: "10px",
  fontWeight: "600",
  color: "var(--sidebar-text-muted)",
  textTransform: "uppercase",
  letterSpacing: "0.8px",
  marginBottom: "5px",
  marginTop: "14px",
};

// Popups portal to the field's parent so the sider's data-feature tokens apply (NavBar.css skins
// them via .nav-dropdown). parentElement, not parentNode: same node here, but one spelling.
export const getPopupContainer = (trigger) => trigger.parentElement;

// The user-panel-or-login block every rail opens with. Log Out is not here — it lives in the
// shared navbar footer, below the theme toggle.
export function NavUserBlock({ userData, onUserLoggedIn, setSettingsModalOpen, setAdminModalOpen }) {
  return userData ? (
    <div className="user-panel">
      <UserPanelHeader
        userData={userData}
        setSettingsModalOpen={setSettingsModalOpen}
        setAdminModalOpen={setAdminModalOpen}
      />
    </div>
  ) : (
    <LoginForm onUserLoggedIn={onUserLoggedIn} />
  );
}

// URL-param writer bound to a section's base pathname: setting a param to null/"" clears it, and
// clearKeys lets a caller drop dependent params in the same navigation (music clears ?artist when
// the view or shelf changes).
export function useSectionParams(pathname) {
  const history = useHistory();
  const location = useLocation();
  return function updateParam(key, value, clearKeys = []) {
    const params = new URLSearchParams(location.search);
    if (value != null && value !== "") params.set(key, value);
    else params.delete(key);
    for (const k of clearKeys) params.delete(k);
    history.push({ pathname, search: params.toString() ? `?${params.toString()}` : "" });
  };
}

export const searchLetters = [
  "#", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
  "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
];

// The absolutely-centered glyph inside a 36px .search-letter-btn square (the class carries the
// square + position:relative in index.css).
const searchLetterStyle = {
  fontWeight: "bold",
  position: "absolute",
  width: "100%",
  height: "1em",
  lineHeight: "1em",
  top: "50%",
  left: "0px",
  marginTop: "-0.5em",
};

// The first-letter grid (movies + boardgames rails). Plain CSS grid (.letter-grid in index.css) —
// this was an antd <List grid>, which v6 deprecated and v7 removes. `active` is the currently
// toggled letter (or falsy); onToggle receives the tapped letter and owns the toggle-off logic.
export function LetterGrid({ active, onToggle }) {
  return (
    <div className="letter-grid" style={{ paddingBottom: "20px" }}>
      {searchLetters.map((item) => (
        <Button
          key={item}
          className="search-letter-btn"
          onClick={() => onToggle(item)}
          style={{
            width: "36px",
            backgroundColor: item === active ? "var(--accent)" : "var(--sidebar-pill-bg)",
            color: item === active ? "#fff" : "var(--sidebar-text-muted)",
            borderColor: item === active ? "var(--accent)" : "var(--sidebar-input-border)",
          }}
        >
          <span style={searchLetterStyle}>{item}</span>
        </Button>
      ))}
    </div>
  );
}
