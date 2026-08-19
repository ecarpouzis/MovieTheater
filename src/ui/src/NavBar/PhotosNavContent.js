import { useHistory, useLocation } from "react-router-dom";
import { NavUserBlock } from "./navShared";
import usePhotosAlbum, { photosNavGroups, photosSection } from "../hooks/usePhotosAlbum";

// The family album's rail (docs/photos-plan.md §4).
//
// /photos used to be served the MOVIE rail — a title/actor/genre search over a film library, on a
// page that has neither. What an album needs instead is an index: the ways into it, and the count
// beside each so a member can see where the collection actually is.
//
// Every row is a real URL (§ the route map), so a view can be bookmarked, shared and refreshed. The
// counts come from the shared album store, which the page is reading at the same moment — one status
// request feeds both.
//
// Log Out, the theme switch and the admin show-hidden checkbox are NOT here: they live in the
// navbar's shared footer, the same as on every other section.
function PhotosNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, setAdminModalOpen }) {
  const history = useHistory();
  const location = useLocation();
  // Enabled unconditionally because this component only renders on /photos — no other section ever
  // issues a photos request.
  const { state, status, unnamed } = usePhotosAlbum({ username: userData?.username });

  const active = photosSection(location.pathname);
  const groups = state === "ready" ? photosNavGroups(status, unnamed.length) : [];

  return (
    <>
      <NavUserBlock userData={userData} onUserLoggedIn={onUserLoggedIn}
        setSettingsModalOpen={setSettingsModalOpen} setAdminModalOpen={setAdminModalOpen} />

      {/* Nothing is listed until the server has said the album is open. The rail is not the gate —
          it just has no index to draw for someone the gate refused. */}
      {groups.length > 0 && (
        <nav className="navbar-photos-nav" aria-label="Album sections">
          {groups.map((group) => (
            <div className="navbar-photos-group" key={group.key}>
              <span className="navbar-photos-heading">{group.label}</span>
              {group.views.map((view) => (
                <button
                  key={view.key}
                  type="button"
                  className={`navbar-photos-link${active === view.key ? " is-active" : ""}`}
                  aria-current={active === view.key ? "page" : undefined}
                  onClick={() => history.push(view.path)}
                >
                  <span className="navbar-photos-link-label">{view.label}</span>
                  {view.count != null && (
                    <span className={`navbar-photos-count${view.waiting ? " is-waiting" : ""}`}>
                      {view.count.toLocaleString()}
                    </span>
                  )}
                </button>
              ))}
            </div>
          ))}
        </nav>
      )}
    </>
  );
}

export default PhotosNavContent;
