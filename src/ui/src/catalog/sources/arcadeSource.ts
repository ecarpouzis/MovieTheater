/**
 * Arcade → `CatalogSource`. The lobby's filters (all URL params) ARE the scope: flat bands page
 * `/API/Arcade/Games` exactly as the lobby's own grid does (absolute `skip`, the lobby's card DTO),
 * grouped views ride `/API/Arcade/GameGroups` + `GameGroupLetters` under the same filters, and the
 * Directory walks systems → games. Opening a card hands the lobby's card object back to the page:
 * its modal is seeded from the card synchronously (a cold `?game=` fetches by version id instead).
 */
import { MovieAPI } from "../../MovieAPI";
import { hueSvg } from "../cards/CardImage";
import type { CardGroup, CardItem, CardPage, CatalogSource, DirectoryNode, GroupPage, GroupSpec, LetterBucket, ListColumn, SortSpec, ViewMode } from "../types";
import { hueOf } from "./hue";

/** The lobby's filter object (`ArcadePage` builds it from the URL). */
export interface ArcadeFilters {
  system?: string;
  hideRegions?: string;
  maxPlayers?: string | number;
  variant?: string;
  genre?: string;
  /** "" = the lobby's default A–Z. */
  sort?: string;
  search?: string;
  ra?: string;
}

/** The lobby's card DTO (`/API/Arcade/Games` `games[]`), the fields the adapter reads. */
export interface ArcadeGameRow {
  key: string;
  title: string;
  system?: string;
  artId?: number;
  artV?: string | number | null;
  hasBoxArt?: boolean;
  year?: number | null;
  maxPlayers?: number | null;
  versionCount?: number;
  rating?: number | null;
  ratingSource?: string | null;
  genres?: string | null;
  developer?: string | null;
  publisher?: string | null;
  summary?: string | null;
  lane?: string | null;
  raAchievements?: boolean;
  raHighScores?: boolean;
  raSpeedruns?: boolean;
  versions?: { id: number }[];
}

interface GroupRow {
  key: string;
  label: string;
  totalItems: number;
  renderTotal?: number;
  items?: ArcadeGameRow[];
}

/** The lobby's `?sort=` vocabulary. Its default is the empty string; the switcher needs a name for it. */
export const ARCADE_DEFAULT_SORT = "alpha";
export const ARCADE_SORTS: SortSpec[] = [
  { value: ARCADE_DEFAULT_SORT, label: "A–Z", alpha: true },
  { value: "rating", label: "Rating" },
  { value: "year", label: "Newest" },
  { value: "system", label: "System" },
  { value: "players", label: "Most players" },
];

export const ARCADE_GROUPS: GroupSpec[] = [
  { value: "system", label: "System" },
  { value: "genre", label: "Genre" },
  { value: "decade", label: "Decade" },
];

/** Which lobby filter a group header applies (`?system=`, `?genre=`); decades have none. */
const GROUP_FILTER_PARAM: Record<string, string> = { system: "system", genre: "genre" };

/** Box art runs from tall NES boxes to wide Genesis ones; the Grid's uniform tile splits the difference. */
export const ARCADE_ASPECT = 0.75;
export const ARCADE_PAGE_SIZE = 60;
const ALL_VIEWS: ViewMode[] = ["grid", "wall", "list", "extended", "shelf", "newspaper", "directory"];
const DIRECTORY_HEADS_PAGE = 50;
const DIRECTORY_MAX_PAGES = 10;

/** The lobby's sort param as the server wants it: the switcher's `alpha` is the server's "" (default). */
export function serverSort(sort: string | null | undefined): string {
  return !sort || sort === ARCADE_DEFAULT_SORT ? "" : sort;
}

/** The lobby's `arcadeQuery`: drops empty / "all" values. */
export function arcadeQuery(params: Record<string, string | number | null | undefined>): string {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v != null && v !== "" && v !== "all") q.set(k, String(v));
  const qs = q.toString();
  return qs ? `?${qs}` : "";
}

/** A stable positive int from the card key, for the rare card without a version id. */
function idFromKey(key: string): number {
  let h = 7;
  for (let i = 0; i < key.length; i += 1) h = (h * 31 + key.charCodeAt(i)) >>> 0;
  return 1_000_000_000 + (h % 1_000_000_000);
}

export function coverUrl(row: ArcadeGameRow): string | null {
  if (!row.hasBoxArt || !row.artId) return null;
  return row.artV ? `/ArcadeImage/${row.artId}?v=${row.artV}` : `/ArcadeImage/${row.artId}`;
}

export function toArcadeCard(row: ArcadeGameRow): CardItem {
  const title = row.title ?? row.key;
  const id = row.versions?.[0]?.id ?? idFromKey(row.key);
  const hue = hueOf(title);
  const badges: CardItem["badges"] = [];
  if (row.rating != null) badges.push({ label: `★ ${row.rating}`, tone: "rating", title: row.ratingSource ? `Rating (${row.ratingSource})` : "Rating" });
  if (row.maxPlayers != null && row.maxPlayers > 1) badges.push({ label: `👥 ${row.maxPlayers}`, tone: "neutral", title: "Players" });
  if (row.raAchievements) badges.push({ label: "🏆", tone: "system", title: "RetroAchievements" });
  if ((row.versionCount ?? 0) > 1) badges.push({ label: `×${row.versionCount}`, tone: "neutral", title: "Versions" });
  return {
    kind: "game",
    id,
    key: `game:${row.key}`,
    title,
    subtitle: row.system ?? undefined,
    label: row.year != null ? String(row.year) : undefined,
    year: row.year ?? undefined,
    aspect: ARCADE_ASPECT,
    imageUrl: coverUrl(row) ?? hueSvg(hue, 100, 133),
    hue,
    rating: row.rating ?? undefined,
    sortKey: title,
    badges,
    raw: row,
  };
}

const rawOf = (i: CardItem) => (i.raw ?? {}) as ArcadeGameRow;

export const ARCADE_LIST_COLUMNS: ListColumn[] = [
  { key: "title", label: "Title", width: "2fr", value: (i) => i.title },
  { key: "system", label: "System", width: "110px", value: (i) => rawOf(i).system },
  { key: "year", label: "Year", width: "64px", mono: true, value: (i) => i.year },
  { key: "players", label: "Players", width: "70px", mono: true, align: "right", value: (i) => rawOf(i).maxPlayers },
  { key: "rating", label: "Rating", width: "64px", mono: true, align: "right", value: (i) => rawOf(i).rating },
  { key: "genres", label: "Genres", width: "1.2fr", value: (i) => rawOf(i).genres },
  { key: "developer", label: "Developer", width: "1fr", value: (i) => rawOf(i).developer },
];

async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const r = await fetch(url, { signal });
  if (!r.ok) throw new Error(`${url} → ${r.status}`);
  return (await r.json()) as T;
}

/** The Newspaper's per-group detail: the group's best-rated game tells the story. */
export function groupDetail(rows: ArcadeGameRow[]): CardGroup["detail"] {
  const lead = [...rows].sort((a, b) => (b.rating ?? -1) - (a.rating ?? -1)).find((r) => r.summary);
  if (!lead) return undefined;
  const byline = [lead.developer, lead.publisher].filter(Boolean).filter((v, i, a) => a.indexOf(v) === i).join(" · ");
  const tags = (lead.genres ?? "").split(/[,;/]/).map((t) => t.trim()).filter(Boolean).slice(0, 4);
  return { synopsis: lead.summary, byline: byline ? `${lead.title} — ${byline}` : lead.title, kicker: lead.system, tags };
}

function toGroup(g: GroupRow): CardGroup {
  const rows = g.items ?? [];
  const items = rows.map((row) => ({ ...toArcadeCard(row), groupKey: g.key }));
  return { key: g.key, label: g.label, totalItems: g.totalItems, renderTotal: g.renderTotal ?? g.totalItems, items, detail: groupDetail(rows) };
}

export interface ArcadeSourceOptions {
  filters: ArcadeFilters;
  /** Names what makes the list a DIFFERENT list (the lobby's filterKey). */
  filterKey: string;
  /** Open the lobby's modal for a card (the lobby's `openGame(card)`). */
  onOpen: (row: ArcadeGameRow) => void;
  /** Apply a lobby filter (`?system=`, `?genre=`) — a group header's click. */
  onFilter?: (param: string, value: string) => void;
}

export function createArcadeSource(o: ArcadeSourceOptions): CatalogSource {
  const f = o.filters;
  const sort = serverSort(f.sort);
  const scope = { system: f.system, hideRegions: f.hideRegions, maxPlayers: f.maxPlayers, variant: f.variant, genre: f.genre, search: f.search, ra: f.ra };
  const alpha = sort === "";
  let knownTotal = -1;

  const fetchGroupMore = async (groupKey: string, skip: number, top: number, groupBy: string, _sort: string, signal?: AbortSignal): Promise<CardPage> => {
    const data = await getJson<{ groups?: GroupRow[] }>(`/API/Arcade/GameGroups${arcadeQuery({ ...scope, sort, groupBy, singleGroupKey: groupKey, perGroupSkip: skip, perGroupTop: top })}`, signal);
    const g = data.groups?.[0];
    return g ? { items: toGroup(g).items, total: g.totalItems } : { items: [], total: 0 };
  };

  return {
    queryKey: `arcade:${o.filterKey}`,
    title: "Arcade",
    itemNoun: "game",
    groupNoun: "groups",
    supports: ALL_VIEWS,
    groups: ARCADE_GROUPS,
    sorts: ARCADE_SORTS,
    currentSort: alpha ? ARCADE_DEFAULT_SORT : sort,
    itemsModes: ["items", "groups"],
    itemsLabels: { items: "Games", groups: "One per group" },
    listColumns: ARCADE_LIST_COLUMNS,
    defaultGroup: "system",
    pageSize: ARCADE_PAGE_SIZE,
    defaultAspect: ARCADE_ASPECT,
    directory: {
      roots: async (signal?: AbortSignal): Promise<DirectoryNode[]> => {
        const nodes: DirectoryNode[] = [];
        let total = Infinity;
        for (let page = 0; page < DIRECTORY_MAX_PAGES && nodes.length < total; page += 1) {
          const data = await getJson<{ totalGroups: number; groups: GroupRow[] }>(
            `/API/Arcade/GameGroups${arcadeQuery({ ...scope, sort, groupBy: "system", groupsSkip: page * DIRECTORY_HEADS_PAGE, groupsTop: DIRECTORY_HEADS_PAGE, perGroupTop: 1 })}`,
            signal,
          );
          total = data.totalGroups;
          if (!data.groups?.length) break;
          for (const g of data.groups) {
            const rep = g.items?.[0] ? toArcadeCard(g.items[0]) : null;
            nodes.push({ id: g.key, label: g.label, count: g.totalItems, imageUrl: rep?.imageUrl, hue: rep?.hue ?? hueOf(g.label) });
          }
        }
        return nodes;
      },
      children: async () => [],
      items: (id, skip, top, signal) => fetchGroupMore(id, skip, top, "system", sort, signal),
    },
    fetchFlatBand: async (skip, top, _sort, signal) => {
      const r = await MovieAPI.getArcadeGames({ ...scope, sort, skip, pageSize: top }, signal);
      if (!r.ok) throw new Error(`/API/Arcade/Games → ${r.status}`);
      const data = (await r.json()) as { games?: ArcadeGameRow[]; totalCount?: number };
      if (typeof data.totalCount === "number" && data.totalCount >= 0) knownTotal = data.totalCount;
      return { items: (data.games ?? []).map(toArcadeCard), total: knownTotal };
    },
    fetchGroupBand: async (groupsSkip, groupsTop, perGroupTop, groupBy, _sort, signal): Promise<GroupPage> => {
      const data = await getJson<{ totalGroups: number; groups: GroupRow[] }>(`/API/Arcade/GameGroups${arcadeQuery({ ...scope, sort, groupBy, groupsSkip, groupsTop, perGroupTop })}`, signal);
      return { groups: (data.groups ?? []).map(toGroup), totalGroups: data.totalGroups ?? 0 };
    },
    fetchGroupMore,
    letters: alpha
      ? async (): Promise<LetterBucket[]> => {
          const r = await MovieAPI.getArcadeGameLetters(scope);
          if (!r.ok) return [];
          return ((await r.json()) as { letters?: LetterBucket[] }).letters ?? [];
        }
      : undefined,
    groupLetters: async (groupBy, _sort, signal) =>
      (await getJson<{ letters?: { letter: string; firstIndex: number }[] }>(`/API/Arcade/GameGroupLetters${arcadeQuery({ ...scope, groupBy })}`, signal)).letters ?? [],
    onOpen: (item) => o.onOpen(rawOf(item)),
    onOpenGroup: o.onFilter
      ? (group, groupBy) => {
          const param = GROUP_FILTER_PARAM[groupBy];
          if (param) o.onFilter!(param, group.key);
        }
      : undefined,
  };
}
