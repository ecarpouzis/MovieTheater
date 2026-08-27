/**
 * `/photos/explore` — the family album's landing (R9 S7). Mounted INSIDE `PhotosPage`'s own Switch,
 * so it inherits the section's gate plates and its `?photo=` lightbox for free: a card here opens
 * exactly the lightbox a timeline card opens, on the same URL.
 *
 * Two small reads, both capped, and the people list is not even one — `PhotosPage` has already
 * fetched it for the sider rail, so it is handed down. "On this day" waits for nothing (it IS the
 * page's reason to exist today); the recent reel waits for `useExploreDepth`.
 */
import { useQuery } from "@tanstack/react-query";
import { useCallback, useMemo } from "react";
import ExploreTab from "../../catalog/explore/ExploreTab";
import { FACET_GROUP_KINDS } from "../../catalog/explore/mapExplore";
import { useExploreDepth } from "../../catalog/explore/useNearViewport";
import type { CardGroup, CardItem } from "../../catalog/types";
import type { PhotoCardRow } from "../../catalog/sources/photosSource";
import {
  composePhotosExplore,
  onThisDaySubtitle,
  photoPersonHref,
  type PhotoPersonRow,
} from "./photosExplore";

const RAIL_SUBTITLES: Record<string, string> = {
  recent: "The newest photographs in the album",
  people: "Everyone the album has learned to name",
};

async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const r = await fetch(url, { signal });
  if (!r.ok) throw new Error(`${url} → ${r.status}`);
  return (await r.json()) as T;
}

export interface PhotoExploreProps {
  /** The people list `PhotosPage` already holds (one status request feeds the page and the rail). */
  people?: PhotoPersonRow[];
  includeHidden?: boolean;
  /** Open the lightbox — the section's `?photo=` param. */
  onOpen: (id: number) => void;
  /** Navigate (the people rail's "More →" and a person card). */
  onNavigate: (href: string) => void;
}

interface OnThisDayDto { items?: PhotoCardRow[]; month?: number; day?: number; years?: number[] }

export default function PhotoExplore({ people, includeHidden = false, onOpen, onNavigate }: PhotoExploreProps) {
  const deep = useExploreDepth();
  const hidden = includeHidden ? "&includeHidden=true" : "";

  const onThisDay = useQuery({
    queryKey: ["photos", "explore", "on-this-day", new Date().toDateString(), includeHidden],
    queryFn: ({ signal }) => getJson<OnThisDayDto>(`/API/Photos/OnThisDay?take=24${hidden}`, signal),
    // The date is the key, so this is answered once a day and from cache for the rest of it.
    staleTime: 60 * 60 * 1000,
  });
  const recent = useQuery({
    queryKey: ["photos", "explore", "recent", includeHidden],
    queryFn: ({ signal }) => getJson<{ items?: PhotoCardRow[] }>(`/API/Photos/Browse?skip=0&top=24${hidden}`, signal),
    enabled: deep,
    staleTime: 10 * 60 * 1000,
  });

  const data = useMemo(() => composePhotosExplore({
    onThisDay: onThisDay.data,
    recent: recent.data?.items,
    people,
  }), [onThisDay.data, recent.data, people]);

  const open = useCallback((item: CardItem) => onOpen(item.id), [onOpen]);
  const openGroup = useCallback((group: CardGroup, groupBy: string) => {
    if (groupBy !== "person") return;
    onNavigate(photoPersonHref(group.key));
  }, [onNavigate]);

  const ready = !onThisDay.isPending || !!onThisDay.data;
  return (
    <div className="photos-explore">
      <ExploreTab
        data={ready ? data : null}
        loading={onThisDay.isFetching || recent.isFetching}
        error={onThisDay.error && !onThisDay.data ? onThisDay.error : undefined}
        onOpen={open}
        onOpenGroup={openGroup}
        groupKinds={FACET_GROUP_KINDS}
        moreHref={(href) => href || null}
        railSubtitle={(rail) => (rail.key === "on-this-day" ? onThisDaySubtitle(onThisDay.data?.years) : RAIL_SUBTITLES[rail.key])}
        heroEyebrow="The family album"
        emptyMessage="Nothing in the album yet."
      />
    </div>
  );
}
