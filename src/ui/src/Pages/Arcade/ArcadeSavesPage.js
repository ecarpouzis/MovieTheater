import { useHistory } from "react-router-dom";
import { Empty } from "antd";
import SavesVault from "./SavesVault";
import { createRoomAndGo } from "./arcadeRoomCreate";
import "./ArcadePage.css";

/**
 * `/arcade/saves` — a member's own save shelf, as a page.
 *
 * It used to be a Drawer the lobby opened from a bar button, and an "Open the vault" card on the
 * ADMIN shell whose copy claimed it showed "every save state the arcade holds, across every player".
 * It never did: `/API/Arcade/Saves/Mine` is scoped to the signed-in user by the auth cookie, so the
 * admin was reading their OWN saves under an operator heading. Managing your saves is a member
 * feature — it belongs beside Trophies in the section rail, where the movies keep Seen / Want /
 * Rate — and a member surface on this site is a page.
 *
 * Resume starts a room from the save, which is why `createRoomAndGo` had to leave ArcadePage: the
 * lobby is no longer the only surface that starts a room.
 */
export default function ArcadeSavesPage({ userData }) {
  const history = useHistory();
  if (!userData) {
    return (
      <div className="arcade-page">
        <div className="arcade-page__inner" style={{ padding: 48 }}>
          <Empty description="Sign in to see your saves." />
        </div>
      </div>
    );
  }
  return (
    <div className="arcade-page">
      <div className="arcade-page__inner">
        <header className="arcade-header">
          <div className="arcade-header__lede">
            <h1 className="arcade-title">My saves</h1>
            <p className="arcade-subtitle">
              Every save state and battery save you have, across every game. Resume drops you back
              into a room on that save. Deleting is the only destructive thing here — a save is the
              only copy.
            </p>
          </div>
        </header>
        <SavesVault onResume={(gameId, opts) => createRoomAndGo(gameId, opts, history)} />
      </div>
    </div>
  );
}
