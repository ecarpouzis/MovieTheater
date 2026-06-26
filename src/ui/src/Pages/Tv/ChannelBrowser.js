import { useEffect, useMemo } from "react";
import { useHistory } from "react-router-dom";
import useChannelLineup from "./useChannelLineup";
import useFavoriteChannels from "./useFavoriteChannels";
import ChannelCard from "./ChannelCard";
import "./ChannelBrowser.css";

/**
 * The poster-rich channel browser (Channels 2.0 centerpiece): channels grouped by category as
 * horizontal rails, with a pinned "My Channels" rail of favorites first. One component, two mounts —
 * an overlay inside the TV room (pass `onClose`; the channel button's friendly "what's on" chooser
 * next to the time-grid EPG) and a standalone `/channels` page (no `onClose`). `onPick(channel)` either
 * tunes (overlay) or navigates (page).
 */
export default function ChannelBrowser({ open = true, onPick, onClose, onGuide, userData, setUserData }) {
  const { lineup } = useChannelLineup({ poll: open });
  const { isFavorite, toggle } = useFavoriteChannels(userData, setUserData);
  const history = useHistory();
  // Overlay mode tunes via onPick; page mode (no onPick) navigates to the channel.
  const pick = onPick || ((c) => history.push("/tv/" + c.id));

  // Esc closes the overlay; trap it so it doesn't reach the page's channel hotkeys.
  useEffect(() => {
    if (!onClose || !open) return undefined;
    const onKey = (e) => { if (e.key === "Escape") { e.stopPropagation(); onClose(); } };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
  }, [onClose, open]);

  // Catalog order is contiguous by category, so a single pass groups them; favorites are pinned first.
  const groups = useMemo(() => {
    if (!lineup) return [];
    const out = [];
    const favs = lineup.filter((c) => isFavorite(c.id));
    if (favs.length) out.push({ name: "My Channels", channels: favs });
    let last = null, cur = null;
    for (const c of lineup) {
      const cat = c.category || "Channels";
      if (cat !== last) { cur = { name: cat, channels: [] }; out.push(cur); last = cat; }
      cur.channels.push(c);
    }
    return out;
  }, [lineup, isFavorite]);

  if (!open) return null;

  return (
    <div className={`chbrowse${onClose ? " chbrowse--overlay" : ""}`}>
      <div className="chbrowse-head">
        <span className="chbrowse-title">Channels</span>
        {onGuide && <button className="chbrowse-guide" onClick={onGuide}>📺 Guide</button>}
        {onClose && <button className="chbrowse-close" onClick={onClose} aria-label="Close">×</button>}
      </div>
      <div className="chbrowse-body">
        {!lineup && <div className="chbrowse-loading">Loading channels…</div>}
        {groups.map((g) => (
          <section className="chbrowse-group" key={g.name}>
            <h3 className="chbrowse-group-title">{g.name}</h3>
            <div className="chbrowse-rail">
              {g.channels.map((c) => (
                <ChannelCard
                  key={`${g.name}-${c.id}`}
                  channel={c}
                  onPick={pick}
                  isFavorite={isFavorite(c.id)}
                  onToggleFavorite={toggle}
                />
              ))}
            </div>
          </section>
        ))}
        {lineup && groups.length === 0 && <div className="chbrowse-loading">No channels are broadcasting.</div>}
      </div>
    </div>
  );
}
