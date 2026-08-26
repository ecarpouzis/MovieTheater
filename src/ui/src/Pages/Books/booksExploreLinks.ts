/**
 * The host's Explore rails say where "more" leads in ITS vocabulary (a browse URL relative to the
 * Books API root). This maps those onto the section's own URLs — the Books URL contract (`r=`, `sort=`,
 * `view=`/`group=`) — and answers null for a rail it cannot honour, so no link ever leads somewhere
 * quietly different from what the rail showed.
 */
export function exploreMoreHref(href: string): string | null {
  if (!href) return null;
  const qIdx = href.indexOf("?");
  const path = qIdx >= 0 ? href.slice(0, qIdx) : href;
  const q = new URLSearchParams(qIdx >= 0 ? href.slice(qIdx + 1) : "");

  if (path.startsWith("/browse/groups")) {
    const groupBy = q.get("groupBy") || "series";
    return `/books?${new URLSearchParams({ view: "shelf", group: groupBy }).toString()}`;
  }
  if (path === "/suggestions") return "/books/shelf?tab=suggested";

  const kids = /^\/kids\/series\/(\d+)\/items/.exec(path);
  if (kids) return `/books/kids?${new URLSearchParams({ series: kids[1] }).toString()}`;

  if (path === "/odata/catalog") {
    const kind = q.get("kind") === "book" ? "book" : "comic";
    const orderby = (q.get("$orderby") ?? "").toLowerCase();
    const filter = q.get("$filter") ?? "";
    const base = kind === "book" ? "/books/novels" : "/books";
    if (orderby.startsWith("rating desc")) {
      const min = /rating ge (\d+)/i.exec(filter)?.[1];
      const p = new URLSearchParams();
      if (min) p.set("r", min);
      p.set("sort", "rating");
      return `${base}?${p.toString()}`;
    }
    if (orderby.startsWith("indexedat desc")) {
      return kind === "book" ? `${base}?sort=newest` : `${base}?sort=relevance`;
    }
    return null;
  }
  return null;
}
