import { MovieAPI } from "../../MovieAPI";
import { useCallback, useMemo, useRef, useState } from "react";
import { Dropdown } from "antd";
import { EyeOutlined, EyeFilled, HeartOutlined, HeartFilled, DownOutlined, CheckOutlined } from "@ant-design/icons";
import { sameUser } from "../../hooks/useUserLists";
import "./UserMovieOptions.css";

// Optimistic array edit: add or drop `id` from `list`.
function withId(list, id, on) {
  const cur = list ?? [];
  if (on) return cur.includes(id) ? cur : [...cur, id];
  return cur.filter((x) => x !== id);
}

function failed(response) {
  if (response && response.success) return;
  // eslint-disable-next-line no-alert
  alert(response?.message || "Couldn't save that.");
}

const LIST_OF = { SetWatched: "moviesSeen", SetWantToWatch: "moviesToWatch" };

/**
 * Stable Seen/Want callbacks over SOMEONE's lists (2026-09-05, friends' marks).
 *
 * `lists`/`setLists` are the lists the pills' WORD acts on — the viewer's own (`userData`) or, on
 * `?for=<username>`, a friend's copy (hooks/useUserLists). `forUserId` says whose (null = own).
 * `peers`/`patchPeer` are everybody's lists (`usePeerLists`), the communal copy the card's counts and
 * the people menu read; every mark patches it too, so the pill on the poster answers at once.
 *
 * The latest stores are kept in refs so the returned callbacks never change identity across renders —
 * essential for the memoized cards. `onToggleViewing(id, action, isActive)` lets the page drop a card
 * from the list it is browsing when the OWNER of that list is the one un-marked.
 *
 * `markFor(id, kind, action, userId, on)` is the people menu: mark a specific person. A Want placed on
 * a friend's list IS the suggestion; Seen on a friend's behalf needs a password session (the server
 * refuses otherwise — the menu hides that caret up front).
 */
export function useViewingToggles({ lists, setLists, forUserId = null, onToggleViewing, peers, patchPeer, userData, setUserData }) {
  const ref = useRef({});
  ref.current = { lists, setLists, forUserId, peers, patchPeer, userData, setUserData };

  // /API/Me carries no user id — the viewer finds themself in the communal copy by name.
  const meId = useMemo(() => peers?.find((p) => sameUser(p.username, userData?.username))?.userId ?? null, [peers, userData?.username]);
  const meIdRef = useRef(meId);
  meIdRef.current = meId;

  const notify = useCallback((id, action, on) => {
    if (typeof onToggleViewing === "function") onToggleViewing(id, action, on);
  }, [onToggleViewing]);

  // Patch every store that holds `userId`'s list (null = the viewer): the communal copy, the viewer's
  // own userData, and the scoped friend's copy when it is theirs.
  const applyLocally = useCallback((userId, action, id, on) => {
    const { lists: L, setLists: setL, forUserId: F, patchPeer: pp, userData: ud, setUserData: sud } = ref.current;
    const list = LIST_OF[action];
    const me = meIdRef.current;
    const isMe = userId == null || (me != null && userId === me);
    if (isMe && ud && typeof sud === "function") sud({ ...ud, [list]: withId(ud[list], id, on) });
    if (!isMe && F != null && userId === F && L && typeof setL === "function") setL({ ...L, [list]: withId(L[list], id, on) });
    if (isMe && F == null && L && L !== ud && typeof setL === "function") setL({ ...L, [list]: withId(L[list], id, on) });
    const peerId = isMe ? me : userId;
    if (peerId != null && typeof pp === "function") pp(peerId, list, id, on);
  }, []);

  // The pills' word: the scoped owner (me, or the friend whose lists the browse is on).
  const toggleScoped = useCallback((action, id, kind) => {
    const { lists: L, forUserId: F } = ref.current;
    if (!L) return;
    const on = !(L[LIST_OF[action]] ?? []).includes(id);
    applyLocally(F, action, id, on);
    notify(id, action, on);
    MovieAPI.setViewingState({ kind, id, action, on, forUserId: F }).then(failed);
  }, [applyLocally, notify]);

  const toggleSeen = useCallback((id, kind = "movie") => toggleScoped("SetWatched", id, kind), [toggleScoped]);
  const toggleWant = useCallback((id, kind = "movie") => toggleScoped("SetWantToWatch", id, kind), [toggleScoped]);

  // The people menu: one specific person (null / the viewer's own id = the viewer).
  const markFor = useCallback((id, kind, action, userId, on) => {
    const { forUserId: F } = ref.current;
    const me = meIdRef.current;
    const isMe = userId == null || (me != null && userId === me);
    applyLocally(userId, action, id, on);
    if (isMe ? F == null : userId === F) notify(id, action, on);
    MovieAPI.setViewingState({ kind, id, action, on, forUserId: isMe ? null : userId }).then(failed);
  }, [applyLocally, notify]);

  return { toggleSeen, toggleWant, markFor, meId };
}

/**
 * The rows of the people menu for one title: the viewer first ("You"), then everyone else, with
 * whether each has marked it. Built from the communal copy at open time.
 */
export function peopleRowsFor(peers, meId, id, list) {
  const rows = (peers ?? []).map((p) => ({
    userId: p.userId,
    username: p.username,
    isMe: p.userId === meId,
    on: (p[list] ?? []).includes(id),
  }));
  rows.sort((a, b) => (a.isMe === b.isMe ? 0 : a.isMe ? -1 : 1));
  return rows;
}

function PeopleMenu({ kind, id, titleKind, people, on, children }) {
  const [open, setOpen] = useState(false);
  const action = kind === "seen" ? "SetWatched" : "SetWantToWatch";
  const list = LIST_OF[action];
  const render = () => {
    const rows = people.rows(id, list);
    return (
      <div className={`pmenu pmenu--${kind}`} role="menu" aria-label={kind === "seen" ? "Mark seen for" : "Mark want to watch for"}>
        <div className="pmenu-head">
          {kind === "seen" ? "Seen" : "Want to watch"}
          <i>{kind === "seen" ? "mark for…" : "for someone else = a suggestion"}</i>
        </div>
        {rows.map((r) => (
          <button
            key={r.userId}
            type="button"
            role="menuitemcheckbox"
            aria-checked={r.on}
            className={`pmenu-row${r.on ? " on" : ""}${r.isMe ? " you" : ""}`}
            onClick={() => people.mark(id, titleKind, action, r.isMe ? null : r.userId, !r.on)}
          >
            <span className="pmenu-dot" aria-hidden="true">{(r.username || "?")[0].toUpperCase()}</span>
            {r.isMe ? "You" : r.username}
            <span className="pmenu-check" aria-hidden="true">{r.on ? <CheckOutlined /> : null}</span>
          </button>
        ))}
        {rows.length === 0 && <div className="pmenu-foot">Nobody else here yet.</div>}
      </div>
    );
  };
  return (
    <Dropdown open={open} onOpenChange={setOpen} trigger={["click"]} placement="bottomLeft" popupRender={render}>
      {children}
    </Dropdown>
  );
}

// Presentational Seen / Want pills. Receives resolved booleans + id/kind + stable toggle callbacks (from
// useViewingToggles) rather than the whole lists object, so it can live inside a memoized card without
// pulling userData through and defeating the memo.
//
// SPLIT pills (2026-09-05): the WORD toggles for the scoped owner exactly as before; the CARET opens the
// people menu (`people` = { rows, mark, canMarkSeen } — absent when signed out). The Seen caret is
// absent for a passwordless session, which the server would refuse anyway.
function UserMovieOptions({ id, kind = "movie", isWatched, isWanted, onToggleSeen, onToggleWant, inline = false, people = null }) {
  const seenCaret = !!people && people.canMarkSeen;
  const wantCaret = !!people;
  const stop = (e) => { e.stopPropagation(); };
  return (
    <div className={`viewing-options${inline ? " viewing-options--compact" : ""}`}>
      <div className={`viewing-btn${inline ? " viewing-btn--compact" : ""}${isWatched ? " viewing-btn-seen--watched" : ""}`}>
        <span className="viewing-btn-main" role="button" aria-pressed={isWatched} onClick={() => onToggleSeen(id, kind)}>
          {isWatched ? <EyeFilled className="viewing-btn-icon" /> : <EyeOutlined className="viewing-btn-icon" />}
          <span className={`viewing-btn-label${inline ? " viewing-btn-label--compact" : ""}`}>Seen</span>
        </span>
        {seenCaret && (
          <PeopleMenu kind="seen" id={id} titleKind={kind} people={people} on={isWatched}>
            <span className="viewing-btn-caret" role="button" aria-label="Mark seen for someone" onClick={stop}><DownOutlined /></span>
          </PeopleMenu>
        )}
      </div>
      <div className={`viewing-btn${inline ? " viewing-btn--compact" : ""}${isWanted ? " viewing-btn-want--wanted" : ""}`}>
        <span className="viewing-btn-main" role="button" aria-pressed={isWanted} onClick={() => onToggleWant(id, kind)}>
          {isWanted ? <HeartFilled className="viewing-btn-icon" /> : <HeartOutlined className="viewing-btn-icon" />}
          <span className={`viewing-btn-label${inline ? " viewing-btn-label--compact" : ""}`}>Want</span>
        </span>
        {wantCaret && (
          <PeopleMenu kind="want" id={id} titleKind={kind} people={people} on={isWanted}>
            <span className="viewing-btn-caret" role="button" aria-label="Suggest to someone" onClick={stop}><DownOutlined /></span>
          </PeopleMenu>
        )}
      </div>
    </div>
  );
}

export default UserMovieOptions;
