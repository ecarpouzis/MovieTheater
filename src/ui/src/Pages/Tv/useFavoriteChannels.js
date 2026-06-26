import { useCallback, useMemo } from "react";
import { MovieAPI } from "../../MovieAPI";

/**
 * Channel favorites ("My Channels"), persisted in the per-user settings store. Reads the initial set
 * from `userData.favoriteChannels` (supplied by /API/Me), toggles optimistically through `setUserData`,
 * and persists via MovieAPI.setFavoriteChannels. Unknown/stale ids are harmless — the lineup join just
 * drops favorites that no longer match a channel.
 */
export default function useFavoriteChannels(userData, setUserData) {
  const favorites = useMemo(
    () => new Set((userData?.favoriteChannels || []).map(Number)),
    [userData]
  );

  const isFavorite = useCallback((id) => favorites.has(Number(id)), [favorites]);

  const toggle = useCallback(
    (id) => {
      const n = Number(id);
      const next = new Set(favorites);
      if (next.has(n)) next.delete(n);
      else next.add(n);
      const arr = [...next];
      if (setUserData) setUserData((u) => ({ ...(u || {}), favoriteChannels: arr }));
      MovieAPI.setFavoriteChannels(arr).catch(() => {});
    },
    [favorites, setUserData]
  );

  return { favorites, isFavorite, toggle };
}
