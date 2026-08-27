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
export function NavUserBlock({ userData, onUserLoggedIn, setSettingsModalOpen }) {
  return userData ? (
    <div className="user-panel">
      <UserPanelHeader userData={userData} setSettingsModalOpen={setSettingsModalOpen} />
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

// The A–Z LetterGrid that used to live here is gone. Both rails that had one (boardgames, then
// movies) moved their quick-scroll to the on-page CatalogPager strip, where a letter SCROLLS the
// alphabetical list instead of re-querying it as "titles starting with X" — the music-library
// convention. Nothing replaced it in the rail; the strip lives with the grid it seeks into.
