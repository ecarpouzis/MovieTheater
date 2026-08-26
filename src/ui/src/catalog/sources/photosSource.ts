/**
 * Photos → `CatalogSource`. Flat bands page `/API/Photos/Browse` (the timeline's predicate under an
 * offset, newest first, the count on the first page only); grouped views ride
 * `/API/Photos/BrowseGroups` (year / month / album / folder); the Directory walks the top-level
 * folders. Cards carry the photograph's TRUE aspect, which is what makes the Wall a contact sheet.
 * The timeline route keeps its justified grid — these views are the section's second surface.
 */
import { hueSvg } from "../cards/CardImage";
import type { CardGroup, CardItem, CardPage, CatalogSource, DirectoryNode, GroupPage, GroupSpec, ListColumn, SortSpec, ViewMode } from "../types";
import { cardKey } from "../types";
import { hueOf } from "./hue";

/** The photo card DTO (`PhotosController.Card`), the fields the adapter reads. */
export interface PhotoCardRow {
  id: number;
  path?: string | null;
  kind?: string | null;
  width?: number | null;
  height?: number | null;
  takenAt?: string | null;
  yearMin?: number | null;
  yearMax?: number | null;
  durationSec?: number | null;
  hidden?: boolean;
  shelf?: string | null;
  thumbState?: string | null;
  videoSynced?: boolean | null;
  gridUrl?: string | null;
}

interface GroupRow {
  key: string;
  label: string;
  totalItems: number;
  renderTotal?: number;
  items?: PhotoCardRow[];
}

export const PHOTO_SORTS: SortSpec[] = [{ value: "newest", label: "Newest first" }];
export const PHOTO_GROUPS: GroupSpec[] = [
  { value: "year", label: "Year" },
  { value: "month", label: "Month" },
  { value: "album", label: "Album" },
  { value: "folder", label: "Folder" },
];
export const PHOTOS_PAGE_SIZE = 60;
const ALL_VIEWS: ViewMode[] = ["grid", "wall", "list", "extended", "shelf", "newspaper", "directory"];
const DIRECTORY_HEADS_PAGE = 50;
const DIRECTORY_MAX_PAGES = 40;
/** A contact sheet tolerates panoramas and tall crops, but not a 20:1 strip. */
const ASPECT_MIN = 0.4;
const ASPECT_MAX = 2.6;

function fmtDate(iso: string | null | undefined): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  return d.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

function fmtDuration(sec: number | null | undefined): string | null {
  if (sec == null || !Number.isFinite(sec)) return null;
  const s = Math.max(0, Math.round(sec));
  const m = Math.floor(s / 60);
  return `${m}:${String(s % 60).padStart(2, "0")}`;
}

function basename(path: string | null | undefined): string | null {
  if (!path) return null;
  const parts = path.split("/").filter(Boolean);
  return parts[parts.length - 1] ?? null;
}

export function photoAspect(row: PhotoCardRow): number {
  const w = row.width ?? 0;
  const h = row.height ?? 0;
  if (w > 0 && h > 0) return Math.min(ASPECT_MAX, Math.max(ASPECT_MIN, w / h));
  return 1;
}

export function toPhotoCard(row: PhotoCardRow): CardItem {
  const id = Number(row.id);
  const date = fmtDate(row.takenAt);
  const title = date ?? basename(row.path) ?? `#${id}`;
  const year = row.takenAt ? Number(String(row.takenAt).slice(0, 4)) : row.yearMin ?? undefined;
  const hue = hueOf(String(id));
  const video = (row.kind ?? "").toLowerCase() === "video";
  const badges: CardItem["badges"] = [];
  if (video) badges.push({ label: fmtDuration(row.durationSec) ? `▶ ${fmtDuration(row.durationSec)}` : "▶", tone: "live", title: row.videoSynced === false ? "Video (not yet synced)" : "Video" });
  if (row.hidden) badges.push({ label: "hidden", tone: "neutral", title: "Hidden" });
  return {
    kind: "photo",
    id,
    key: cardKey("photo", id),
    title,
    label: date ?? undefined,
    year: Number.isFinite(year) ? (year as number) : undefined,
    aspect: photoAspect(row),
    imageUrl: row.gridUrl || hueSvg(hue, 100, 100),
    hue,
    sortKey: row.takenAt ?? undefined,
    badges,
    raw: row,
  };
}

const rawOf = (i: CardItem) => (i.raw ?? {}) as PhotoCardRow;

export const PHOTO_LIST_COLUMNS: ListColumn[] = [
  { key: "date", label: "Taken", width: "150px", mono: true, value: (i) => i.label },
  { key: "file", label: "File", width: "2fr", value: (i) => basename(rawOf(i).path) },
  { key: "kind", label: "Kind", width: "70px", value: (i) => rawOf(i).kind },
  { key: "size", label: "Size", width: "110px", mono: true, value: (i) => (rawOf(i).width && rawOf(i).height ? `${rawOf(i).width}×${rawOf(i).height}` : null) },
  { key: "shelf", label: "Shelf", width: "90px", value: (i) => rawOf(i).shelf },
];

async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const r = await fetch(url, { signal });
  if (!r.ok) throw new Error(`${url} → ${r.status}`);
  return (await r.json()) as T;
}

function qs(params: Record<string, string | number | boolean | null | undefined>): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v != null && v !== "") p.set(k, String(v));
  return p.toString();
}

function toGroup(g: GroupRow): CardGroup {
  const items = (g.items ?? []).map((row) => ({ ...toPhotoCard(row), groupKey: g.key }));
  return { key: g.key, label: g.label, totalItems: g.totalItems, renderTotal: g.renderTotal ?? g.totalItems, items };
}

export interface PhotosSourceOptions {
  includeHidden: boolean;
  /** Names what makes the list a DIFFERENT list (a curation refresh, the hidden toggle). */
  listKey: string;
  /** Open the lightbox (`?photo=<id>`). */
  onOpen: (id: number) => void;
  /** An album header → the album page (by slug). */
  onOpenAlbum?: (slug: string) => void;
  /** A folder header → the folder view (by root-relative path). */
  onOpenFolder?: (path: string) => void;
}

export function createPhotosSource(o: PhotosSourceOptions): CatalogSource {
  const hidden = o.includeHidden ? { includeHidden: true } : {};
  let knownTotal = -1;

  const fetchGroupMore = async (groupKey: string, skip: number, top: number, groupBy: string, _sort: string, signal?: AbortSignal): Promise<CardPage> => {
    const data = await getJson<{ groups?: GroupRow[] }>(`/API/Photos/BrowseGroups?${qs({ groupBy, singleGroupKey: groupKey, perGroupSkip: skip, perGroupTop: top, ...hidden })}`, signal);
    const g = data.groups?.[0];
    return g ? { items: toGroup(g).items, total: g.totalItems } : { items: [], total: 0 };
  };

  return {
    queryKey: `photos:${o.listKey}`,
    title: "Photos",
    itemNoun: "photo",
    groupNoun: "groups",
    supports: ALL_VIEWS,
    groups: PHOTO_GROUPS,
    sorts: PHOTO_SORTS,
    itemsModes: ["items", "groups"],
    itemsLabels: { items: "Every photo", groups: "One per group" },
    listColumns: PHOTO_LIST_COLUMNS,
    defaultGroup: "month",
    pageSize: PHOTOS_PAGE_SIZE,
    defaultAspect: 1,
    directory: {
      roots: async (signal?: AbortSignal): Promise<DirectoryNode[]> => {
        const nodes: DirectoryNode[] = [];
        let total = Infinity;
        for (let page = 0; page < DIRECTORY_MAX_PAGES && nodes.length < total; page += 1) {
          const data = await getJson<{ totalGroups: number; groups: GroupRow[] }>(
            `/API/Photos/BrowseGroups?${qs({ groupBy: "folder", groupsSkip: page * DIRECTORY_HEADS_PAGE, groupsTop: DIRECTORY_HEADS_PAGE, perGroupTop: 1, ...hidden })}`,
            signal,
          );
          total = data.totalGroups;
          if (!data.groups?.length) break;
          for (const g of data.groups) {
            const rep = g.items?.[0] ? toPhotoCard(g.items[0]) : null;
            nodes.push({ id: g.key, label: g.label, count: g.totalItems, imageUrl: rep?.imageUrl, hue: rep?.hue ?? hueOf(g.label) });
          }
        }
        return nodes;
      },
      children: async () => [],
      items: (id, skip, top, signal) => fetchGroupMore(id, skip, top, "folder", "newest", signal),
    },
    fetchFlatBand: async (skip, top, _sort, signal) => {
      const data = await getJson<{ items?: PhotoCardRow[]; total?: number }>(`/API/Photos/Browse?${qs({ skip, top, ...hidden })}`, signal);
      if (typeof data.total === "number" && data.total >= 0) knownTotal = data.total;
      return { items: (data.items ?? []).map(toPhotoCard), total: knownTotal };
    },
    fetchGroupBand: async (groupsSkip, groupsTop, perGroupTop, groupBy, _sort, signal): Promise<GroupPage> => {
      const data = await getJson<{ totalGroups: number; groups: GroupRow[] }>(`/API/Photos/BrowseGroups?${qs({ groupBy, groupsSkip, groupsTop, perGroupTop, ...hidden })}`, signal);
      return { groups: (data.groups ?? []).map(toGroup), totalGroups: data.totalGroups ?? 0 };
    },
    fetchGroupMore,
    onOpen: (item) => o.onOpen(item.id),
    onOpenGroup: (group, groupBy) => {
      if (groupBy === "album") o.onOpenAlbum?.(group.key);
      else if (groupBy === "folder") o.onOpenFolder?.(group.key);
    },
  };
}
