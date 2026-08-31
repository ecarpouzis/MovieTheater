/**
 * `/music/explore` — the Music section's landing (R9 S7).
 *
 * It costs the section almost nothing: the shelf is the SAME shared React-Query resource the browse
 * and the sider rail read (`useMusicShelf`, stale-while-revalidate off `music.catalog.v1:<shelf>`),
 * so arriving here from the browse draws from memory. The one extra read is the caller's playlists —
 * and that waits for `useExploreDepth`, so a landing nobody scrolls never makes it.
 *
 * An album card opens the section's album sheet at `/music?album=<id>`: that sheet is bound to the
 * persistent play bar and to MusicPage's queue (the one modal on the site deliberately off the shared
 * shell — see `MusicPage.css`), so Explore links to it rather than mounting a second copy.
 * An ARTIST card is a group card and lands on `/music?f=artist:<id>`; a GENRE card is one too and
 * lands on `/music?f=genre:<name>`.
 */
import { useQuery } from "@tanstack/react-query";
import { useCallback, useMemo } from "react";
import { useHistory, useLocation } from "react-router-dom";
import ExploreTab from "../../catalog/explore/ExploreTab";
import { FACET_GROUP_KINDS } from "../../catalog/explore/mapExplore";
import { useExploreDepth } from "../../catalog/explore/useNearViewport";
import type { CardGroup, CardItem } from "../../catalog/types";
import { MovieAPI } from "../../MovieAPI";
import { MUSIC_UNSEEDED_RAILS, composeMusicExplore, musicArtistHref, musicGenreHref, type MusicPlaylistRow } from "./musicExplore";
import { useMusicShelf } from "./useMusicShelf";
import "./MusicPage.css";

const RAIL_SUBTITLES: Record<string, string> = {
  favourites: "Everything you have hearted, newest first",
  "just-added": "The most recent arrivals in the library",
  artists: "The names with the most on the shelf",
  genres: "What the collection is made of",
  // Named for what each number IS. Popularity is an audience count, not a verdict, and the two
  // rails sat under one "Best on the shelf" heading until 2026-08-31.
  popular: "Most widely heard, well beyond this house",
  best: "Best regarded, where anyone has said so",
  random: "A shuffled handful — roll again for another",
};

/**
 * The shared set plus `genre`: this section's genres are a facet value, so a genre card stands for a
 * whole browse the way an artist card does. It is passed here rather than added to the shared set
 * because "genre" is not a group everywhere — Movies has genres too, and a genre card on THAT
 * landing would need to open a different view.
 */
const MUSIC_GROUP_KINDS: ReadonlySet<string> = new Set([...FACET_GROUP_KINDS, "genre"]);

export function readSeed(search: string): number {
  const raw = new URLSearchParams(search).get("seed");
  if (raw && /^[0-9]{1,9}$/.test(raw)) {
    const n = Number(raw);
    if (Number.isSafeInteger(n) && n > 0) return n;
  }
  return 1;
}

export default function MusicExplorePage({ userData }: { userData?: { hasPassword?: boolean } | null }) {
  const history = useHistory();
  const location = useLocation();
  const gated = !userData?.hasPassword;
  const seed = readSeed(location.search);
  const deep = useExploreDepth();

  // The library shelf — the SAME shared resource the browse and the sider rail read.
  const shelf = useMusicShelf("", !gated);
  const playlists = useQuery({
    queryKey: ["music", "explore", "playlists"],
    queryFn: async () => {
      const r = await MovieAPI.getMyMusicPlaylists();
      if (!r.ok) throw new Error(`playlists → ${r.status}`);
      return (await r.json()) as MusicPlaylistRow[];
    },
    enabled: !gated && deep,
    staleTime: 5 * 60 * 1000,
  });

  const data = useMemo(() => composeMusicExplore({
    albums: shelf.albums,
    artists: shelf.artists,
    playlists: playlists.data,
    seed,
  }), [shelf.albums, shelf.artists, playlists.data, seed]);

  const onSeed = useCallback((next: number) => {
    const p = new URLSearchParams(location.search);
    p.set("seed", String(next));
    history.push({ pathname: location.pathname, search: `?${p.toString()}` });
  }, [history, location.pathname, location.search]);

  const onOpen = useCallback((item: CardItem) => {
    history.push(`/music?album=${item.id}`);
  }, [history]);

  const onOpenGroup = useCallback((group: CardGroup, groupBy: string) => {
    if (groupBy === "artist") history.push(musicArtistHref(group.key));
    else if (groupBy === "genre") history.push(musicGenreHref(group.key));
  }, [history]);

  if (gated) {
    return (
      <div className="music-page">
        <div className="music-gate-note">
          Music streaming needs a password-protected account — ask the site admin.
        </div>
      </div>
    );
  }

  return (
    <div className="music-page music-explore">
      <ExploreTab
        data={shelf.loading && shelf.albums.length === 0 ? null : data}
        loading={shelf.loading}
        error={shelf.error && shelf.albums.length === 0 ? new Error("music") : undefined}
        onSeed={onSeed}
        onOpen={onOpen}
        onOpenGroup={onOpenGroup}
        groupKinds={MUSIC_GROUP_KINDS}
        moreHref={(href) => href || null}
        unseededRails={MUSIC_UNSEEDED_RAILS}
        railSubtitle={(rail) => RAIL_SUBTITLES[rail.key]}
        heroEyebrow="From the library"
        emptyMessage="Nothing on the shelf yet."
      />
    </div>
  );
}
