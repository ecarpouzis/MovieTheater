/**
 * The Books browse's facets — the standalone's eight, in its rail order, plus the search, the year
 * range, the rating floor and the two personal flags. Values: series and collections key by ID (v2's
 * stable keys), publishers by NAME (the projection carries no publisher id, and the group keys are
 * names too), everything else by its display value. The loaders read `/browse/facets` and the paged
 * `/browse/facet-options` for the three long tails.
 */
import { fetchFacetOptions, fetchFacets } from "./booksApi";
import { folderIconUrl } from "./booksMedia";
import type { FacetOptionRow, FacetSpec } from "../../catalog/rail/facetSpec";

export const RATING_PRESETS = [
  { value: 60, label: "3★+" },
  { value: 70, label: "3.5★+" },
  { value: 80, label: "4★+" },
  { value: 90, label: "4.5★+" },
];

export function booksFacetSpec(identity: string): FacetSpec {
  return {
    identity: `books:comic:${identity}`,
    text: true,
    years: { decadesKey: "decades" },
    rating: { presets: RATING_PRESETS },
    flags: [
      { key: "read", token: "read", label: "Read", title: "Only what you have finished (and the series you marked read)", appliesTo: "groups" },
      { key: "want", token: "want", label: "Want to read", title: "Only your queue", appliesTo: "groups" },
    ],
    facets: [
      { key: "collections", token: "collection", label: "Collections", one: "Collection", valueType: "number", render: "tile", defaultOpen: true },
      { key: "series", token: "series", label: "Series", one: "Series", valueType: "number" },
      { key: "tags", token: "tag", label: "Tags", one: "Tag", valueType: "string", dynamic: true },
      { key: "authors", token: "author", label: "Writers", one: "Writer", valueType: "string", dynamic: true },
      { key: "artists", token: "artist", label: "Artists", one: "Artist", valueType: "string", dynamic: true },
      { key: "events", token: "event", label: "Events", one: "Event", valueType: "string" },
      { key: "franchises", token: "franchise", label: "Franchises", one: "Franchise", valueType: "string" },
      { key: "publishers", token: "publisher", label: "Publishers", one: "Publisher", valueType: "string", render: "swatch" },
    ],
    async loadFacets(signal) {
      const f = await fetchFacets("comic", signal);
      const plain = (rows: { value: string; count: number }[]): FacetOptionRow[] => rows.map((r) => ({ value: r.value, label: r.value, count: r.count }));
      return {
        collections: f.collections.map((c) => ({ value: c.id, label: c.name, count: c.count, imageUrl: folderIconUrl(c.id) })),
        series: f.series.map((s) => ({ value: s.id, label: s.value, count: s.count })),
        tags: plain(f.tags),
        authors: plain(f.authors),
        artists: plain(f.artists),
        events: plain(f.events),
        franchises: plain(f.franchises),
        publishers: f.publishers.map((p) => ({ value: p.name, label: p.full ?? p.name, count: p.count })),
        decades: plain(f.decades),
      };
    },
    async loadOptions(key, q, skip, top, signal) {
      const field = key === "authors" ? "authors" : key === "artists" ? "artists" : "tags";
      const r = await fetchFacetOptions(field, q, skip, top, "comic", signal);
      return { items: r.items.map((o) => ({ value: o.value, label: o.value, count: o.count })), total: r.total };
    },
  };
}
