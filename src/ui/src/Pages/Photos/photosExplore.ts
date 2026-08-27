/**
 * The Photos Explore composition (R9 S7) — the album's own habits as a landing.
 *
 * | Rail | Endpoint |
 * |---|---|
 * | spotlight | whichever of the two below has photographs today |
 * | `on-this-day` | `/API/Photos/OnThisDay` — the ONE composition the section could not assemble from what existed |
 * | `recent` | `/API/Photos/Browse?skip=0&top=` — the timeline's own predicate, newest first |
 * | `people` | `/API/Photos/People` — GROUP cards, routed by `f=person:<id>` |
 *
 * There is no Shuffle here: nothing on this page is a roll. "On this day" is a date, "Latest" is a
 * fact and the people are the people.
 */
import { exploreRail, exploreResponse, groupCard } from "../../catalog/explore/composeExplore";
import { facetHref } from "../../catalog/rail/facetUrl";
import type { CardItem, ExploreResponse } from "../../catalog/types";
import { toPhotoCard, type PhotoCardRow } from "../../catalog/sources/photosSource";

export interface PhotoPersonRow {
  id: number;
  name: string;
  tagCount?: number;
  coverUrl?: string | null;
  faceCropUrl?: string | null;
}

export interface PhotosExploreInput {
  onThisDay?: { items?: PhotoCardRow[]; month?: number; day?: number; years?: number[] } | null;
  recent?: PhotoCardRow[];
  people?: PhotoPersonRow[];
}

export const PHOTOS_SPOTLIGHT_SIZE = 4;
const PEOPLE_TAKE = 18;
/** A person tagged in one photograph is not yet a shelf. */
const PERSON_MIN = 2;

export const PHOTOS_MORE = {
  recent: "/photos/browse",
  people: "/photos/people",
};

/** `/photos/browse?f=person:12` — the Photos rail's person facet is numeric. */
export function photoPersonHref(personId: number | string): string {
  return facetHref("/photos/browse", [["person", personId]]);
}

/** "On this day" as a sentence: the date, and how many years the rail reaches back over. */
export function onThisDayTitle(month?: number, day?: number): string {
  if (!month || !day) return "On this day";
  const d = new Date(2000, month - 1, day);
  return `On this day — ${d.toLocaleDateString(undefined, { month: "long", day: "numeric" })}`;
}

export function onThisDaySubtitle(years: number[] | undefined): string | undefined {
  const n = years?.length ?? 0;
  if (n === 0) return undefined;
  if (n === 1) return `From ${years![0]}`;
  return `Across ${n} years, ${Math.min(...years!)}–${Math.max(...years!)}`;
}

/** A person as a GROUP card: their cover (or their face crop) is the face of the shelf. */
export function toPersonCard(p: PhotoPersonRow): CardItem | null {
  if (!p?.id || !(p.name ?? "").trim()) return null;
  return groupCard({
    kind: "person",
    id: p.id,
    key: String(p.id),
    title: p.name,
    count: p.tagCount ?? 0,
    imageUrl: p.coverUrl ?? p.faceCropUrl ?? undefined,
    aspect: 1,
    raw: p,
  });
}

/** The people worth a rail, most-photographed first. */
export function topPeople(people: readonly PhotoPersonRow[] | undefined, take = PEOPLE_TAKE): PhotoPersonRow[] {
  return (people ?? [])
    .filter((p) => (p.tagCount ?? 0) >= PERSON_MIN && (p.name ?? "").trim().length > 0)
    .slice()
    .sort((a, b) => (b.tagCount ?? 0) - (a.tagCount ?? 0))
    .slice(0, take);
}

export function composePhotosExplore(input: PhotosExploreInput): ExploreResponse {
  const today = input.onThisDay?.items ?? [];
  const recent = input.recent ?? [];
  // The hero leads with the anniversary when there is one, and with the newest arrivals otherwise.
  const spotlightRows = (today.length > 0 ? today : recent).slice(0, PHOTOS_SPOTLIGHT_SIZE);

  return exploreResponse(spotlightRows.map(toPhotoCard), [
    exploreRail("on-this-day", onThisDayTitle(input.onThisDay?.month, input.onThisDay?.day), "wall", today.map(toPhotoCard)),
    exploreRail("recent", "Latest in the album", "wall", recent.map(toPhotoCard), PHOTOS_MORE.recent),
    exploreRail("people", "The people in the album", "strip", topPeople(input.people).map(toPersonCard), PHOTOS_MORE.people),
  ]);
}
