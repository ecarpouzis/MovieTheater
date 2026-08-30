/**
 * The Books URLs the modals and rails jump to — one place, so a facet chip and a modal link build
 * the same browse URL. Facet links start a FRESH browse (the standalone's semantics: "browse this
 * series" is a new search, not an added chip).
 */
export function facetHref(token: string, value: string | number, extra: Record<string, string> = {}): string {
  const p = new URLSearchParams();
  p.append("f", `${token}:${value}`);
  for (const [k, v] of Object.entries(extra)) p.set(k, v);
  return `/books?${p.toString()}`;
}

/**
 * "Browse this series" for a BOOK. The comics browse filters series by ID; the Novels page's series
 * facet is by NAME (the host reads `BookDetail.SeriesName`), and a novel's home is /books/novels — so
 * a book series' browse link is not the same URL as a comic's, and sending it to `/books?f=series:<id>`
 * lands on the comics browse with nothing in it.
 */
export function novelsSeriesHref(name: string): string {
  return `/books/novels?${new URLSearchParams({ f: `series:${name}` }).toString()}`;
}

export function searchHref(q: string): string {
  return `/books?${new URLSearchParams({ q }).toString()}`;
}

export function directoryHref(folderId: number): string {
  return `/books?${new URLSearchParams({ view: "directory", dir: String(folderId) }).toString()}`;
}

export function readHref(itemId: number): string {
  return `/books/read/${itemId}`;
}
