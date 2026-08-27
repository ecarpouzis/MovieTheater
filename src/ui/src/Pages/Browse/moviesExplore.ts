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
 * | `recent` | `/API/Browse?sort=added` |
 * | `franchises` | `/API/BrowseGroups?groupBy=franchise` — GROUP cards, routed by facet |
 * | `franchise-run` | `/API/GetFranchiseRail` anchored on the spotlight title |
 *
 * The composer is PURE: it takes what the queries returned and answers the payload, so the rails and
 * their "More →" targets can be asserted without a network.
 */
import { exploreRail, exploreResponse, groupCard, humanizeKey } from "../../catalog/explore/composeExplore";
import { MovieAPI } from "../../MovieAPI";
import type { CardItem, ExploreResponse } from "../../catalog/types";
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
  franchiseRun?: FranchiseRailDto | null;
  lineup?: LineupChannel[] | null;
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
  "now-on-tv": "/channels",
  recent: "/?sort=added",
  random: "/?sort=random",
  franchises: "/?view=shelf&group=franchise",
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
  return { ...withCorner(base, `${pct}%`, "Where you left off"), subtitle: row.note || base.subtitle };
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

// ── The composition ────────────────────────────────────────────────────────────────────────────

export function composeMoviesExplore(input: MoviesExploreInput): ExploreResponse {
  const random = (input.random ?? []).map(toCard);
  const spotlight = random.slice(0, SPOTLIGHT_SIZE);
  const run = pickFranchiseRun(input.franchiseRun);

  return exploreResponse(spotlight, [
    exploreRail("continue", "Keep watching", "strip", (input.continueWatching ?? []).map(toContinueCard)),
    exploreRail("now-on-tv", "On TV right now", "strip", (input.lineup ?? []).map(toChannelCard), MOVIES_MORE["now-on-tv"]),
    exploreRail("for-you", "Picked for you", "strip", (input.recommendations ?? []).map(toRecommendationCard)),
    exploreRail("recent", "Just added to the library", "wall", (input.recent ?? []).map(toCard), MOVIES_MORE.recent),
    exploreRail("franchises", "Whole runs to binge", "strip", (input.franchiseGroups ?? []).map(toFranchiseCard), MOVIES_MORE.franchises),
    run
      ? exploreRail(
          "franchise-run",
          `The ${humanizeKey(run.value)} run, in order`,
          "strip",
          run.items.map(toFranchiseRunCard),
          moviesFacetHref("franchise", run.value),
        )
      : null,
    exploreRail("random", "Something else entirely", "grid", random.slice(SPOTLIGHT_SIZE), MOVIES_MORE.random),
  ], input.seed);
}

/** The rail the endpoint itself calls most specific (fewest members), else the first it returned. */
export function pickFranchiseRun(dto: FranchiseRailDto | null | undefined): { value: string; items: FranchiseRailItem[] } | null {
  const list = dto?.franchises ?? [];
  if (list.length === 0) return null;
  const pick = list.find((f) => f.value === dto?.defaultFranchise) ?? list[0];
  return pick?.items?.length ? { value: pick.value, items: pick.items } : null;
}

/** Rails whose point is that they are CURRENT — a shuffle would be a lie. */
export const MOVIES_UNSEEDED_RAILS: ReadonlySet<string> = new Set(["continue", "now-on-tv", "for-you", "recent", "franchises", "franchise-run"]);
