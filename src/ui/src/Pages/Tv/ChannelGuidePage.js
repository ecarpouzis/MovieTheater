import { useState, useEffect, useCallback, useMemo, useRef } from "react";
import { useHistory, useLocation } from "react-router-dom";
import ChannelGrid from "./ChannelGrid";
import GuideDetail from "./GuideDetail";
import BarSearchPortal from "../../catalog/bar/BarSearch";
import { BarToolsSlot } from "../../catalog/bar/SlotPortal";
import { MovieAPI } from "../../MovieAPI";
import { nowPlaying } from "./channelNow";
import { rowMatches } from "./guideModel";

/**
 * The /channels destination IS the cross-channel grid guide (the EPG). It sits in the content
 * area under the SectionBar (R9 S1c — it used to pin itself fixed over the whole window) with the
 * real-TV-guide move above it: a detail panel at the top for the selected programme (poster, the meta
 * line, description, ▶ Tune in / ↺ Start over, Open title, ♥ the channel, up next — and, on desktop,
 * the live preview). Guide v2 (2026-09-04) made that panel behave like a cable box:
 *
 *   * something is ALWAYS selected: the first visible row's current programme is picked as soon as
 *     the lineup lands, so the top of the page is never blank. That auto-selection shows the POSTER —
 *     the video preview only starts once the viewer has clicked a programme (`previewArmed`), so
 *     merely opening the guide never spawns an encode;
 *   * the selection FOLLOWS the channel: when the selected programme ends, or a refresh shows its
 *     slot moved (a skip/restart shifted startUtc), the channel's current programme takes its place.
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
  const [selected, setSelected] = useState(null); // { channel, program, rowItems, row }
  const [previewArmed, setPreviewArmed] = useState(false);
  const [lineup, setLineup] = useState(null); // { byId, skewMs } from the grid's last load
  const [nowMs, setNowMs] = useState(() => Date.now());
  const dismissedRef = useRef(false); // × on the panel: stop auto-selecting until the next click

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

  // The page's clock rides the grid's server skew so "· now", "N min left" and the follow-the-channel
  // rule agree with the now line.
  useEffect(() => {
    const skew = lineup?.skewMs || 0;
    setNowMs(Date.now() + skew);
    const t = setInterval(() => setNowMs(Date.now() + skew), 15_000);
    return () => clearInterval(t);
  }, [lineup?.skewMs]);

  // A Set of the favourite ids, or null when the pill is off — ChannelGrid treats null as "no
  // favourites filter", so an empty Set still means "show me my favourites, of which there are none".
  const favoriteIds = useMemo(
    () => (favoritesOnly ? new Set((userData?.favoriteChannels || []).map(Number)) : null),
    [favoritesOnly, userData?.favoriteChannels]
  );

  const onPickProgram = useCallback((channel, program, rowItems) => {
    dismissedRef.current = false;
    setPreviewArmed(true);
    setSelected((cur) => {
      if (cur && cur.channel.id === channel.id && cur.program.startUtc === program.startUtc) return null;
      return { channel, program, rowItems, row: lineup?.byId.get(channel.id) || null };
    });
  }, [lineup]);

  const onLineup = useCallback((info) => setLineup(info), []);

  // Auto-select + follow. Runs when the lineup lands/refreshes, the filter changes, or the clock ticks
  // past the selected programme's end.
  useEffect(() => {
    if (!lineup) return;
    const needle = query.trim().toLowerCase();
    const pickCurrent = (channel) => {
      const row = lineup.byId.get(channel.id);
      const program = nowPlaying(row?.items, nowMs);
      return program ? { channel, program, rowItems: row.items, row } : null;
    };
    setSelected((cur) => {
      if (cur) {
        const row = lineup.byId.get(cur.channel.id);
        const stillThere = row?.items.find((p) => p.startUtc === cur.program.startUtc);
        const ended = Date.parse(cur.program.endUtc) <= nowMs;
        // Refresh the row facts (viewers/paused) and the programme's own fields in place.
        if (stillThere && !ended) return { ...cur, program: stillThere, rowItems: row.items, row };
        // The slot moved (skip/restart) or the programme ended: follow the channel.
        return pickCurrent(cur.channel) || cur;
      }
      if (dismissedRef.current) return cur;
      const first = channels.find((ch) => lineup.byId.has(ch.id) && rowMatches(ch, lineup.byId.get(ch.id)?.items, needle, favoriteIds));
      return first ? pickCurrent(first) : null;
    });
  }, [lineup, channels, query, favoriteIds, nowMs]);

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
          row={selected.row}
          userData={userData}
          setUserData={setUserData}
          previewArmed={previewArmed}
          onArmPreview={() => setPreviewArmed(true)}
          nowMs={nowMs}
          onClose={() => { dismissedRef.current = true; setSelected(null); }}
        />
      )}
      <ChannelGrid
        open
        channels={channels}
        currentChannelId={null}
        onPick={(ch) => history.push("/tv/" + ch.id)}
        onPickProgram={onPickProgram}
        onLineup={onLineup}
        selectedKey={selected ? `${selected.channel.id}:${selected.program.startUtc}` : null}
        onClose={() => history.push("/")}
        query={query}
        favoriteIds={favoriteIds}
      />
    </div>
  );
}
