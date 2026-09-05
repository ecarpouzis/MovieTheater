/**
 * The Novels rail's facets — the host's five (`/novels/facets`: authors, series, publishers, decades,
 * tags), plus the search, the rating floor and the "unknown" flag. Values are the host's own strings:
 * a decade is "1990s", a tag is the composite "category:value" the host hands back (and takes back
 * unchanged); the chip shows the value half, spaced and capitalised.
 *
 * Only tags can be EXCLUDED on the host (`excludeTag`); the other facets are include-only, so the
 * rail is told not to offer "−" on them. The default exclusion (`adult-romance`) is applied by the
 * page on first landing, never here — the URL is the state.
 */
import type { FacetOptionRow, FacetSpec } from "../../catalog/rail/facetSpec";
import { fetchNovelFacets } from "./booksApi";

export const NOVELS_RATING_PRESETS = [
  { value: 70, label: "70+" },
  { value: 80, label: "80+" },
  { value: 90, label: "90+" },
];

/** The default content exclusion the standalone shipped with — a bare tag value matches any category. */
export const NOVELS_DEFAULT_EXCLUDE_TAG = "adult-romance";

/** "genre:adult-romance" → "Adult romance"; "1990s" stays. */
export function novelTagLabel(value: string | number): string {
  const s = String(value);
  const i = s.indexOf(":");
  const v = (i >= 0 ? s.slice(i + 1) : s).replace(/-/g, " ").trim();
  return v ? v.charAt(0).toUpperCase() + v.slice(1) : s;
}

export function novelsFacetSpec(identity: string): FacetSpec {
  return {
    identity: `books:novels:${identity}`,
    noun: "books",
    text: true,
    rating: { presets: NOVELS_RATING_PRESETS },
    flags: [{ key: "unknown", token: "unknown", label: "No metadata yet", title: "Only the books with no insight row — the pile to fix up" }],
    facets: [
      { key: "authors", token: "author", label: "Authors", one: "Author", valueType: "string", excludable: false },
      { key: "series", token: "series", label: "Series", one: "Series", valueType: "string", excludable: false },
      { key: "tags", token: "tag", label: "Tags", one: "Tag", valueType: "string", labelOf: novelTagLabel },
      { key: "publishers", token: "publisher", label: "Publishers", one: "Publisher", valueType: "string", excludable: false },
      { key: "decades", token: "decade", label: "Decade", one: "Decade", valueType: "string", excludable: false },
    ],
    async loadFacets(signal) {
      const f = await fetchNovelFacets(signal);
      const plain = (rows: { value: string; count: number }[]): FacetOptionRow[] => rows.map((r) => ({ value: r.value, label: r.value, count: r.count }));
      return {
        authors: plain(f.authors),
        series: plain(f.series),
        publishers: plain(f.publishers),
        decades: plain(f.decades),
        tags: f.tags.map((t) => ({ value: t.value, label: novelTagLabel(t.value), count: t.count })),
      };
    },
  };
}
