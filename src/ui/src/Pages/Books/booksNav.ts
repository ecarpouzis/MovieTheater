/**
 * The Books section's index: one table of views read by the navbar rail (which links them) and the
 * page (which routes them), so a view cannot exist in one and not the other — the photos convention.
 * Kid accounts (a maturity ceiling of 0 and not an admin) see Kids and Shelf only and are pinned to them.
 */

export interface BooksMe {
  booksAccess?: boolean;
  booksMaturityCeiling?: number | null;
  booksKidsStyle?: string | null;
  booksHostBaseUrl?: string | null;
  isAdmin?: boolean;
  hasPassword?: boolean;
  username?: string | null;
}

export interface BooksView { key: string; label: string; path: string }

export const BOOKS_VIEWS: BooksView[] = [
  { key: "explore", label: "Explore", path: "/books/explore" },
  { key: "browse", label: "Browse", path: "/books" },
  { key: "novels", label: "Novels", path: "/books/novels" },
  { key: "kids", label: "Kids", path: "/books/kids" },
  { key: "shelf", label: "Shelf", path: "/books/shelf" },
  { key: "admin", label: "Admin", path: "/books/admin" },
];

const VIEW_BY_KEY = Object.fromEntries(BOOKS_VIEWS.map((v) => [v.key, v])) as Record<string, BooksView>;

/** Which view a /books URL is on. Unrecognised → browse (the section root). The reader is its own thing. */
export function booksSection(pathname: string): string {
  const rest = String(pathname || "").replace(/^\/books\/?/, "").split(/[/?#]/)[0];
  if (rest === "read") return "read";
  return VIEW_BY_KEY[rest] ? rest : "browse";
}

export function booksViewLabel(key: string): string {
  return VIEW_BY_KEY[key]?.label ?? "Books";
}

export function isKidAccount(me: BooksMe | null | undefined): boolean {
  return !!me && me.booksMaturityCeiling === 0 && !me.isAdmin;
}

/** Where a kid account may be: Kids, the shelf, and reading. Everything else redirects to Kids. */
export function kidAllowedPath(pathname: string): boolean {
  const s = booksSection(pathname);
  return s === "kids" || s === "shelf" || s === "read";
}

export interface BooksIndexCounts {
  catalog?: number | null;
  novels?: number | null;
  continueReading?: number | null;
}

export interface IndexView extends BooksView { count?: number | null; waiting?: boolean }
export interface IndexGroup { key: string; label?: string; views: IndexView[] }

/** The rail's grouped index, gated the way the pages are. */
export function booksNavGroups(me: BooksMe | null | undefined, counts: BooksIndexCounts = {}): IndexGroup[] {
  if (!me?.booksAccess) return [];
  if (isKidAccount(me)) {
    return [{ key: "library", views: [{ ...VIEW_BY_KEY.kids }, { ...VIEW_BY_KEY.shelf, count: counts.continueReading ?? null, waiting: (counts.continueReading ?? 0) > 0 }] }];
  }
  const groups: IndexGroup[] = [
    {
      key: "library",
      label: "The library",
      views: [
        { ...VIEW_BY_KEY.explore },
        { ...VIEW_BY_KEY.browse, count: counts.catalog ?? null },
        { ...VIEW_BY_KEY.novels, count: counts.novels ?? null },
        { ...VIEW_BY_KEY.kids },
      ],
    },
    {
      key: "yours",
      label: "Yours",
      views: [{ ...VIEW_BY_KEY.shelf, count: counts.continueReading ?? null, waiting: (counts.continueReading ?? 0) > 0 }],
    },
  ];
  if (me.isAdmin) groups.push({ key: "operate", label: "Operate", views: [{ ...VIEW_BY_KEY.admin }] });
  return groups;
}
