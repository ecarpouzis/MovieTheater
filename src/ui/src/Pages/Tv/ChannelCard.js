import { useState } from "react";
import { MovieAPI } from "../../MovieAPI";
import "./ChannelCard.css";

// Hash a category name to a stable hue so a cold channel (no poster yet) reads as a colored band per
// family rather than an empty box.
function hueFor(s) {
  let h = 0;
  for (let i = 0; i < (s || "").length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0;
  return h % 360;
}

/**
 * A poster-forward channel card, reused by the Channel Browser and the homepage "Now on TV" rail.
 * Shows the now-playing poster (the right route per kind), name, Now/Next titles, a viewer pill, and a
 * favorite star (which doesn't tune). Falls back to a category-tinted gradient with the channel initial.
 */
export default function ChannelCard({ channel, onPick, isFavorite, onToggleFavorite }) {
  const [imgOk, setImgOk] = useState(true);
  const now = channel.now;
  const poster = now && now.posterId
    ? MovieAPI.getPosterThumbnail(now.posterId, now.posterVersion, now.kind)
    : null;

  return (
    <button className="chcard" onClick={() => onPick(channel)} title={`Watch ${channel.name}`}>
      <div className="chcard-art" style={{ "--chcard-hue": hueFor(channel.category || channel.name) }}>
        {poster && imgOk ? (
          <img
            className="chcard-poster"
            src={poster}
            alt=""
            loading="lazy"
            decoding="async"
            onError={() => setImgOk(false)}
          />
        ) : (
          <span className="chcard-initial">{(channel.name || "?").charAt(0)}</span>
        )}
        {channel.viewers > 0 && <span className="chcard-viewers">👁 {channel.viewers}</span>}
        {channel.paused && <span className="chcard-paused" title="Paused">❚❚</span>}
        {onToggleFavorite && (
          <span
            className={`chcard-fav${isFavorite ? " chcard-fav--on" : ""}`}
            role="button"
            tabIndex={0}
            title={isFavorite ? "Remove from My Channels" : "Add to My Channels"}
            onClick={(e) => { e.stopPropagation(); onToggleFavorite(channel.id); }}
            onKeyDown={(e) => {
              if (e.key === "Enter" || e.key === " ") { e.stopPropagation(); e.preventDefault(); onToggleFavorite(channel.id); }
            }}
          >
            {isFavorite ? "★" : "☆"}
          </span>
        )}
      </div>
      <div className="chcard-meta">
        <div className="chcard-name">{channel.name}</div>
        {now && <div className="chcard-line"><span className="chcard-tag">Now</span>{now.title}</div>}
        {channel.next?.[0] && (
          <div className="chcard-line chcard-line--next"><span className="chcard-tag">Next</span>{channel.next[0].title}</div>
        )}
      </div>
    </button>
  );
}
