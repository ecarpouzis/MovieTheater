import { useMemo } from "react";
import { useHistory } from "react-router-dom";
import useChannelLineup from "../Tv/useChannelLineup";
import useFavoriteChannels from "../Tv/useFavoriteChannels";
import ChannelCard from "../Tv/ChannelCard";
import "./NowOnTvRail.css";

/**
 * A "Now on TV" poster rail at the top of Browse — surfaces channels from the homepage so the lineup
 * gets discovered, not just hidden in /tv. Favorites first, then a curated slice. Only shown to
 * streaming-enabled users; a card tunes straight to the channel.
 */
export default function NowOnTvRail({ userData, setUserData }) {
  const { lineup } = useChannelLineup();
  const { isFavorite, toggle } = useFavoriteChannels(userData, setUserData);
  const history = useHistory();

  const channels = useMemo(() => {
    if (!lineup) return [];
    const favs = lineup.filter((c) => isFavorite(c.id));
    const favIds = new Set(favs.map((c) => c.id));
    // Favorites first, then the busiest (most viewers), capped — a "what's on right now" strip.
    const rest = lineup
      .filter((c) => !favIds.has(c.id))
      .sort((a, b) => (b.viewers || 0) - (a.viewers || 0))
      .slice(0, Math.max(0, 24 - favs.length));
    return [...favs, ...rest];
  }, [lineup, isFavorite]);

  if (!userData?.hasPassword) return null; // streaming needs a password-verified session
  if (!lineup || channels.length === 0) return null;

  return (
    <div className="nowtv">
      <div className="nowtv-head">
        <span className="nowtv-title">Now on TV</span>
        <button className="nowtv-all" onClick={() => history.push("/channels")}>All channels →</button>
      </div>
      <div className="nowtv-rail">
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
