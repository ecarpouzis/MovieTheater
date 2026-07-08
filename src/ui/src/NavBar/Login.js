import {
  UserOutlined,
  EyeOutlined,
  EyeFilled,
  HeartOutlined,
  HeartFilled,
  StarOutlined,
  StarFilled,
} from "@ant-design/icons";
import { useHistory, useLocation } from "react-router-dom";
import LoginForm from "./LoginForm";
import "./Login.css";

// Function component Login (Movie Theater section only — Board Games / Arcade have their own nav).
// Props:
//   userData / setUserData / onUserLoggedIn — session plumbing
//   setSettingsModalOpen / setAdminModalOpen — modal openers
//   onOpenPlaylists — opens the "My Playlists" modal (streaming accounts only)
function Login({ userData, setUserData, onUserLoggedIn, setSettingsModalOpen, setAdminModalOpen, onOpenPlaylists }) {
  const history = useHistory();
  const location = useLocation();

  function logoutUser() {
    fetch("/API/Logout", { method: "POST" }).finally(() => {
      setUserData();
      window.localStorage.clear();
    });
  }

  function navigateToBrowseSearch(mode) {
    const params = new URLSearchParams();
    params.set("mode", mode);
    history.push({ pathname: "/", search: `?${params.toString()}` });
  }

  // Which stat row (if any) reflects the current view — drives filled vs. outline icon.
  const activeMode = location.pathname === "/" ? new URLSearchParams(location.search).get("mode") : null;
  const seenActive = activeMode === "seen";
  const wantActive = activeMode === "want";
  const rateActive = location.pathname === "/rate";

  function getLoggedInDisplay(userData) {
    return (
      <div className="user-panel">
        <div className="user-panel-header">
          <div className="user-avatar"><UserOutlined /></div>
          <span className="user-username">{userData.username}</span>
          {userData.hasPassword && (
            <button className="playlists-pill" onClick={() => onOpenPlaylists && onOpenPlaylists()} title="My Playlists">
              ≡ Playlists
            </button>
          )}
          <button className="settings-icon-btn" onClick={() => setSettingsModalOpen(true)} title="User Settings">
            ⚙️
          </button>
          {userData.canEditMovies && (
            <button className="settings-icon-btn" onClick={() => history.push("/review-ingest")} title="Library Review">
              🗂️
            </button>
          )}
          {userData.isAdmin && (
            <button className="settings-icon-btn" onClick={() => setAdminModalOpen(true)} title="User Administration">
              🛡️
            </button>
          )}
        </div>
        <div className={`stat-row${seenActive ? " stat-row--active" : ""}`} onClick={() => navigateToBrowseSearch("seen")}>
          {seenActive ? <EyeFilled className="stat-icon stat-icon--seen" /> : <EyeOutlined className="stat-icon stat-icon--seen" />}
          <span className="stat-label">Seen</span>
          <span className="stat-count">{userData.moviesSeen.length}</span>
        </div>
        <div className={`stat-row${wantActive ? " stat-row--active" : ""}`} onClick={() => navigateToBrowseSearch("want")}>
          {wantActive ? <HeartFilled className="stat-icon stat-icon--want" /> : <HeartOutlined className="stat-icon stat-icon--want" />}
          <span className="stat-label">Want to Watch</span>
          <span className="stat-count">{userData.moviesToWatch.length}</span>
        </div>
        <div className={`stat-row${rateActive ? " stat-row--active" : ""}`} onClick={() => history.push("/rate")}>
          {rateActive ? <StarFilled className="stat-icon stat-icon--rate" /> : <StarOutlined className="stat-icon stat-icon--rate" />}
          <span className="stat-label">Rate Movies</span>
          <span className="stat-count">{Object.keys(userData.ratings || {}).length}</span>
        </div>
        <button className="logout-button" onClick={logoutUser}>
          Log Out
        </button>
      </div>
    );
  }

  if (userData) {
    return getLoggedInDisplay(userData);
  }
  return <LoginForm onUserLoggedIn={onUserLoggedIn} />;
}

export default Login;
