import { useCallback, useMemo } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { MovieAPI } from "../MovieAPI";

/**
 * Whose lists the movies browse is on (2026-09-04, friends' marks).
 *
 * The URL param `for=<username>` scopes the section to a FRIEND's lists: the sider's index rows count
 * theirs, the rail's `my=` reads theirs, and the cards' Seen/Want pills mark on their behalf. Absent (or
 * naming the signed-in user) = "me", and everything reads `userData` exactly as it always did.
 *
 * In "for" mode the lists come from `/API/UserLists` through React Query — per-user data, so NOT
 * `useCachedResource` (that primitive is for user-independent resources). NavBar's dispatcher and
 * Browse both call this; React Query dedupes the fetch, and `setLists` patches the one copy both
 * trees see (the same contract as `useSharedCachedResource`). `ready` gates the dense id-list
 * dispatch: with nothing loaded there is no list to page.
 *
 * The shape returned is the SAME either way — `{ moviesSeen, moviesToWatch, moviesSuggested, miscSeen }`
 * plus a setter — so `useViewingToggles` needs no idea whose lists it edits.
 */

export const EMPTY_LISTS = Object.freeze({ moviesSeen: [], moviesToWatch: [], moviesSuggested: [], miscSeen: [] });

/** The `for=` param of a search string, or null. */
export function forUserOf(search) {
  const v = (new URLSearchParams(search || "").get("for") || "").trim();
  return v || null;
}

/** True when `forUser` is nobody, or the signed-in person themself. */
export function isOwnLists(forUser, userData) {
  if (!forUser) return true;
  const me = (userData?.username || "").toLowerCase();
  return !!me && forUser.toLowerCase() === me;
}

export const userListsKey = (forUser) => ["userLists", (forUser || "").toLowerCase()];

/** Case-insensitive username match — `/API/Me` has no user id, so the viewer finds themself by name. */
export function sameUser(a, b) {
  return !!a && !!b && String(a).toLowerCase() === String(b).toLowerCase();
}

/**
 * Everybody's Seen / Want lists, one communal copy held five minutes and patched as marks are made
 * (`patchPeer`). Feeds the card's "3 have seen it" pill, the pills' people menu and the "Lists for"
 * switcher. Each row: { userId, username, hasPassword, moviesSeen, moviesToWatch }.
 */
export const PEER_LISTS_KEY = ["peerLists"];

export function usePeerLists(enabled = true) {
  const queryClient = useQueryClient();
  const query = useQuery({
    queryKey: PEER_LISTS_KEY,
    queryFn: ({ signal }) => MovieAPI.getPeerLists(signal),
    enabled,
    staleTime: 5 * 60 * 1000,
    retry: false,
  });
  const peers = useMemo(() => (Array.isArray(query.data) ? query.data : []), [query.data]);
  // Patch one person's list in place: `list` = "moviesSeen" | "moviesToWatch".
  const patchPeer = useCallback((userId, list, id, on) => {
    queryClient.setQueryData(PEER_LISTS_KEY, (prev) => {
      if (!Array.isArray(prev)) return prev;
      return prev.map((p) => {
        if (p.userId !== userId) return p;
        const cur = p[list] ?? [];
        const next = on ? (cur.includes(id) ? cur : [...cur, id]) : cur.filter((x) => x !== id);
        return next === cur ? p : { ...p, [list]: next };
      });
    });
  }, [queryClient]);
  return { peers, patchPeer, ready: query.isSuccess, error: query.error ?? null };
}

export default function useUserLists(forUser, userData, setUserData) {
  const me = isOwnLists(forUser, userData);
  const queryClient = useQueryClient();
  const key = useMemo(() => userListsKey(forUser), [forUser]);
  const keyStr = key[1];

  const query = useQuery({
    queryKey: key,
    queryFn: ({ signal }) => MovieAPI.getUserLists(forUser, signal),
    enabled: !me && !!userData && !!forUser,
    staleTime: 5 * 60 * 1000,
    retry: false,
  });

  const setLists = useCallback((next) => {
    queryClient.setQueryData(userListsKey(keyStr), (prev) => (typeof next === "function" ? next(prev ?? EMPTY_LISTS) : next));
  }, [queryClient, keyStr]);

  if (me) {
    return {
      me: true,
      forUser: null,
      username: userData?.username ?? null,
      userId: null,
      lists: userData,
      setLists: setUserData,
      ready: !!userData,
      error: null,
    };
  }
  return {
    me: false,
    forUser,
    username: query.data?.username ?? forUser,
    userId: query.data?.userId ?? null,
    lists: query.data ?? null,
    setLists,
    ready: query.isSuccess,
    error: query.error ?? null,
  };
}
