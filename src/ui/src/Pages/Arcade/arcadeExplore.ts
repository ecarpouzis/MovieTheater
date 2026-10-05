/**
 * The Arcade Explore composition (R9 S7) — the lobby's two strips plus what the section already
 * knows, as one landing. Nothing new is asked of the API.
 *
 * | Rail | Where it comes from |
 * |---|---|
 * | `recent` "Recently played" | `/API/Arcade/RecentlyPlayed` — **MOVED here from the lobby**, which keeps the console carousel and the grid |
 * | `live` "Live rooms" | `/API/Arcade/Rooms` — a card joins the room rather than opening the game |
 * | `trophies` | `/API/Arcade/Trophies/Mine` — the games you last unlocked something in |
 * | `systems` | `/API/Arcade/Filters` — GROUP cards, routed by `f=system:<value>` |
 * | `top` (ranked ten) + spotlight | `/API/Arcade/Games?sort=rating` — the ten, then a seeded handful of the next thirty |
 * | `ways-in` | `/API/Arcade/GameGroups?groupBy=system|genre|players&perGroupTop=3` — the active tab only |
 * | `spin` | one system picked by the seed, its best games — a FOCUS plate on the console |
 * | `quick` | three columns: couch co-op (`maxPlayers=2`), pocket-sized (the handhelds), coin-op (`system=arcade`) |
 */
import { exploreColumn, exploreColumns, exploreDoors, exploreFocus, exploreRail, exploreResponse, groupCard, seededShuffle, distinctCovers } from "../../catalog/explore/composeExplore";
import { facetHref } from "../../catalog/rail/facetUrl";
import type { CardItem, ExploreDoor, ExploreResponse } from "../../catalog/types";
import type { HeroDetail } from "../../catalog/explore/HeroSpotlight";
import { ARCADE_ASPECT, coverUrl, toArcadeCard, type ArcadeGameRow } from "../../catalog/sources/arcadeSource";
import { hueOf } from "../../catalog/sources/hue";
import { systemLabel } from "./arcadeSystems";

export interface RecentlyPlayedRow {
  game: ArcadeGameRow;
  lastPlayedUtc?: string;
  saveCount?: number;
  /** The ROM row the player's save belongs to — the version the modal must open on. */
  playedVersionId?: number;
}
export interface LiveRoomRow {
  roomCode: string;
  game: { id: number; title: string; system?: string | null };
  players?: string[];
  maxPlayers?: number;
  seatsFree?: number;
  spectators?: string[];
  host?: string | null;
  starting?: boolean;
}
export interface TrophyGameRow { gameId: number; title: string; system?: string | null; earnedCount?: number; points?: number; lastUnlockedUtc?: string }
export interface SystemFacetRow { value: string; count: number }

export interface ArcadeExploreInput {
  recent?: RecentlyPlayedRow[];
  rooms?: LiveRoomRow[];
  trophies?: TrophyGameRow[];
  systems?: SystemFacetRow[];
  top?: ArcadeGameRow[];
  spin?: { system: string; games: ArcadeGameRow[] } | null;
  /** "Ways in": each axis's groups as they load (`/API/Arcade/GameGroups`). */
  doors?: Partial<Record<ArcadeDoorAxis, ArcadeGroupRow[]>>;
  doorAxis?: ArcadeDoorAxis;
  /** The quick-pick columns, each a page of the best-rated in its slice (shuffled by the seed). */
  coop?: ArcadeGameRow[];
  pocket?: ArcadeGameRow[];
  coinop?: ArcadeGameRow[];
  seed?: number;
}

export interface ArcadeGroupRow { key: string; label?: string; totalItems: number; items?: ArcadeGameRow[] }

export const ARCADE_DOOR_AXES = [
  { key: "system", label: "Console" },
  { key: "genre", label: "Genre" },
  { key: "players", label: "Players" },
] as const;
export type ArcadeDoorAxis = (typeof ARCADE_DOOR_AXES)[number]["key"];

/** The handhelds the "Pocket-sized" column draws from (`system=` takes a comma list). */
export const POCKET_SYSTEMS = "gb,gbc,gba,gg,nds,psp,ngpc,lynx,wsc";
const DOORS_TAKE = 18;
const DOOR_MIN = 5;

/** Where a door leads: the lobby with exactly that facet (the console carousel IS the system facet). */
export function arcadeDoorHref(axis: string, key: string): string {
  return axis === "system" ? arcadeSystemHref(key) : facetHref("/arcade", [[axis, key]]);
}

export function arcadeDoors(axis: string, rows: readonly ArcadeGroupRow[]): ExploreDoor[] {
  const list = rows.filter((g) => g.key && (g.totalItems ?? 0) >= DOOR_MIN);
  // Players is a ladder (1, 2, 3-4…) — keep the server's order; everything else biggest first.
  const ordered = (axis === "players" ? list : list.slice().sort((a, b) => b.totalItems - a.totalItems)).slice(0, DOORS_TAKE);
  const covers = distinctCovers(ordered, (g) => (g.items ?? []).map(toArcadeCard).map((c) => ({ src: c.imageThumbUrl ?? c.imageUrl, hue: c.hue })));
  return ordered.map((g, i) => ({
    key: g.key,
    label: axis === "system" ? systemLabel(g.key) : (g.label || g.key),
    count: g.totalItems,
    href: arcadeDoorHref(axis, g.key),
    covers: covers[i],
  }));
}

/** A game's own row as the marquee's lines: its blurb, the console and the maker, its genres. */
export function arcadeHeroDetail(item: CardItem): HeroDetail | null {
  const g = (item.raw ?? null) as (ArcadeGameRow & { summary?: string | null; developer?: string | null; genres?: string | null }) | null;
  if (!g || typeof g !== "object") return null;
  // The LaunchBox summaries carry marketing bullet lists after the prose — the first paragraph is the blurb.
  const synopsis = (g.summary ?? "").split(/\n\s*\n|\n\*/)[0].trim() || null;
  const meta = [g.system ? systemLabel(g.system) : null, g.year ? String(g.year) : null, g.developer || null,
    g.maxPlayers && g.maxPlayers > 1 ? `Up to ${g.maxPlayers} players` : null].filter((x): x is string => !!x);
  const tags = (g.genres ?? "").split(/[;,]/).map((t) => t.trim()).filter(Boolean);
  return { synopsis, meta, tags, subtitle: null };
}

export const ARCADE_SPOTLIGHT_SIZE = 5;
const TROPHIES_TAKE = 12;

export const ARCADE_MORE = {
  live: "/arcade",
  systems: "/arcade",
  top: "/arcade?sort=rating",
  coop: "/arcade?f=players%3A2&sort=rating",
  pocket: "/arcade?sort=rating",
};

/** `/arcade?f=system:ps2` — the console carousel IS this facet, so the link lands exactly on it. */
export function arcadeSystemHref(system: string): string {
  return facetHref("/arcade", [["system", system]]);
}

/** Coarse relative time — a shelf only needs "roughly how long ago" (the lobby strip's own rule). */
export function timeAgo(iso: string | null | undefined, nowMs = Date.now()): string {
  if (!iso) return "";
  const t = new Date(iso).getTime();
  if (!Number.isFinite(t)) return "";
  const min = Math.round((nowMs - t) / 60000);
  if (min < 1) return "just now";
  if (min < 60) return `${min}m ago`;
  const hr = Math.round(min / 60);
  if (hr < 24) return `${hr}h ago`;
  const day = Math.round(hr / 24);
  if (day < 30) return `${day}d ago`;
  return new Date(iso).toLocaleDateString();
}

/**
 * A recently-played tile. Saves are keyed on the ROM ROW, so the card's id is `playedVersionId` —
 * that is what rides into `/arcade?game=` and what makes Start resume the save the tile advertises.
 * (The lobby strip carried the same rule; this is where it lives now.)
 */
export function toRecentCard(row: RecentlyPlayedRow, nowMs?: number): CardItem | null {
  if (!row?.game?.key) return null;
  const base = toExploreGameCard(row.game);
  const id = row.playedVersionId ?? base.id;
  return { ...base, id, key: `game:${id}:recent`, label: timeAgo(row.lastPlayedUtc, nowMs) || base.label, raw: row.game };
}

/** A live room. `raw.roomCode` is what the page reads to JOIN instead of opening the game. */
export function toRoomCard(room: LiveRoomRow): CardItem | null {
  if (!room?.roomCode || !room.game) return null;
  const players = room.players?.length ?? 0;
  const seats = room.maxPlayers ?? 0;
  const hue = hueOf(room.game.title || room.roomCode);
  return {
    kind: "game",
    id: room.game.id,
    key: `room:${room.roomCode}`,
    title: room.game.title,
    subtitle: room.game.system ? systemLabel(room.game.system) : undefined,
    label: room.starting ? "starting…" : `${players}/${seats || players} playing`,
    aspect: ARCADE_ASPECT,
    imageUrl: `/ArcadeImage/${room.game.id}`,
    hue,
    badges: [{ label: "LIVE", tone: "live" as const, title: `${room.host ?? "someone"} hosting` }],
    raw: room,
  };
}

/** A game you have unlocked something in. The trophy row carries no art row — the game id IS the art id. */
export function toTrophyCard(t: TrophyGameRow): CardItem | null {
  if (!t?.gameId) return null;
  const hue = hueOf(t.title || String(t.gameId));
  return {
    kind: "game",
    id: t.gameId,
    key: `game:${t.gameId}:trophy`,
    title: t.title,
    subtitle: t.system ? systemLabel(t.system) : undefined,
    label: timeAgo(t.lastUnlockedUtc),
    aspect: ARCADE_ASPECT,
    imageUrl: `/ArcadeImage/${t.gameId}`,
    hue,
    badges: [{ label: `🏆 ${t.earnedCount ?? 0}`, tone: "system" as const, title: `${t.points ?? 0} points` }],
    raw: t,
  };
}

/** A console as a GROUP card. Its face is the best-rated game the top page happens to hold for it. */
export function toSystemCard(row: SystemFacetRow, faces: ReadonlyMap<string, ArcadeGameRow>): CardItem | null {
  if (!row?.value) return null;
  const face = faces.get(row.value);
  return groupCard({
    kind: "system",
    key: row.value,
    title: systemLabel(row.value),
    count: row.count,
    imageUrl: face ? coverUrl(face) ?? undefined : undefined,
    aspect: ARCADE_ASPECT,
    raw: row,
  });
}

/** The system the seed lands on — deterministic, so Back walks the spins. */
export function pickSpinSystem(systems: readonly SystemFacetRow[] | undefined, seed: number): string | null {
  const list = (systems ?? []).filter((s) => s.value && s.count > 0);
  if (list.length === 0) return null;
  return list[Math.abs(seed || 1) % list.length].value;
}

/** A lobby card with its console NAMED ("Super Nintendo", not "snes") — Explore is read, not scanned. */
export function toExploreGameCard(row: ArcadeGameRow): CardItem {
  const card = toArcadeCard(row);
  return row.system ? { ...card, subtitle: systemLabel(row.system) } : card;
}

/** Six of a column's page, shuffled by the seed so every roll shows a different handful of the best. */
function columnPick(rows: readonly ArcadeGameRow[] | undefined, seed: number, salt: number): CardItem[] {
  return seededShuffle(rows ?? [], seed + salt).slice(0, 6).map(toExploreGameCard);
}

export function composeArcadeExplore(input: ArcadeExploreInput, nowMs?: number): ExploreResponse {
  const top = input.top ?? [];
  const seed = input.seed ?? 1;
  // The ten are the ranked module; the marquee rotates a seeded handful of the NEXT best, so the two
  // never show the same game and every Shuffle brings a new feature.
  const pool = top.length >= 10 + ARCADE_SPOTLIGHT_SIZE ? top.slice(10) : top;
  const spotlight = seededShuffle(pool, seed).slice(0, ARCADE_SPOTLIGHT_SIZE).map(toExploreGameCard);
  const spin = input.spin;
  const spinCount = spin ? input.systems?.find((x) => x.value === spin.system)?.count : undefined;
  const doorAxes = ARCADE_DOOR_AXES.map((a) => {
    const rows = input.doors?.[a.key];
    return { key: a.key, label: a.label, doors: rows ? arcadeDoors(a.key, rows) : undefined };
  });

  return exploreResponse(spotlight, [
    exploreRail("recent", "Recently played", "strip", (input.recent ?? []).map((r) => toRecentCard(r, nowMs))),
    exploreRail("live", "Live rooms", "strip", (input.rooms ?? []).map(toRoomCard), ARCADE_MORE.live),
    exploreDoors("ways-in", "Ways in", doorAxes, input.doorAxis),
    exploreRail("top", "The ten best-rated", "ranked", top.slice(0, 10).map(toExploreGameCard), ARCADE_MORE.top),
    spin
      ? exploreFocus(
          "spin",
          "Spin the shelf",
          { name: systemLabel(spin.system), count: spinCount, blurb: "One console, chosen by the roll. Its best-rated games first.", href: arcadeSystemHref(spin.system) },
          spin.games.map(toExploreGameCard),
        )
      : null,
    exploreColumns("quick", "Quick picks", [
      exploreColumn("coop", "Couch co-op", columnPick(input.coop, seed, 3), ARCADE_MORE.coop),
      exploreColumn("pocket", "Pocket-sized", columnPick(input.pocket, seed, 5), ARCADE_MORE.pocket),
      exploreColumn("coinop", "Coin-op classics", columnPick(input.coinop, seed, 7), arcadeSystemHref("arcade")),
    ]),
    exploreRail("trophies", "Where you last earned something", "strip", (input.trophies ?? []).slice(0, TROPHIES_TAKE).map(toTrophyCard)),
  ], input.seed);
}

/** Everything here reports a CURRENT (or fixed) fact; only the spin and the quick picks re-roll. */
export const ARCADE_UNSEEDED_RAILS: ReadonlySet<string> = new Set(["recent", "live", "trophies", "systems", "top", "ways-in"]);
