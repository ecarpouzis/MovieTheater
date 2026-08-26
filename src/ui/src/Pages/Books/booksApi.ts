/**
 * The Books host, as the SPA reaches it: every call goes to `/API/Books/<route>` on the site's own
 * origin (the pod's Yarp route strips the prefix and stamps the identity header), same-origin
 * cookies included. Typed against the R6 controllers — `src/MovieTheater.Books/Controllers/*` — and
 * the S0 additions. Media BYTES never come through here: see `booksMedia.ts`.
 *
 * Conventions: a 204 reads as `null`; any other non-2xx throws `BooksApiError { status, url }` so a
 * caller can tell a 403 (re-mint the media token, or the grant is gone) from a 404 (gone/hidden).
 * Enums arrive as strings (the host's `JsonStringEnumConverter`).
 */

export const BOOKS_API = "/API/Books";

export class BooksApiError extends Error {
  constructor(public readonly status: number, public readonly url: string, message?: string) {
    super(message ?? `${url} → ${status}`);
    this.name = "BooksApiError";
  }
}

// ── the host's shapes ──

export type ItemKind = "comic" | "book";
export type DatePrecision = "None" | "Year" | "Month" | "Day";
export type SynopsisSource = "None" | "Cv" | "Embedded" | "Locg" | "External" | "Mu" | "CvDeck" | "AI";
export type TagSource = "ComicInfo" | "Cv" | "Calibre" | "Locg" | "Gcd" | "External" | "Mu" | "AI";
export type TrackRole = "Primary" | "Container" | "Alternate";
export type CollectionLevel = "Issue" | "Volume" | "Book" | "Omnibus";

export interface ItemSummary {
  id: number;
  kind: ItemKind;
  title: string | null;
  seriesId: number | null;
  series: string | null;
  seriesIssueCount: number | null;
  seriesYearStart: number | null;
  seriesYearEnd: number | null;
  seriesIsOngoing: boolean;
  franchise: string | null;
  isSingleIssueSeries: boolean;
  seriesRatingResolved: number | null;
  publisher: string | null;
  year: number | null;
  month: number | null;
  datePrecision: DatePrecision;
  rating: number | null;
  synopsisSource: SynopsisSource;
  creatorsCsv: string | null;
  tagsCsv: string | null;
  coverAspect: number | null;
  fileName: string;
  extension: string | null;
  fileSize: number;
  pageCount: number | null;
  indexedAt: string | null;
  folderId: number;
  topFolderId: number | null;
  isExcluded: boolean;
}

export interface FacetOption { value: string; count: number }
export interface SeriesFacetOption { id: number; value: string; count: number }
export interface PublisherFacetOption { id: number | null; name: string; full: string | null; count: number }
export interface CollectionFacetOption { id: number; name: string; count: number }
export interface BrowseFacetsResult {
  series: SeriesFacetOption[];
  tags: FacetOption[];
  authors: FacetOption[];
  artists: FacetOption[];
  events: FacetOption[];
  franchises: FacetOption[];
  publishers: PublisherFacetOption[];
  collections: CollectionFacetOption[];
  decades: FacetOption[];
}

export interface GroupUserMark { isRead: boolean; wantToRead: boolean; isFavorite: boolean; rating: number | null; notes: string | null }
export interface GroupDetailResult { aiSynopsis: string | null; aiRating: number | null; aiKnownSeries: boolean; aiTags: string[] }
export interface BrowseGroupItem {
  key: string;
  label: string;
  totalItems: number;
  items: ItemSummary[];
  userMeta: GroupUserMark | null;
  groupDetail: GroupDetailResult | null;
  renderTotal: number | null;
}
export interface BrowseGroupsResponse { totalGroups: number; groups: BrowseGroupItem[] }
export interface GroupLetter { letter: string; firstIndex: number }

export interface ReadingOrderBlock {
  seriesId: number | null; readTier: number | null; readNumber: number | null; readDate: string | null;
  readDatePrecision: DatePrecision; readIndex: number | null; readCount: number; source: string; confidence: string;
}
export interface CollectionBlock {
  level: CollectionLevel; trackRole: TrackRole; spanStart: number | null; spanEnd: number | null;
  containsCount: number; parentItemId: number | null; spanSource: string; spanLabel: string | null;
}
export interface SeriesRunRow { item: ItemSummary; readingOrder: ReadingOrderBlock | null; collection: CollectionBlock | null }
export interface SeriesRun { seriesId: number; total: number; items: SeriesRunRow[] }

export interface CreditRow { source: TagSource; ordinal: number; role: string | null; name: string | null }
export interface TagRow { source: TagSource; category: string; value: string }
export interface InsightBlock {
  modelId: string; confidence: string; recognized: boolean; rating: number | null; synopsis: string | null;
  author: string | null; artist: string | null; yearBegin: number | null; yearEnd: number | null; maturity: number | null;
  generatedAt: string | null; tags: string[];
}
export interface ParsedBlock {
  seriesKey: string | null; issueNo: string | null; year: number | null; volumeNo: number | null; publisher: string | null;
  format: string; formatRaw: string | null; isCollection: boolean; eventName: string | null; issueTitle: string | null;
}
export interface SeriesBlock {
  id: number; name: string | null; displayNameOverride: string | null; issueCount: number; yearStart: number | null;
  yearEnd: number | null; isOngoing: boolean; franchise: string | null; resolvedRating: number | null;
}
export interface ItemDetail {
  summary: ItemSummary;
  relativePath: string;
  folderName: string | null;
  folderPath: string | null;
  topFolderId: number | null;
  topFolderName: string | null;
  hasThumbnail: boolean;
  embedded: { summary: string | null; publisher: string | null; storyArc: string | null; format: string | null } | null;
  parsed: ParsedBlock | null;
  book: { isbn: string | null; seriesName: string | null; seriesIndex: number | null; publisher: string | null; publishedOn: string | null; language: string | null; description: string | null } | null;
  series: SeriesBlock | null;
  insight: InsightBlock | null;
  seriesInsight: InsightBlock | null;
  cvVolume: { id: number; name: string | null; deck: string | null; description: string | null } | null;
  cvIssue: { id: number; name: string | null; deck: string | null; description: string | null } | null;
  locg: { description: string | null; communityRating: number | null; ratingCount: number | null; isKey: boolean; keyType: string | null } | null;
  mu: { description: string | null; bayesianRating: number | null } | null;
  external: { provider: string | null; description: string | null } | null;
  readingOrder: ReadingOrderBlock | null;
  collection: CollectionBlock | null;
  credits: CreditRow[];
  tags: TagRow[];
  seriesTags: TagRow[];
  thumbUrl: string | null;
  downloadUrl: string | null;
  pagesUrlTemplate: string | null;
}

export interface FolderNode {
  id: number; name: string | null; path: string | null; depth: number; parentId: number | null;
  directChildCount: number; descendantItemCount: number; hasIcon: boolean; iconUrl: string | null;
}
export interface FolderPage { folder: FolderNode; kind: string; children: FolderNode[]; totalItems: number; skip: number; top: number; items: ItemSummary[] }

export interface MediaTokenResponse { configured: boolean; token?: string; baseUrl?: string; expiresUtc?: string }

export interface ReadingPosition {
  itemId: number; lastPage: number; lastSpineItemIndex: number | null; lastScrollPercent: number | null;
  status: "unread" | "inprogress" | "finished"; wantToRead: boolean; favorite: boolean; hiddenFromHistory: boolean; updatedAt: string | null;
}
export interface HistoryEntry {
  itemId: number; lastPage: number; lastSpineItemIndex: number | null; lastScrollPercent: number | null;
  status: string; wantToRead: boolean; favorite: boolean; updatedAt: string | null; item: ItemSummary | null;
}
export interface HistoryPage { totalCount: number; skip: number; top: number; entries: HistoryEntry[] }
export interface ItemMark { itemId: number; wantToRead: boolean; favorite: boolean; status: string; rating: number | null; updatedAt: string | null; item: ItemSummary | null }
export interface ItemMarksPage { totalCount: number; skip: number; top: number; entries: ItemMark[] }
export interface GroupMark { groupType: string; groupKey: string; label: string | null; isRead: boolean; wantToRead: boolean; isFavorite: boolean; rating: number | null; notes: string | null; updatedAt: string | null }
export interface GroupMarkUpsertResult { mark: GroupMark; issuesMarked: number; issuesRemaining: number }
export interface ShelfSeriesCard {
  seriesId: number; seriesName: string; issueCount: number; finishedCount: number; seriesIssueCount: number | null;
  coverItemId: number | null; publisher: string | null; year: number | null; yearEnd: number | null; isOngoing: boolean;
  isRead: boolean; wantToRead: boolean; isFavorite: boolean; rating: number | null;
}
export interface ShelfSeriesPage { totalCount: number; skip: number; top: number; series: ShelfSeriesCard[] }
export interface SeriesProgress { seriesId: number; total: number; finishedCount: number; finishedIds: number[]; inProgressIds: number[] }

export interface ExploreCard {
  kind: string; id: number; key: string; title: string; subtitle: string | null; label: string | null; year: number | null;
  aspect: number; imageUrl: string | null; imageThumbUrl: string | null; hue: number | null; rating: number | null;
  badges: { label: string; tone: string | null; title: string | null }[] | null; groupKey: string | null; sortKey: string | null; raw: unknown;
}
export interface ExploreRailDto { key: string; title: string; kind: "strip" | "wall" | "grid"; items: ExploreCard[]; more: { href: string } | null }
export interface ExploreResponseDto { spotlight: ExploreCard[]; rails: ExploreRailDto[]; seed: number }

export interface NovelsPage { total: number; skip: number; top: number; items: ItemSummary[]; covers: Record<string, string | null>; maturity: Record<string, number | null> }
export interface NovelFacetOption { value: string; count: number }
export interface NovelFacets { authors: NovelFacetOption[]; series: NovelFacetOption[]; publishers: NovelFacetOption[]; decades: NovelFacetOption[]; tags: NovelFacetOption[] }

/** Bubble Zoom: a text block on a page, every number normalized 0–1 to the page's size. */
export interface TextRegion {
  x: number; y: number; width: number; height: number;
  hitX: number; hitY: number; hitWidth: number; hitHeight: number;
  pol: number; glyphs: number;
}
export interface EpubSpineItem { index: number; href: string; title?: string | null }
export interface EpubSpine { id: number; count: number; fixedLayout: boolean; direction: string | null; items: EpubSpineItem[] }
/** `spineIndex` -1 = a heading whose target is not in the reading order. */
export interface EpubTocEntry { label: string; spineIndex: number; anchor?: string | null; depth: number }

export interface KidsBrowseResponse { totalGroups: number; groups: BrowseGroupItem[]; covers: Record<string, string | null> }
export interface KidsSeriesItems { series: { id: number; name: string; rating: number | null }; total: number; skip: number; top: number; items: ItemSummary[]; covers: Record<string, string | null> }

/** The exact facet filters the host takes beside `$filter` (S0). Repeatable params, never CSV. */
export interface ExactParams {
  author?: string[]; artist?: string[]; tag?: string[]; event?: string[];
  exAuthor?: string[]; exArtist?: string[]; exTag?: string[]; exEvent?: string[];
}

// ── plumbing ──

type Param = string | number | boolean | null | undefined | string[];

export function qs(params: Record<string, Param>): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v == null || v === "" || v === false) continue;
    if (Array.isArray(v)) {
      for (const item of v) if (item !== "") p.append(k, item);
    } else {
      p.set(k, String(v));
    }
  }
  const s = p.toString();
  return s ? `?${s}` : "";
}

async function request<T>(path: string, init?: RequestInit, signal?: AbortSignal): Promise<T> {
  const url = `${BOOKS_API}${path}`;
  const r = await fetch(url, { credentials: "same-origin", ...init, signal });
  if (r.status === 204) return null as T;
  if (!r.ok) throw new BooksApiError(r.status, url);
  return (await r.json()) as T;
}

const json = (body: unknown): RequestInit => ({
  method: "PUT",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify(body),
});

// ── catalog + browse ──

export interface CatalogQuery {
  q?: string; kind?: ItemKind; directory?: number; filter?: string | null; orderby?: string | null;
  skip?: number; top?: number; count?: boolean; exact?: ExactParams;
}

/** `/odata/catalog` — a plain JSON array; the total rides the `X-Total-Count` header when `count` is asked for. */
export async function fetchCatalog(query: CatalogQuery, signal?: AbortSignal): Promise<{ items: ItemSummary[]; total: number }> {
  const url = `${BOOKS_API}/odata/catalog${qs({
    q: query.q, kind: query.kind, directory: query.directory,
    $filter: query.filter, $orderby: query.orderby, $skip: query.skip, $top: query.top,
    $count: query.count ? "true" : undefined, ...(query.exact ?? {}),
  })}`;
  const r = await fetch(url, { credentials: "same-origin", signal });
  if (!r.ok) throw new BooksApiError(r.status, url);
  const items = (await r.json()) as ItemSummary[];
  const header = r.headers.get("X-Total-Count");
  const total = header != null && header !== "" && Number.isFinite(Number(header)) ? Number(header) : -1;
  return { items: Array.isArray(items) ? items : [], total };
}

export const fetchFacets = (kind: ItemKind = "comic", signal?: AbortSignal) =>
  request<BrowseFacetsResult>(`/browse/facets${qs({ kind })}`, undefined, signal);

export const fetchFacetOptions = (field: "authors" | "artists" | "tags", q: string, skip: number, top: number, kind: ItemKind = "comic", signal?: AbortSignal) =>
  request<{ items: FacetOption[]; total: number }>(`/browse/facet-options${qs({ field, q, skip, top, kind })}`, undefined, signal);

export interface GroupsQuery {
  groupBy: string; q?: string; orderby?: string | null; groupsTop?: number; groupsSkip?: number; perGroupTop?: number; perGroupSkip?: number;
  filter?: string | null; subGroupBy?: string; singleGroupKey?: string; kind?: ItemKind; wantToReadOnly?: boolean; readOnly?: boolean; exact?: ExactParams;
}

const groupsQs = (g: GroupsQuery) => qs({
  groupBy: g.groupBy, q: g.q, orderby: g.orderby, groupsTop: g.groupsTop, groupsSkip: g.groupsSkip, perGroupTop: g.perGroupTop, perGroupSkip: g.perGroupSkip,
  $filter: g.filter, subGroupBy: g.subGroupBy, singleGroupKey: g.singleGroupKey, kind: g.kind, wantToReadOnly: g.wantToReadOnly, readOnly: g.readOnly, ...(g.exact ?? {}),
});

export const fetchGroups = (g: GroupsQuery, signal?: AbortSignal) =>
  request<BrowseGroupsResponse>(`/browse/groups${groupsQs(g)}`, undefined, signal);

export const fetchGroupLetters = (g: Pick<GroupsQuery, "groupBy" | "q" | "filter" | "kind" | "wantToReadOnly" | "readOnly" | "exact">, signal?: AbortSignal) =>
  request<{ totalGroups: number; letters: GroupLetter[] }>(`/browse/group-letters${groupsQs({ ...g })}`, undefined, signal);

/** One bucket of the flat A–Z strip (the catalog package's `LetterBucket` shape). */
export interface LetterBucket { letter: string; count: number; offset: number }

/** `/browse/letters` — the flat sibling of group-letters, over the same filters; `sort` = series | title | publisher. */
export const fetchLetters = (g: { sort?: string } & Pick<GroupsQuery, "q" | "filter" | "kind" | "wantToReadOnly" | "readOnly" | "exact">, signal?: AbortSignal) =>
  request<{ total: number; letters: LetterBucket[] }>(
    `/browse/letters${qs({ sort: g.sort, q: g.q, $filter: g.filter, kind: g.kind, wantToReadOnly: g.wantToReadOnly, readOnly: g.readOnly, ...(g.exact ?? {}) })}`,
    undefined, signal);

export const fetchGroupItems = (groupBy: string, key: string, g: { skip?: number; top?: number } & Pick<GroupsQuery, "orderby" | "q" | "filter" | "kind" | "wantToReadOnly" | "readOnly" | "exact">, signal?: AbortSignal) =>
  request<{ items: ItemSummary[]; total: number }>(
    `/browse/groups/${encodeURIComponent(groupBy)}/${encodeURIComponent(key)}/items${qs({ skip: g.skip, top: g.top, orderby: g.orderby, q: g.q, $filter: g.filter, kind: g.kind, wantToReadOnly: g.wantToReadOnly, readOnly: g.readOnly, ...(g.exact ?? {}) })}`,
    undefined, signal);

export const fetchSeriesLibraryRating = (seriesId: number, signal?: AbortSignal) =>
  request<{ rating: number | null; note: string | null }>(`/browse/series/${seriesId}/library-rating`, undefined, signal);

export const fetchSeriesRun = (seriesId: number, signal?: AbortSignal) =>
  request<SeriesRun>(`/browse/series/${seriesId}/run`, undefined, signal);

// ── items, folders, epub, media ──

export const fetchItem = (id: number, mediaToken?: string | null, signal?: AbortSignal) =>
  request<ItemDetail>(`/items/${id}${qs({ mediaToken })}`, undefined, signal);

export const fetchNext = (id: number, mediaToken?: string | null) =>
  request<{ via: string; item: ItemDetail } | null>(`/items/${id}/next${qs({ mediaToken })}`);

export const fetchPrev = (id: number, mediaToken?: string | null) =>
  request<{ via: string; item: ItemDetail } | null>(`/items/${id}/prev${qs({ mediaToken })}`);

export const fetchTextRegions = (id: number, page: number, signal?: AbortSignal) =>
  request<{ regions: TextRegion[] }>(`/items/${id}/pages/${page}/text-regions`, undefined, signal);

export const thumbsBatch = (ids: number[], mediaToken?: string | null) =>
  request<Record<string, { url: string; etag: string | null } | null>>("/thumbs/batch", { ...json({ ids, mediaToken }), method: "POST" });

export const fetchRandom = (kind: ItemKind = "comic", mediaToken?: string | null) => request<ItemDetail>(`/items/random${qs({ kind, mediaToken })}`);
export const fetchLatest = (kind: ItemKind, skip: number, top: number) => request<{ total: number; skip: number; top: number; items: ItemSummary[] }>(`/items/latest${qs({ kind, skip, top })}`);
export const fetchFeatured = (kind: ItemKind, count: number, seed?: number) => request<{ seed: number; items: ItemSummary[] }>(`/items/featured${qs({ kind, count, seed })}`);

export const fetchLibraryFolders = (kind: ItemKind, parentId?: number | null, signal?: AbortSignal) =>
  request<FolderNode[]>(`/library/${kind}/folders${qs({ parentId })}`, undefined, signal);

export const fetchFolder = (id: number, p: { kind?: ItemKind; skip?: number; top?: number; orderby?: string } = {}, signal?: AbortSignal) =>
  request<FolderPage>(`/folders/${id}${qs(p)}`, undefined, signal);

export const fetchFolderParent = (id: number) => request<{ parentId: number | null; parent: FolderNode | null }>(`/folders/${id}/parent`);

export const fetchEpubSpine = (id: number, signal?: AbortSignal) => request<EpubSpine>(`/epub/${id}/spine`, undefined, signal);
export const fetchEpubToc = (id: number, signal?: AbortSignal) => request<{ id: number; count: number; entries: EpubTocEntry[] }>(`/epub/${id}/toc`, undefined, signal);
export async function fetchEpubChapterHtml(id: number, spineIndex: number, signal?: AbortSignal): Promise<string> {
  const url = `${BOOKS_API}/epub/${id}/chapters/${spineIndex}`;
  const r = await fetch(url, { credentials: "same-origin", signal });
  if (!r.ok) throw new BooksApiError(r.status, url);
  return r.text();
}

export const fetchMediaToken = () => request<MediaTokenResponse>("/media-token");

// ── positions (the ONE progress API) ──

export const getPosition = (id: number, signal?: AbortSignal) => request<ReadingPosition>(`/positions/${id}`, undefined, signal);
/** `keepalive` lets a page-unload flush outlive the document (the readers' last position). */
export const putPosition = (id: number, body: { lastPage?: number; lastSpineItemIndex?: number; lastScrollPercent?: number }, opts?: { keepalive?: boolean }) =>
  request<ReadingPosition>(`/positions/${id}`, { ...json(body), keepalive: opts?.keepalive });
/** An empty body: "I opened it" — surfaces the item on Last opened without moving the page. */
export const touchPosition = (id: number) => request<ReadingPosition>(`/positions/${id}`, json({}));
/** `lastPage: -1` is the ONLY Finished signal. */
export const markFinished = (id: number) => request<ReadingPosition>(`/positions/${id}`, json({ lastPage: -1 }));
export const resetPosition = (id: number) => request<null>(`/positions/${id}`, { method: "DELETE" });
export const hideFromHistory = (id: number) => request<null>(`/positions/${id}/hide`, { method: "POST" });
export const fetchHistory = (status: "opened" | "inprogress" | "finished", skip = 0, top = 48, signal?: AbortSignal) =>
  request<HistoryPage>(`/positions/history${qs({ status, skip, top })}`, undefined, signal);

// ── marks ──

export const fetchItemMarks = (kind: "want" | "favorite" | "read", skip = 0, top = 48, signal?: AbortSignal) =>
  request<ItemMarksPage>(`/marks/items${qs({ kind, skip, top })}`, undefined, signal);
export const fetchItemMark = (id: number, signal?: AbortSignal) => request<ItemMark>(`/marks/items/${id}`, undefined, signal);
/** `rating` is tri-state: omit = untouched, `null` = clear, number = set. */
export const putItemMark = (id: number, body: { wantToRead?: boolean; favorite?: boolean; rating?: number | null }) =>
  request<ItemMark>(`/marks/items/${id}`, json(body));
export const deleteItemMark = (id: number, kind: "want" | "favorite" | "rating") => request<null>(`/marks/items/${id}/${kind}`, { method: "DELETE" });
export const fetchGroupMarks = (groupType = "series", signal?: AbortSignal) => request<GroupMark[]>(`/marks/groups${qs({ groupType })}`, undefined, signal);
export const putGroupMark = (groupType: string, key: string, body: { isRead?: boolean; wantToRead?: boolean; isFavorite?: boolean; rating?: number | null; notes?: string | null }) =>
  request<GroupMarkUpsertResult>(`/marks/groups/${encodeURIComponent(groupType)}/${encodeURIComponent(key)}`, json(body));
export const deleteGroupMark = (groupType: string, key: string) =>
  request<null>(`/marks/groups/${encodeURIComponent(groupType)}/${encodeURIComponent(key)}`, { method: "DELETE" });
export const groupMarksBatch = (items: { groupType: string; groupKey: string }[]) =>
  request<GroupMark[]>("/marks/groups/batch", { ...json({ items }), method: "POST" });

// ── shelf, suggestions, explore, kids, novels ──

export const fetchShelfSeries = (kind: "read" | "want", skip = 0, top = 100, signal?: AbortSignal) =>
  request<ShelfSeriesPage>(`/shelf/series${qs({ kind, skip, top })}`, undefined, signal);
export const fetchSeriesProgress = (seriesId: number, signal?: AbortSignal) => request<SeriesProgress>(`/shelf/series/${seriesId}/progress`, undefined, signal);
export const fetchContinue = (skip = 0, top = 24, signal?: AbortSignal) => request<HistoryPage>(`/shelf/continue${qs({ skip, top })}`, undefined, signal);
export const fetchLastOpened = (skip = 0, top = 24, signal?: AbortSignal) => request<HistoryPage>(`/shelf/last-opened${qs({ skip, top })}`, undefined, signal);
export const fetchSuggestions = (count = 12, seed?: number, signal?: AbortSignal) =>
  request<{ count: number; items: ItemSummary[] }>(`/suggestions${qs({ count, seed })}`, undefined, signal);
export const fetchExplore = (kind: ItemKind = "comic", seed?: number, signal?: AbortSignal) =>
  request<ExploreResponseDto>(`/explore${qs({ kind, seed })}`, undefined, signal);
export const fetchExploreKids = (seed?: number, signal?: AbortSignal) => request<ExploreResponseDto>(`/explore/kids${qs({ seed })}`, undefined, signal);
export const fetchKidsBrowse = (p: { groupsSkip?: number; groupsTop?: number; perGroupTop?: number; mediaToken?: string | null }, signal?: AbortSignal) =>
  request<KidsBrowseResponse>(`/kids/browse${qs({ groupBy: "series", ...p })}`, undefined, signal);
export const fetchKidsSeriesItems = (seriesId: number, skip = 0, top = 40, mediaToken?: string | null, signal?: AbortSignal) =>
  request<KidsSeriesItems>(`/kids/series/${seriesId}/items${qs({ skip, top, mediaToken })}`, undefined, signal);

export interface NovelsQuery {
  author?: string; series?: string; publisher?: string; decade?: string; tag?: string; q?: string;
  skip?: number; top?: number; orderby?: string; excludeTag?: string; minRating?: number; unknown?: boolean;
}
export const fetchNovels = (p: NovelsQuery, signal?: AbortSignal) => request<NovelsPage>(`/novels${qs({ ...p })}`, undefined, signal);
export const fetchNovelFacets = (signal?: AbortSignal) => request<NovelFacets>("/novels/facets", undefined, signal);
/** `/novels/letters` — the flat A–Z buckets over the list's filters; `orderby=title` buckets on the title, else the author line. */
export const fetchNovelLetters = (p: Omit<NovelsQuery, "skip" | "top">, signal?: AbortSignal) =>
  request<{ total: number; letters: LetterBucket[] }>(`/novels/letters${qs({ ...p })}`, undefined, signal);
export const fetchNovel = (id: number, mediaToken?: string | null) => request<ItemDetail>(`/novels/${id}${qs({ mediaToken })}`);

// ── the one site-side write Books owns ──

/** The kids style is a SITE user setting (`BooksKidsStyle`), never a host fact. */
export async function setKidsStyle(style: "pop" | "bubble"): Promise<void> {
  const r = await fetch("/API/SetUserSetting", {
    method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ settingKey: "BooksKidsStyle", settingValue: style }),
  });
  if (!r.ok) throw new BooksApiError(r.status, "/API/SetUserSetting");
}
