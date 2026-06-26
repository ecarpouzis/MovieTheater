import { useState, useEffect, useCallback, useRef } from "react";
import { MovieAPI } from "../../MovieAPI";

/**
 * Fetches the visible channel list once and the cross-channel GuideGrid (now + upcoming per channel)
 * on a slow poll, joining them into a lineup the poster browser and the homepage rail render. `now` is
 * the airing item, `next` the upcoming ones; both carry posterId/kind/posterVersion so the card picks
 * the right poster route. Schedules are stable, so a 60s poll is plenty — the maintainer warms cold
 * channels in the background and they fill in on a later refresh.
 */
export default function useChannelLineup({ poll = true } = {}) {
  const [lineup, setLineup] = useState(null);
  const channelsRef = useRef(null);

  const load = useCallback(async () => {
    try {
      if (!channelsRef.current) {
        const lr = await MovieAPI.getChannelList();
        if (!lr.ok) return;
        channelsRef.current = await lr.json();
      }
      const gr = await MovieAPI.getGuideGrid(6);
      const grid = gr.ok ? await gr.json() : { items: [] };
      const byId = new Map();
      for (const c of grid.items || []) byId.set(c.id, c);

      setLineup(
        channelsRef.current.map((ch) => {
          const g = byId.get(ch.id);
          const items = g?.items || [];
          return {
            ...ch,
            viewers: g?.viewers || 0,
            paused: g?.paused || false,
            now: items[0] || null,
            next: items.slice(1, 4),
          };
        })
      );
    } catch {
      /* transient — a later refresh retries */
    }
  }, []);

  useEffect(() => {
    load();
    if (!poll) return undefined;
    const t = setInterval(load, 60_000);
    return () => clearInterval(t);
  }, [load, poll]);

  return { lineup, refresh: load };
}
