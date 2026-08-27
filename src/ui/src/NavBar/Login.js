import {
  EyeOutlined,
  EyeFilled,
  HeartOutlined,
  HeartFilled,
  StarOutlined,
  StarFilled,
} from "@ant-design/icons";
import { useHistory, useLocation } from "react-router-dom";
import LoginForm from "./LoginForm";
import UserPanelHeader from "./UserPanelHeader";
import "./Login.css";

// Function component Login (Movie Theater section only — Board Games / Arcade have their own nav).
// Log Out is NOT here: it lives in the shared navbar footer, below the theme toggle, on every page.
// Props:
//   userData / onUserLoggedIn — session plumbing
//   setSettingsModalOpen / setAdminModalOpen — modal openers
//   onOpenPlaylists — opens the "My Playlists" modal (streaming accounts only)
function Login({ userData, onUserLoggedIn, setSettingsModalOpen, setAdminModalOpen, onOpenPlaylists }) {
  const history = useHistory();
  const location = useLocation();

  // The index rows seed the browse with the viewer's list (`my=` — the facet rail's own flag, so the
  // rail shows it checked and it combines with any facet picked afterwards).
  function navigateToBrowseSearch(list) {
    history.push({ pathname: "/", search: `?my=${list}` });
  }

  // Which stat row (if any) reflects the current view — drives filled vs. outline icon.
  const myLists = location.pathname === "/" ? (new URLSearchParams(location.search).get("my") || "").split(",") : [];
  const seenActive = myLists.includes("seen");
  const wantActive = myLists.includes("want");
  const rateActive = location.pathname === "/rate";

  function getLoggedInDisplay(userData) {
    return (
      <div className="user-panel">
        <UserPanelHeader
          userData={userData}
          setSettingsModalOpen={setSettingsModalOpen}
          setAdminModalOpen={setAdminModalOpen}
          onOpenPlaylists={onOpenPlaylists}
        />
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
      </div>
    );
  }

  if (userData) {
    return getLoggedInDisplay(userData);
  }
  return <LoginForm onUserLoggedIn={onUserLoggedIn} />;
}

export default Login;
