/**
 * What survives of the standalone's `dataTransform.ts` now that the host resolves title, series,
 * publisher, date, creators and tags server-side: FORMATTING only. The date label, the run label, the
 * aspect clamp, the star scale, the synopsis pick by the leg the server named, and an HTML stripper.
 */
import type { DatePrecision, ItemDetail, ItemSummary, SynopsisSource } from "./booksApi";

/** "1987.02" at Month/Day precision, "1987" at Year, "" when unknown — the standalone's label. */
export function dateLabel(year: number | null | undefined, month: number | null | undefined, precision: DatePrecision | undefined): string {
  if (!year) return "";
  const showMonth = (precision === "Month" || precision === "Day") && month != null && month >= 1 && month <= 12;
  return showMonth ? `${year}.${String(month).padStart(2, "0")}` : String(year);
}

/** LOCG-style run label: "1987 – 2001", "2001 – Present", "1993", or null. */
export function runLabel(start: number | null | undefined, end: number | null | undefined, ongoing?: boolean): string | null {
  if (!start) return null;
  const tail = ongoing ? "Present" : end && end !== start ? String(end) : null;
  return tail ? `${start} – ${tail}` : String(start);
}

export const ASPECT_MIN = 0.35;
export const ASPECT_MAX = 1.6;
export const ASPECT_DEFAULT = 0.66;

export function clampAspect(aspect: number | null | undefined): number {
  if (aspect == null || !Number.isFinite(aspect) || aspect <= 0) return ASPECT_DEFAULT;
  return Math.min(ASPECT_MAX, Math.max(ASPECT_MIN, aspect));
}

/** 0–100 → 0–5 stars (one decimal). */
export function starsFromRating(rating: number | null | undefined): number {
  if (rating == null) return 0;
  return Math.round(Math.max(0, Math.min(100, rating)) / 2) / 10;
}

/** DOMParser-based HTML → prose (the standalone's `stripHtml`), with an entity-only fast path. */
export function stripHtml(html: string | null | undefined): string {
  if (!html) return "";
  if (!/[<&]/.test(html)) return html.trim();
  if (typeof DOMParser === "undefined") return html.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim();
  const doc = new DOMParser().parseFromString(html, "text/html");
  return (doc.body.textContent ?? "").replace(/\s+/g, " ").trim();
}

const LEG_LABEL: Record<SynopsisSource, string> = {
  None: "",
  Cv: "ComicVine",
  Embedded: "the file's own metadata",
  Locg: "League of Comic Geeks",
  External: "Open Library",
  Mu: "MangaUpdates",
  CvDeck: "ComicVine (deck)",
  AI: "AI",
};

/** The synopsis the resolver chose, read off the leg it named, plus the provenance label to print beside it. */
export function synopsisFor(detail: ItemDetail): { text: string; source: SynopsisSource; label: string } {
  const s = detail.summary.synopsisSource;
  const pick = (): string | null | undefined => {
    switch (s) {
      case "Cv": return detail.cvIssue?.description ?? detail.cvVolume?.description;
      case "Embedded": return detail.embedded?.summary;
      case "Locg": return detail.locg?.description;
      case "External": return detail.external?.description;
      case "Mu": return detail.mu?.description;
      case "CvDeck": return detail.cvIssue?.deck ?? detail.cvVolume?.deck;
      case "AI": return detail.insight?.synopsis ?? detail.seriesInsight?.synopsis;
      default: return null;
    }
  };
  const text = stripHtml(pick() ?? detail.book?.description ?? "");
  return { text, source: s, label: LEG_LABEL[s] ?? "" };
}

/** The series-level description in the standalone's order — CV volume → MangaUpdates → external → the AI insight. */
export function seriesSynopsisFor(detail: ItemDetail | null | undefined, aiFallback?: string | null): { text: string; isAi: boolean } {
  if (detail) {
    for (const [text, isAi] of [
      [detail.cvVolume?.description, false],
      [detail.mu?.description, false],
      [detail.external?.description, false],
      [detail.seriesInsight?.synopsis, true],
      [detail.cvVolume?.deck, false],
    ] as [string | null | undefined, boolean][]) {
      const clean = stripHtml(text ?? "");
      if (clean.length >= 40 || (isAi && clean.length > 0)) return { text: clean, isAi };
    }
  }
  return { text: stripHtml(aiFallback ?? ""), isAi: true };
}

export function fileLabel(summary: ItemSummary): string {
  return summary.fileName + (summary.extension ?? "");
}

export function plural(n: number, one: string, many = `${one}s`): string {
  return `${n.toLocaleString()} ${n === 1 ? one : many}`;
}
