/**
 * The Movies/TV Explore composition (R9 S7) — assembled IN THE BROWSER out of endpoints the section
 * already served. Nothing here is a new browse: every rail is a named query the site answers today,
 * mapped onto the catalog package's cards by `moviesSource.toCard`, so a rail's cards are the same
 * cards its browse would show.
 *
 * | Rail | Where it comes from |
 * |---|---|
 * | spotlight + `random` | `/API/Browse?sort=random&seed=` — one seeded page, hero off the top |
 * | `continue` | `/API/ContinueWatching` (R9 S7's one new movie read: a resume position had no route) |
 * | `now-on-tv` | the channel lineup `useChannelLineup` already builds for the homepage rail |
 * | `for-you` | `/API/Recommendations` (the `TitleRecommendation` rows the maintenance service keeps) |
 * | `suggested` | the head of `/API/Me`'s `moviesSuggested` (newest first), resolved by `/API/GetMoviesByIds` |
 * | `recent` | `/API/Browse?sort=added` |
 * | `franchises` | `/API/BrowseGroups?groupBy=franchise` — GROUP cards, routed by facet |
 * | `franchise-run` | `/API/GetFranchiseRail` anchored on the spotlight title — a FOCUS plate |
 * | `ways-in` | `/API/BrowseGroups?groupBy=<axis>&headsBy=count&perGroupTop=3` — one axis at a time, the active tab's |
 * | `director` | `/API/BrowseGroups?groupBy=director&headsBy=count` — one of the most-represented, by the seed |
 * | `top` | `/API/Browse?sort=imdb&pageSize=10` — the ranked ten |
 * | `quick` | three columns: `/API/Browse?sort=rt`, a seeded decade (`yearMin/yearMax`), a seeded mood (`tag=mood:`) |
 *
 * The composer is PURE: it takes what the queries returned and answers the payload, so the rails and
 * their "More →" targets can be asserted without a network.
 */
import { exploreColumn, exploreColumns, exploreDoors, exploreFocus, exploreRail, exploreResponse, groupCard, distinctCovers, humanizeKey, seededItem, seededShuffle } from "../../catalog/explore/composeExplore";
import { facetHref } from "../../catalog/rail/facetUrl";
import { MovieAPI } from "../../MovieAPI";
import type { CardItem, ExploreDoor, ExploreResponse } from "../../catalog/types";
import { POSTER_ASPECT, hueOf, toCard, type MovieCardRow } from "../../catalog/sources/moviesSource";
import { browseSearchFor } from "./moviesFacetSpec";

// ── Wire shapes ────────────────────────────────────────────────────────────────────────────────

export interface ContinueRow { card: MovieCardRow; percent: number; lastPlayedUtc?: string; note?: string | null }
export interface RecommendationRow { card: MovieCardRow; score?: number; reason?: string | null }
export interface FranchiseGroupRow { key: string; label: string; totalItems: number; items?: MovieCardRow[] }
export interface FranchiseRailItem { id: number; kind: string; title: string; year?: number | null; posterVersion?: number; streamable?: boolean; isCurrent?: boolean }
export interface FranchiseRailDto { defaultFranchise?: string | null; franchises?: { value: string; count: number; items: FranchiseRailItem[] }[] }
/** One row of the channel lineup `useChannelLineup` builds (only what a card needs). */
export interface LineupChannel {
  id: number;
  name: string;
  category?: string | null;
  viewers?: number;
  now?: { title?: string | null; posterId?: number | null; posterVersion?: number; kind?: string | null } | null;
}

export interface MoviesExploreInput {
  random?: MovieCardRow[];
  recent?: MovieCardRow[];
  continueWatching?: ContinueRow[];
  recommendations?: RecommendationRow[];
  franchiseGroups?: FranchiseGroupRow[];
  /** What friends suggested to the viewer, newest first (the head of `userData.moviesSuggested`,
   *  resolved to cards by `/API/GetMoviesByIds`). */
  suggested?: MovieCardRow[];
  franchiseRun?: FranchiseRailDto | null;
  lineup?: LineupChannel[] | null;
  /** "Ways in": each axis's biggest groups (`/API/BrowseGroups?headsBy=count&perGroupTop=3`), as they load. */
  doors?: Partial<Record<MovieDoorAxis, FranchiseGroupRow[]>>;
  doorAxis?: MovieDoorAxis;
  /** The most-represented directors, each with their best-rated titles (the focus plate picks one). */
  directors?: FranchiseGroupRow[];
  /** `/API/Browse?sort=imdb` — the ranked ten. */
  top?: MovieCardRow[];
  /** The three quick-pick columns. */
  critics?: MovieCardRow[];
  decade?: { decade: string; rows: MovieCardRow[] } | null;
  mood?: { mood: string; rows: MovieCardRow[] } | null;
  seed?: number;
}

/** How many spotlight cards the hero rotates through. */
export const SPOTLIGHT_SIZE = 5;

// ── The rails' "More →" targets, in the section's own URL vocabulary ────────────────────────────

/** `/?f=franchise:mcu` etc. — the rail URL contract (R9 S2), written straight. */
export function moviesFacetHref(mode: string, value: string): string | null {
  const search = browseSearchFor(mode, value);
  return search == null ? null : `/${search}`;
}

export const MOVIES_MORE: Record<string, string> = {
  suggested: "/?my=suggested",
  "now-on-tv": "/channels",
  recent: "/?sort=added",
  random: "/?sort=random",
  franchises: "/?view=shelf&group=franchise",
  top: "/?sort=imdb&f=type%3AMovies",
  critics: "/?sort=rt",
};

// ── Card mappers ───────────────────────────────────────────────────────────────────────────────

function withCorner(card: CardItem, label: string, title?: string): CardItem {
  const rest = (card.badges ?? []).filter((b) => b.tone === "rating");
  return { ...card, badges: [{ label, tone: "neutral" as const, title }, ...rest] };
}

export function toContinueCard(row: ContinueRow): CardItem | null {
  if (!row?.card) return null;
  const base = toCard(row.card);
  const pct = Math.max(0, Math.min(100, Math.round(row.percent ?? 0)));
  return { ...withCorner(base, `${pct}%`, "Where you left off"), progress: pct, subtitle: row.note || base.subtitle };
}

export function toRecommendationCard(row: RecommendationRow): CardItem | null {
  if (!row?.card) return null;
  const base = toCard(row.card);
  return row.reason ? { ...base, subtitle: row.reason } : base;
}

/** A channel as a card: the poster of what is on RIGHT NOW, the channel's name as the title. */
export function toChannelCard(ch: LineupChannel): CardItem | null {
  if (!ch?.id) return null;
  const now = ch.now ?? null;
  const poster = now?.posterId
    ? MovieAPI.getPosterThumbnail(now.posterId, now.posterVersion ?? 0, now.kind ?? "movie")
    : "";
  return {
    kind: "channel",
    id: ch.id,
    key: `channel:${ch.id}`,
    title: ch.name,
    subtitle: now?.title ?? ch.category ?? undefined,
    label: ch.viewers ? `${ch.viewers} watching` : undefined,
    aspect: POSTER_ASPECT,
    imageUrl: poster,
    hue: hueOf(ch.category || ch.name || ""),
    badges: [{ label: "LIVE", tone: "live" as const, title: "On now" }],
    raw: ch,
  };
}

/** A franchise head as a GROUP card — its first member's poster is the face of the run. */
export function toFranchiseCard(g: FranchiseGroupRow): CardItem | null {
  if (!g?.key) return null;
  const rep = g.items?.[0] ? toCard(g.items[0]) : null;
  return groupCard({
    kind: "franchise",
    key: g.key,
    title: g.label || humanizeKey(g.key),
    count: g.totalItems,
    imageUrl: rep?.imageUrl,
    imageThumbUrl: rep?.imageThumbUrl,
    aspect: POSTER_ASPECT,
    hue: rep?.hue,
    raw: g,
  });
}

/** `/API/GetFranchiseRail`'s own row shape (not a MovieCardDto) onto a card. */
export function toFranchiseRunCard(it: FranchiseRailItem): CardItem | null {
  if (!it?.id) return null;
  const kind = it.kind === "series" ? "series" : "movie";
  const card = toCard({ id: it.id, kind, title: it.title, posterVersion: it.posterVersion ?? 0 });
  return {
    ...card,
    label: it.year ? String(it.year) : undefined,
    year: it.year ?? undefined,
    badges: it.isCurrent ? [{ label: "In the spotlight", tone: "want" as const }] : undefined,
  };
}

// ── Doors, focus, ranked, columns ──────────────────────────────────────────────────────────────

/** The axes the "Ways in" module cuts the library along — each a facet the browse's rail can hold. */
export const MOVIE_DOOR_AXES = [
  { key: "genre", label: "Genre" },
  { key: "mood", label: "Mood" },
  { key: "subgenre", label: "Subgenre" },
  { key: "setting", label: "Setting" },
  { key: "decade", label: "Decade" },
] as const;
export type MovieDoorAxis = (typeof MOVIE_DOOR_AXES)[number]["key"];

/** How many doors an axis shows (two rows at desktop width; the grid clamps the rest away). */
export const DOORS_TAKE = 18;

/** Where a door leads: the browse with exactly that facet (a decade is a year range). */
export function movieDoorHref(axis: string, key: string): string {
  if (axis === "decade") {
    const d = Number(key);
    return Number.isFinite(d) ? `/?y=${d}-${d + 9}` : "/";
  }
  return facetHref("/", [[axis, key]]);
}

function doorLabel(axis: string, g: FranchiseGroupRow): string {
  if (axis === "decade") return g.label || `${g.key}s`;
  return g.label || humanizeKey(g.key);
}

const coverCandidates = (g: FranchiseGroupRow) => (g.items ?? []).map(toCard).map((c) => ({ src: c.imageThumbUrl ?? c.imageUrl, hue: c.hue }));

export function toMovieDoor(axis: string, g: FranchiseGroupRow, covers: ExploreDoor["covers"] = coverCandidates(g).slice(0, 3)): ExploreDoor | null {
  if (!g?.key || !g.totalItems) return null;
  return { key: g.key, label: doorLabel(axis, g), count: g.totalItems, href: movieDoorHref(axis, g.key), covers };
}

/** A door needs this many titles behind it — a one-film "Adult" tile is noise, not a way in. */
export const DOOR_MIN_TITLES = 8;

/**
 * An axis's groups as doors: biggest first (the server's `headsBy=count` order, re-applied here so an
 * older server's alphabetical answer still reads right), decades in their own chronological order,
 * tiny groups dropped.
 */
export function movieDoors(axis: string, rows: readonly FranchiseGroupRow[]): ExploreDoor[] {
  const list = rows.filter((g) => (g.totalItems ?? 0) >= DOOR_MIN_TITLES);
  const ordered = axis === "decade" ? list.slice().sort((a, b) => Number(a.key) - Number(b.key)) : list.slice().sort((a, b) => b.totalItems - a.totalItems);
  const top = ordered.slice(0, DOORS_TAKE);
  const covers = distinctCovers(top, coverCandidates);
  return top.map((g, i) => toMovieDoor(axis, g, covers[i])).filter((d): d is ExploreDoor => !!d);
}

/**
 * The ranked ten: the IMDb order, keeping only titles the critics have ALSO scored. An IMDb number
 * alone is easy to top with a few hundred fan votes (a YouTube review series sat at #1), and a
 * Tomatometer is a cheap, honest proof the thing is a released film with a real audience.
 */
export function rankedFilms(rows: readonly MovieCardRow[] | undefined): MovieCardRow[] {
  return (rows ?? []).filter((r) => r.rtTomatometer != null).slice(0, 10);
}

/** The decades worth a column: enough of the library to fill one, oldest first. */
export const COLUMN_DECADES = ["1930", "1940", "1950", "1960", "1970", "1980", "1990", "2000", "2010"];
/** A mood column needs at least this many titles behind it to be worth the space. */
export const MOOD_COLUMN_MIN = 120;

export function pickColumnDecade(seed: number): string {
  return seededItem(COLUMN_DECADES, seed, 11) ?? "1970";
}

export function pickColumnMood(moods: readonly { value: string; count: number }[] | undefined, seed: number): string | null {
  const list = (moods ?? []).filter((m) => m.value && m.count >= MOOD_COLUMN_MIN);
  return seededItem(list, seed, 23)?.value ?? null;
}

/** The director the focus plate looks at: one of the most-represented, chosen by the seed. */
export function pickDirector(groups: readonly FranchiseGroupRow[] | undefined, seed: number): FranchiseGroupRow | null {
  const list = (groups ?? []).filter((g) => g.key && (g.items?.length ?? 0) >= 3);
  return seededItem(list, seed, 5) ?? null;
}

// ── The composition ────────────────────────────────────────────────────────────────────────────

/**
 * The marquee's picks: the best-rated handful of the seeded shuffle (the shuffle still decides WHICH
 * thirty, so every roll features something different) — a raw shuffle headlined whatever came up.
 * The grid below gets the rest of the shuffle, in its own order, never a duplicate.
 */
export function splitSpotlight(random: readonly CardItem[]): { spotlight: CardItem[]; rest: CardItem[] } {
  const ranked = random.map((c, i) => ({ c, i })).sort((a, b) => (b.c.rating ?? -1) - (a.c.rating ?? -1) || a.i - b.i);
  const picked = new Set(ranked.slice(0, SPOTLIGHT_SIZE).map((x) => x.c.key));
  return { spotlight: ranked.slice(0, SPOTLIGHT_SIZE).map((x) => x.c), rest: random.filter((c) => !picked.has(c.key)) };
}

/** "8 titles, 1987–2002" — how much of a name the shelf holds and over what span. */
export function spanBlurb(count: number, items: readonly CardItem[], noun = "title"): string {
  const years = items.map((i) => i.year).filter((y): y is number => typeof y === "number" && y > 0);
  const span = years.length ? (Math.min(...years) === Math.max(...years) ? `, ${years[0]}` : `, ${Math.min(...years)}\u2013${Math.max(...years)}`) : "";
  return `${count} ${noun}${count === 1 ? "" : "s"} on the shelf${span}. Best-rated first.`;
}

export function composeMoviesExplore(input: MoviesExploreInput): ExploreResponse {
  const random = (input.random ?? []).map(toCard);
  const { spotlight, rest } = splitSpotlight(random);
  const run = pickFranchiseRun(input.franchiseRun);
  const director = pickDirector(input.directors, input.seed ?? 1);
  const doorAxes = MOVIE_DOOR_AXES.map((a) => {
    const rows = input.doors?.[a.key];
    return { key: a.key, label: a.label, doors: rows ? movieDoors(a.key, rows) : undefined };
  });

  return exploreResponse(spotlight, [
    exploreRail("continue", "Keep watching", "resume", (input.continueWatching ?? []).map(toContinueCard)),
    exploreRail("now-on-tv", "On TV right now", "strip", (input.lineup ?? []).map(toChannelCard), MOVIES_MORE["now-on-tv"]),
    exploreDoors("ways-in", "Ways in", doorAxes, input.doorAxis),
    exploreRail("recent", "Just added", "wall", (input.recent ?? []).map(toCard), MOVIES_MORE.recent),
    exploreRail("for-you", "Picked for you", "strip", (input.recommendations ?? []).map(toRecommendationCard)),
    director
      ? exploreFocus(
          "director",
          "A closer look",
          { name: director.label || director.key, count: director.totalItems, blurb: spanBlurb(director.totalItems, (director.items ?? []).map(toCard)), href: moviesFacetHref("actor", director.key) ?? undefined },
          (director.items ?? []).map(toCard),
        )
      : null,
    exploreRail("top", "The ten best-rated films", "ranked", rankedFilms(input.top).map(toCard), MOVIES_MORE.top),
    exploreColumns("quick", "Quick picks", [
      exploreColumn("critics", "Critics' favourites", seededShuffle(input.critics ?? [], (input.seed ?? 1) + 7).slice(0, 6).map(toCard), MOVIES_MORE.critics),
      input.decade ? exploreColumn("decade", `Best of the ${input.decade.decade}s`, input.decade.rows.map(toCard), movieDoorHref("decade", input.decade.decade)) : null,
      input.mood ? exploreColumn("mood", `In a ${input.mood.mood} mood`, input.mood.rows.map(toCard), movieDoorHref("mood", input.mood.mood)) : null,
    ]),
    exploreRail("suggested", "Suggested by friends", "strip", (input.suggested ?? []).map(toCard), MOVIES_MORE.suggested),
    exploreRail("franchises", "Whole runs to binge", "strip", (input.franchiseGroups ?? []).map(toFranchiseCard), MOVIES_MORE.franchises),
    run
      ? exploreFocus(
          "franchise-run",
          "The whole run, in order",
          { name: humanizeKey(run.value), count: run.items.length, href: moviesFacetHref("franchise", run.value) ?? undefined },
          run.items.map(toFranchiseRunCard),
        )
      : null,
    exploreRail("random", "Something else entirely", "grid", rest, MOVIES_MORE.random),
  ], input.seed);
}

/** The rail the endpoint itself calls most specific (fewest members), else the first it returned. */
export function pickFranchiseRun(dto: FranchiseRailDto | null | undefined): { value: string; items: FranchiseRailItem[] } | null {
  const list = dto?.franchises ?? [];
  if (list.length === 0) return null;
  const pick = list.find((f) => f.value === dto?.defaultFranchise) ?? list[0];
  return pick?.items?.length ? { value: pick.value, items: pick.items } : null;
}

/** Rails whose point is that they are CURRENT (or fixed) — a shuffle would be a lie. */
export const MOVIES_UNSEEDED_RAILS: ReadonlySet<string> = new Set(["continue", "now-on-tv", "for-you", "suggested", "recent", "franchises", "franchise-run", "ways-in", "top"]);
