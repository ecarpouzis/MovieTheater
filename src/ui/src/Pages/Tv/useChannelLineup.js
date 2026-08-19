import { useState, useEffect, useCallback, useRef } from "react";
import { MovieAPI } from "../../MovieAPI";
import { preloadImages } from "../../preloadImages";
import usePolling from "../../hooks/usePolling";
import { readStored, writeStored } from "../../utils/storage";
import { nowPlaying } from "./channelNow";

const LINEUP_CACHE_KEY = "tv.lineup.v1";

/**
 * Fetches the visible channel list once and the cross-channel GuideGrid (now + upcoming per channel)
 * on a slow poll, joining them into a lineup the poster browser and the homepage rail render. `now` is
 * the airing item, `next` the upcoming ones; both carry posterId/kind/posterVersion so the card picks
 * the right poster route. Schedules are stable, so a 60s poll is plenty — the maintainer warms cold
 * channels in the background and they fill in on a later refresh.
 */
export default function useChannelLineup({ poll = true } = {}) {
  // Seeded from the last successful build (stale-while-revalidate): the homepage rail renders its
  // last-known lineup instantly instead of a blank band, and the first live poll replaces it —
  // a seconds-stale "Now" beats an empty rail. User-independent, so caching is safe.
  const [lineup, setLineup] = useState(() => {
    const raw = readStored(LINEUP_CACHE_KEY);
    if (raw == null) return null;
    try { return JSON.parse(raw); } catch { return null; }
  });
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

      // "Now" is read off the lineup by the SERVER's clock, through the same helper the EPG uses —
      // the grid reaches 30 minutes into the past, so the first item is routinely a programme that
      // has already finished. Next is whatever follows the airing one, never a slice from the top.
      const serverNow = Date.parse(grid.serverNowUtc);
      const atMs = Number.isFinite(serverNow) ? serverNow : Date.now();

      const built = channelsRef.current.map((ch) => {
        const g = byId.get(ch.id);
        const items = g?.items || [];
        const now = nowPlaying(items, atMs);
        const after = now ? items.indexOf(now) + 1 : items.length;
        return {
          ...ch,
          viewers: g?.viewers || 0,
          paused: g?.paused || false,
          now,
          next: items.slice(after, after + 3),
        };
      });
      setLineup(built);
      writeStored(LINEUP_CACHE_KEY, JSON.stringify(built));

      // Warm now-playing posters ahead of scroll so channel cards never snap in (covers the homepage
      // rail and the /channels browser, which both consume this lineup). Low priority so they don't
      // out-compete the page's own content; on poll, only newly-changed posters fetch.
      preloadImages(
        built
          .map((c) => (c.now?.posterId ? MovieAPI.getPosterThumbnail(c.now.posterId, c.now.posterVersion, c.now.kind) : null))
          .filter(Boolean)
      );
    } catch {
      /* transient — a later refresh retries */
    }
  }, []);

  // Visibility-aware: the homepage rail used to keep polling the guide from a backgrounded tab
  // forever. A non-polling consumer still gets its one load.
  usePolling(load, 60_000, { enabled: poll });
  useEffect(() => {
    if (!poll) load();
  }, [load, poll]);

  return { lineup, refresh: load };
}
