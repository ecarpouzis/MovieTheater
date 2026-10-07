// The parts of the site a request can be for (2026-10-06). Keys are the server's
// ContentRequestSections values AND the SPA's section keys, so a row can link straight to where the
// content would land. `detailLabel` is the one disambiguating field the compose form asks for in that
// section — the thing a future acquisition job keys off alongside the title.
export const REQUEST_SECTIONS = [
  { key: "movies", label: "Movies & TV", short: "Movies", hue: "#4A90E2", path: "/", detailLabel: "Director, or anything that pins it down", detailHint: "e.g. the 1982 one, not the remake", titlePlaceholder: "Movie or show title" },
  { key: "music", label: "Music", short: "Music", hue: "#C9484F", path: "/music", detailLabel: "Artist", detailHint: "Leave the title as the artist's name to ask for everything they've done", titlePlaceholder: "Album title (or an artist)" },
  { key: "boardgames", label: "Board Games", short: "Board Games", hue: "#2E9E63", path: "/boardgames", detailLabel: "Designer or publisher", detailHint: "Helps when two games share a name", titlePlaceholder: "Game title" },
  { key: "arcade", label: "Arcade", short: "Arcade", hue: "#9A7BD4", path: "/arcade", detailLabel: "System or platform", detailHint: "SNES, PS2, arcade, DOS…", titlePlaceholder: "Game title" },
  { key: "books", label: "Books & Comics", short: "Books", hue: "#D98936", path: "/books", detailLabel: "Author or publisher", detailHint: "Matching is by hand for books — the library lives on its own host", titlePlaceholder: "Book, series or run" },
];

export const SECTION_BY_KEY = Object.fromEntries(REQUEST_SECTIONS.map((s) => [s.key, s]));

export function sectionOf(key) {
  return SECTION_BY_KEY[key] ?? { key, label: key, short: key, hue: "#888", path: "/", detailLabel: "Detail" };
}

export const STATUS_LABEL = { open: "Open", fulfilled: "Added", declined: "Declined", withdrawn: "Withdrawn" };

/** Where a matched / fulfilled catalog row opens — the sections' URL-driven detail modals. */
export function catalogHref(kind, id) {
  switch (kind) {
    case "movie": return `/?title=movie:${id}`;
    case "series": return `/?title=series:${id}`;
    case "album": return `/music?album=${id}`;
    case "artist": return "/music";
    case "boardgame": return `/boardgames?game=${id}`;
    case "arcade": return `/arcade?game=${id}`;
    default: return null;
  }
}

/** "3 min ago" / "2 d ago" / "Mar 4" — the queue's dates are about recency, not exactness. */
export function ago(iso, nowMs = Date.now()) {
  if (!iso) return "";
  const t = new Date(iso).getTime();
  if (!Number.isFinite(t)) return "";
  const s = Math.max(0, Math.round((nowMs - t) / 1000));
  if (s < 60) return "just now";
  const m = Math.round(s / 60);
  if (m < 60) return `${m} min ago`;
  const h = Math.round(m / 60);
  if (h < 36) return `${h} h ago`;
  const d = Math.round(h / 24);
  if (d < 14) return `${d} d ago`;
  const date = new Date(t);
  const sameYear = date.getFullYear() === new Date(nowMs).getFullYear();
  return date.toLocaleDateString(undefined, sameYear ? { month: "short", day: "numeric" } : { month: "short", day: "numeric", year: "numeric" });
}
