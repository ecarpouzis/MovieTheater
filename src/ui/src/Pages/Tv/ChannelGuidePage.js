import { useState, useEffect, useCallback, useMemo } from "react";
import { useHistory, useLocation } from "react-router-dom";
import ChannelGrid from "./ChannelGrid";
import GuideDetail from "./GuideDetail";
import BarSearchPortal from "../../catalog/bar/BarSearch";
import { BarToolsSlot } from "../../catalog/bar/SlotPortal";
import { MovieAPI } from "../../MovieAPI";

/**
 * The /channels destination IS the cross-channel grid guide (the EPG). It sits in the content
 * area under the SectionBar (R9 S1c — it used to pin itself fixed over the whole window) with the
 * real-TV-guide move above it: click a show and its detail panel opens at the top (description,
 * ▶ Watch on that channel, Open title, ♥ the channel, up next); the channel button still tunes.
 *
 * Two controls ride the section bar's own slots, and both close gaps rather than adding chrome:
 *
 *   * **Search.** The TV section has declared a `searchPlaceholder` ("Search the guide — a show, a
 *     channel…") since R9 S1c and nothing ever portalled into the slot, so the bar's centre was
 *     empty on every channels page. It filters rows on the channel's name/category AND on the
 *     programme titles in the fetched window (ChannelGrid holds the lineup, so it does the matching).
 *   * **Favourites.** ♥ has been storable per user since the playlists work — the guide's own detail
 *     panel is where you set it — but nothing in the guide ever READ it. Marking a channel a
 *     favourite from this page changed nothing on this page; the only consumer was the "Now Playing"
 *     rail over on the movies browse. The pill narrows the guide to them.
 *
 * Both live in the URL (`?q=`, `?fav=1`), so a filtered guide is a link and Back steps out of it.
 *
 * There is deliberately no "Watch party" control here. A watch party is a PLAYLIST of titles with a
 * shareable token (ChannelController's playlist endpoints — a channel cannot become one), it is
 * created by ticking "watch party" in the playlist picker, and the TV rail already lists My
 * playlists, which creates, rejoins and deletes them. A button here would be a second door onto that
 * same modal.
 */
export default function ChannelGuidePage({ userData, setUserData }) {
  const history = useHistory();
  const location = useLocation();
  const [channels, setChannels] = useState([]);
  const [selected, setSelected] = useState(null); // { channel, program, rowItems }

  const params = new URLSearchParams(location.search);
  const query = params.get("q") || "";
  const favoritesOnly = params.get("fav") === "1";

  const setParam = useCallback((key, value) => {
    const next = new URLSearchParams(location.search);
    if (value) next.set(key, value); else next.delete(key);
    history.push({ pathname: location.pathname, search: next.toString() ? `?${next.toString()}` : "" });
  }, [history, location.pathname, location.search]);

  useEffect(() => {
    let alive = true;
    MovieAPI.getChannelList()
      .then((r) => (r.ok ? r.json() : []))
      .then((c) => { if (alive) setChannels(c); })
      .catch(() => {});
    return () => { alive = false; };
  }, []);

  // A Set of the favourite ids, or null when the pill is off — ChannelGrid treats null as "no
  // favourites filter", so an empty Set still means "show me my favourites, of which there are none".
  const favoriteIds = useMemo(
    () => (favoritesOnly ? new Set((userData?.favoriteChannels || []).map(Number)) : null),
    [favoritesOnly, userData?.favoriteChannels]
  );

  const onPickProgram = useCallback((channel, program, rowItems) => {
    setSelected((cur) => (cur && cur.channel.id === channel.id && cur.program.startUtc === program.startUtc ? null : { channel, program, rowItems }));
  }, []);

  return (
    <div className={`channel-guide-page${selected ? " channel-guide-page--detail" : ""}`}>
      <BarSearchPortal
        placeholder="Search the guide — a show, a channel…"
        value={query}
        onSubmit={(text) => setParam("q", text)}
        ariaLabel="Search the guide"
      />
      {userData && (
        <BarToolsSlot>
          <button
            type="button"
            className={`bx-tool-btn${favoritesOnly ? " on" : ""}`}
            aria-pressed={favoritesOnly}
            onClick={() => setParam("fav", favoritesOnly ? "" : "1")}
          >
            {favoritesOnly ? "★" : "☆"} Favourites
          </button>
        </BarToolsSlot>
      )}
      {selected && (
        <GuideDetail
          channel={selected.channel}
          program={selected.program}
          rowItems={selected.rowItems}
          userData={userData}
          setUserData={setUserData}
          onClose={() => setSelected(null)}
        />
      )}
      <ChannelGrid
        open
        channels={channels}
        currentChannelId={null}
        onPick={(ch) => history.push("/tv/" + ch.id)}
        onPickProgram={onPickProgram}
        selectedKey={selected ? `${selected.channel.id}:${selected.program.startUtc}` : null}
        onClose={() => history.push("/")}
        query={query}
        favoriteIds={favoriteIds}
      />
    </div>
  );
}
