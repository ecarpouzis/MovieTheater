// The lobby's filters live entirely in the /arcade query string (?system=…&q=…), so a room — a
// different route — has no way to rebuild them on the way out. Remember the last lobby query and hand
// it back, or every exit button drops the player onto the unfiltered grid of ~13k games.
const KEY = "arcade.lobbySearch";

export function rememberLobbySearch(search) {
  try { sessionStorage.setItem(KEY, search || ""); } catch { /* private mode */ }
}

// "/arcade?system=snes&q=mario" — or a bare "/arcade" for someone who arrived cold on a room link.
export function lobbyPath() {
  let search = "";
  try { search = sessionStorage.getItem(KEY) || ""; } catch { /* private mode */ }
  return `/arcade${search}`;
}
