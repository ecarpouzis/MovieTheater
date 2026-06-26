import { useEffect, useMemo, useRef } from "react";
import { useHistory } from "react-router-dom";
import useChannelLineup from "../Tv/useChannelLineup";
import useFavoriteChannels from "../Tv/useFavoriteChannels";
import ChannelCard from "../Tv/ChannelCard";
import "./NowOnTvRail.css";

/**
 * A "Now Playing" poster rail at the top of Browse — surfaces channels from the homepage so the lineup
 * gets discovered, not just hidden in /tv. Favorites first, then a curated slice. Only shown to
 * streaming-enabled users; a card tunes straight to the channel.
 */
export default function NowOnTvRail({ userData, setUserData }) {
  const { lineup } = useChannelLineup();
  const { isFavorite, toggle } = useFavoriteChannels(userData, setUserData);
  const history = useHistory();
  const railRef = useRef(null);

  // Translate vertical wheel/trackpad scrolling into horizontal travel so the rail flows under the
  // cursor without having to grab the scrollbar or click "All channels". Native non-passive listener
  // so preventDefault actually sticks (React's synthetic onWheel is passive).
  useEffect(() => {
    const el = railRef.current;
    if (!el) return;
    const onWheel = (e) => {
      if (Math.abs(e.deltaY) <= Math.abs(e.deltaX)) return; // let real horizontal gestures pass through
      el.scrollLeft += e.deltaY;
      e.preventDefault();
    };
    el.addEventListener("wheel", onWheel, { passive: false });
    return () => el.removeEventListener("wheel", onWheel);
  }, [lineup]);

  const channels = useMemo(() => {
    if (!lineup) return [];
    const favs = lineup.filter((c) => isFavorite(c.id));
    const favIds = new Set(favs.map((c) => c.id));
    // Favorites first, then the rest busiest-first — the whole lineup, uncapped, so the rail scrolls
    // through every channel that's on right now (the /channels guide shows the same set by category).
    const rest = lineup
      .filter((c) => !favIds.has(c.id))
      .sort((a, b) => (b.viewers || 0) - (a.viewers || 0));
    return [...favs, ...rest];
  }, [lineup, isFavorite]);

  if (!userData?.hasPassword) return null; // streaming needs a password-verified session
  if (!lineup || channels.length === 0) return null;

  return (
    <div className="nowtv">
      <div className="nowtv-head">
        <span className="nowtv-live" aria-hidden="true" />
        <span className="nowtv-title">Now Playing</span>
        <button className="nowtv-all" onClick={() => history.push("/channels")}>All channels →</button>
      </div>
      <div className="nowtv-rail" ref={railRef}>
        {channels.map((c) => (
          <ChannelCard
            key={c.id}
            channel={c}
            onPick={(ch) => history.push("/tv/" + ch.id)}
            isFavorite={isFavorite(c.id)}
            onToggleFavorite={toggle}
          />
        ))}
      </div>
    </div>
  );
}
