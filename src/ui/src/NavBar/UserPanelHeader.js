import { useState } from "react";
import { UserOutlined, SettingOutlined } from "@ant-design/icons";
import { useHistory } from "react-router-dom";

// Shared logged-in header row for every feature's sidebar: avatar + username, an optional Playlists
// pill (Movies only), and a single gear that opens a small menu holding User Settings and shortcuts
// into the section's admin tabs. Those tools used to be their own always-visible icons, which
// crowded the 200px header and — once the Playlists pill was added — overflowed and hid the
// admin/review buttons entirely. Folding them behind the gear keeps them one click away without a
// dedicated row.
//
// R9 S6: "User Administration" was a MODAL opened from here; it is the Users tab of /movies/admin
// now, so both entries are plain links into the one admin shell.
function UserPanelHeader({ userData, setSettingsModalOpen, onOpenPlaylists }) {
  const history = useHistory();
  const [menuOpen, setMenuOpen] = useState(false);
  const close = () => setMenuOpen(false);

  const showPlaylists = typeof onOpenPlaylists === "function" && userData.hasPassword;

  return (
    <div className="user-panel-header">
      <div className="user-avatar"><UserOutlined /></div>
      <span className="user-username" title={userData.username}>{userData.username}</span>
      {showPlaylists && (
        <button className="playlists-pill" onClick={onOpenPlaylists} title="My Playlists">≡ Playlists</button>
      )}
      <div className="user-menu-wrapper">
        <button
          className="settings-icon-btn"
          onClick={() => setMenuOpen((o) => !o)}
          title="Account & tools"
          aria-label="Account menu"
        >
          <SettingOutlined />
        </button>
        {menuOpen && (
          <>
            <div className="user-menu-overlay" onClick={close} />
            <div className="user-menu">
              <button className="user-menu-item" onClick={() => { close(); setSettingsModalOpen(true); }}>
                User Settings
              </button>
              {(userData.canEditMovies || userData.isAdmin) && (
                <button className="user-menu-item" onClick={() => { close(); history.push("/movies/admin?tab=review-ingest"); }}>
                  Library Review
                </button>
              )}
              {userData.isAdmin && (
                <button className="user-menu-item" onClick={() => { close(); history.push("/movies/admin?tab=users"); }}>
                  User Administration
                </button>
              )}
            </div>
          </>
        )}
      </div>
    </div>
  );
}

export default UserPanelHeader;
