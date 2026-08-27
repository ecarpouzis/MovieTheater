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
 * | spotlight + `top` | `/API/Arcade/Games?sort=rating` |
 * | `spin` | one system picked by the seed, its best games — "random system", as a shelf |
 */
import { exploreRail, exploreResponse, groupCard } from "../../catalog/explore/composeExplore";
import { facetHref } from "../../catalog/rail/facetUrl";
import type { CardItem, ExploreResponse } from "../../catalog/types";
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
  seed?: number;
}

export const ARCADE_SPOTLIGHT_SIZE = 5;
const TROPHIES_TAKE = 12;
const SYSTEMS_TAKE = 18;

export const ARCADE_MORE = {
  live: "/arcade",
  systems: "/arcade",
  top: "/arcade?sort=rating",
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
  const base = toArcadeCard(row.game);
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

export function composeArcadeExplore(input: ArcadeExploreInput, nowMs?: number): ExploreResponse {
  const top = input.top ?? [];
  const faces = new Map<string, ArcadeGameRow>();
  for (const g of top) if (g.system && !faces.has(g.system)) faces.set(g.system, g);
  const spotlight = top.slice(0, ARCADE_SPOTLIGHT_SIZE).map(toArcadeCard);
  const spin = input.spin;

  return exploreResponse(spotlight, [
    exploreRail("recent", "Recently played", "strip", (input.recent ?? []).map((r) => toRecentCard(r, nowMs))),
    exploreRail("live", "Live rooms", "strip", (input.rooms ?? []).map(toRoomCard), ARCADE_MORE.live),
    exploreRail("trophies", "Where you last earned something", "strip", (input.trophies ?? []).slice(0, TROPHIES_TAKE).map(toTrophyCard)),
    exploreRail("systems", "Pick a console", "strip", (input.systems ?? []).slice(0, SYSTEMS_TAKE).map((s) => toSystemCard(s, faces)), ARCADE_MORE.systems),
    exploreRail("top", "Best on the shelf", "grid", top.slice(ARCADE_SPOTLIGHT_SIZE).map(toArcadeCard), ARCADE_MORE.top),
    spin
      ? exploreRail("spin", `Spin the shelf: ${systemLabel(spin.system)}`, "grid", spin.games.map(toArcadeCard), arcadeSystemHref(spin.system))
      : null,
  ], input.seed);
}

/** Everything here reports a CURRENT fact; only the seeded spin re-rolls. */
export const ARCADE_UNSEEDED_RAILS: ReadonlySet<string> = new Set(["recent", "live", "trophies", "systems", "top"]);
