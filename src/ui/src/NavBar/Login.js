import {
  EyeOutlined,
  EyeFilled,
  HeartOutlined,
  HeartFilled,
  StarOutlined,
  StarFilled,
} from "@ant-design/icons";
import { Select } from "antd";
import { useHistory, useLocation } from "react-router-dom";
import LoginForm from "./LoginForm";
import UserPanelHeader from "./UserPanelHeader";
import { getPopupContainer } from "./navShared";
import { sameUser, usePeerLists } from "../hooks/useUserLists";
import "./Login.css";

const ME = "";

// The "Lists for" switcher (2026-09-04, the Suggested feature): whose lists the section is on. Me is
// the default; a friend re-scopes the index rows, the rail's flags and the cards' Seen/Want/Suggest
// to THEIR lists (`?for=<username>`). Selecting a person lands on what has been suggested to them —
// the list a suggester most wants to see; Me drops `for=` and keeps whichever list was open.
function ListsForSwitcher({ scoped, userData }) {
  const history = useHistory();
  const location = useLocation();
  const { peers } = usePeerLists(!!userData);
  const value = scoped.me ? ME : (scoped.forUser ?? ME);
  const people = peers.filter((p) => !sameUser(p.username, userData?.username));
  const options = [
    { value: ME, label: <span><span className="lists-for-dot">{(userData?.username || "?")[0].toUpperCase()}</span>Me</span> },
    ...people.map((t) => ({
      value: t.username,
      label: <span><span className="lists-for-dot lists-for-dot--other">{(t.username || "?")[0].toUpperCase()}</span>{t.username}</span>,
    })),
  ];
  // An unknown name in the URL (a typo, a renamed account) still shows what the URL says.
  if (value !== ME && !options.some((o) => o.value === value)) options.push({ value, label: value });

  const onChange = (next) => {
    const params = new URLSearchParams(location.pathname === "/" ? location.search : "");
    params.delete("title");
    if (next === ME) {
      params.delete("for");
    } else {
      // Land on what they want to watch — the list a suggester most wants to see.
      params.set("for", next);
      if (!params.get("my")) params.set("my", "want");
    }
    const search = params.toString();
    history.push({ pathname: "/", search: search ? `?${search}` : "" });
  };

  return (
    <div className={`lists-for${scoped.me ? "" : " lists-for--other"}`}>
      <span className="lists-for-label">Lists for</span>
      <Select
        className="lists-for-select"
        size="small"
        value={value}
        options={options}
        onChange={onChange}
        getPopupContainer={getPopupContainer}
        aria-label="Whose lists"
      />
    </div>
  );
}

// Function component Login (Movie Theater section only — Board Games / Arcade have their own nav).
// Log Out is NOT here: it lives in the shared navbar footer, below the theme toggle, on every page.
// Props:
//   userData / onUserLoggedIn — session plumbing
//   setSettingsModalOpen — opens the user-settings modal
//   onOpenPlaylists — opens the "My Playlists" modal (streaming accounts only)
//   scoped — whose lists the index rows count (hooks/useUserLists); absent = the viewer's own
function Login({ userData, onUserLoggedIn, setSettingsModalOpen, onOpenPlaylists, scoped = null }) {
  const history = useHistory();
  const location = useLocation();
  const me = scoped ? scoped.me : true;
  const forUser = scoped?.forUser ?? null;
  const lists = scoped ? scoped.lists : userData;

  // Which stat row (if any) reflects the current view — drives filled vs. outline icon.
  const myLists = location.pathname === "/" ? (new URLSearchParams(location.search).get("my") || "").split(",") : [];
  const seenActive = myLists.includes("seen");
  const wantActive = myLists.includes("want");
  const rateActive = location.pathname === "/rate";

  // The index rows are the ONE door onto the lists (`my=` — the rail parses it and shows the chip, but
  // draws no section of its own): a row seeds the browse with its list, keeping WHOSE lists (`for=`),
  // and combines with any facet picked afterwards; the active row pressed again clears it.
  function navigateToBrowseSearch(list) {
    const params = new URLSearchParams();
    if (forUser) params.set("for", forUser);
    if (!myLists.includes(list)) params.set("my", list);
    const search = params.toString();
    history.push({ pathname: "/", search: search ? `?${search}` : "" });
  }
  const count = (arr) => (Array.isArray(arr) ? arr.length : "…");

  function getLoggedInDisplay(userData) {
    return (
      <div className="user-panel">
        <UserPanelHeader
          userData={userData}
          setSettingsModalOpen={setSettingsModalOpen}
          onOpenPlaylists={onOpenPlaylists}
        />
        {scoped && <ListsForSwitcher scoped={scoped} userData={userData} />}
        <div className={`stat-row${seenActive ? " stat-row--active" : ""}`} onClick={() => navigateToBrowseSearch("seen")}>
          {seenActive ? <EyeFilled className="stat-icon stat-icon--seen" /> : <EyeOutlined className="stat-icon stat-icon--seen" />}
          <span className="stat-label">Seen</span>
          <span className="stat-count">{count(lists?.moviesSeen)}</span>
        </div>
        <div className={`stat-row${wantActive ? " stat-row--active" : ""}`} onClick={() => navigateToBrowseSearch("want")}>
          {wantActive ? <HeartFilled className="stat-icon stat-icon--want" /> : <HeartOutlined className="stat-icon stat-icon--want" />}
          <span className="stat-label">Want to Watch</span>
          <span className="stat-count">{count(lists?.moviesToWatch)}</span>
        </div>
        {/* Ratings are the viewer's own — a control that does not apply is removed, not disabled. */}
        {me && (
          <div className={`stat-row${rateActive ? " stat-row--active" : ""}`} onClick={() => history.push("/rate")}>
            {rateActive ? <StarFilled className="stat-icon stat-icon--rate" /> : <StarOutlined className="stat-icon stat-icon--rate" />}
            <span className="stat-label">Rate Movies</span>
            <span className="stat-count">{Object.keys(userData.ratings || {}).length}</span>
          </div>
        )}
      </div>
    );
  }

  if (userData) {
    return getLoggedInDisplay(userData);
  }
  return <LoginForm onUserLoggedIn={onUserLoggedIn} />;
}

export default Login;
