/**
 * The section table behind the SectionBar (R9 S1): which section a pathname belongs to, the tabs
 * that section shows in the ONE content-top bar, and its search placeholder. Table-driven on purpose
 * — adding a tab is a row here, never a component. Tabs are routes (`path`) the bar links to; a tab
 * that doesn't apply to the signed-in user (`when`) is REMOVED, not disabled (the Long Box rule).
 *
 * The bar hides on the screening rooms (the movie/TV players, an arcade room, a watch-together
 * room): those are full-screen surfaces with their own Back.
 */
import { isKidAccount } from "../../Pages/Books/booksNav";

export interface SectionUser {
  isAdmin?: boolean;
  canEditMovies?: boolean;
  hasPassword?: boolean;
  booksAccess?: boolean;
  familyAlbum?: boolean;
  booksKidsStyle?: string | null;
  username?: string;
  [key: string]: unknown;
}

export interface SectionTab {
  key: string;
  label: string;
  path: string;
  /** Active only on the exact pathname (a section root whose siblings share the prefix). */
  exact?: boolean;
  /** Styled as the admin entry (the dashed accent tab). */
  admin?: boolean;
  /** Shown only when this returns true for the signed-in user (absent = always). */
  when?: (user: SectionUser | null | undefined) => boolean;
}

export interface SectionDef {
  key: string;
  /** Pathname prefixes that belong to the section; the last row (no prefixes) is the fallback. */
  prefixes: string[];
  title: string;
  searchPlaceholder: string;
  tabs: SectionTab[];
}

const admin = (u: SectionUser | null | undefined) => !!u?.isAdmin;
const editor = (u: SectionUser | null | undefined) => !!u?.isAdmin || !!u?.canEditMovies;
const booksMember = (u: SectionUser | null | undefined) => !!u?.booksAccess && !!u?.hasPassword;
const booksGrownUp = (u: SectionUser | null | undefined) => booksMember(u) && !isKidAccount(u as never);

export const SECTIONS: SectionDef[] = [
  {
    key: "tv", prefixes: ["/channels", "/tv", "/watch-together"], title: "TV", searchPlaceholder: "Search the guide — a show, a channel…",
    tabs: [
      { key: "guide", label: "Guide", path: "/channels", exact: true },
      { key: "admin", label: "Admin", path: "/channels/admin", admin: true, when: editor },
    ],
  },
  {
    key: "arcade", prefixes: ["/arcade"], title: "Arcade", searchPlaceholder: "system:PS2, genre:RPG, or a game…",
    tabs: [
      { key: "explore", label: "Explore", path: "/arcade/explore" },
      { key: "browse", label: "Browse", path: "/arcade", exact: true },
      { key: "admin", label: "Admin", path: "/arcade/admin", admin: true, when: admin },
    ],
  },
  {
    key: "boardgames", prefixes: ["/boardgames"], title: "Board Games", searchPlaceholder: "mechanic:Deck, designer:Knizia, or a game…",
    tabs: [
      { key: "browse", label: "Browse", path: "/boardgames", exact: true },
      { key: "admin", label: "Admin", path: "/boardgames/admin", admin: true, when: editor },
    ],
  },
  {
    key: "music", prefixes: ["/music"], title: "Music", searchPlaceholder: "artist:Bush, or an album…",
    tabs: [
      { key: "explore", label: "Explore", path: "/music/explore" },
      { key: "browse", label: "Browse", path: "/music", exact: true },
      { key: "playlists", label: "Playlists", path: "/music/playlists" },
      { key: "now", label: "Now playing", path: "/music/now-playing" },
      { key: "admin", label: "Admin", path: "/music/admin", admin: true, when: admin },
    ],
  },
  {
    key: "photos", prefixes: ["/photos"], title: "Photos", searchPlaceholder: "person:Grandma, album:Summer…",
    tabs: [
      { key: "timeline", label: "Timeline", path: "/photos", exact: true },
      { key: "browse", label: "Browse", path: "/photos/browse" },
      { key: "albums", label: "Albums", path: "/photos/albums" },
      { key: "gallery", label: "Gallery", path: "/photos/gallery" },
      { key: "people", label: "People", path: "/photos/people" },
      { key: "admin", label: "Admin", path: "/photos/admin", admin: true, when: admin },
    ],
  },
  {
    key: "books", prefixes: ["/books"], title: "Books", searchPlaceholder: "author:Miller, tag:Noir, series:Batman…",
    tabs: [
      { key: "explore", label: "Explore", path: "/books/explore", when: booksGrownUp },
      { key: "browse", label: "Browse", path: "/books", exact: true, when: booksGrownUp },
      { key: "shelf", label: "Shelf", path: "/books/shelf", when: booksMember },
      { key: "novels", label: "Novels", path: "/books/novels", when: booksGrownUp },
      { key: "kids", label: "Kids", path: "/books/kids", when: booksMember },
      { key: "admin", label: "Admin", path: "/books/admin", admin: true, when: (u) => booksMember(u) && admin(u) },
    ],
  },
  {
    key: "movies", prefixes: [], title: "Movie Theater", searchPlaceholder: "genre:Noir, person:Pacino, or a title…",
    tabs: [
      { key: "explore", label: "Explore", path: "/movies/explore" },
      { key: "browse", label: "Browse", path: "/", exact: true },
      { key: "channels", label: "Channels", path: "/channels", when: (u) => !!u?.hasPassword },
      { key: "admin", label: "Admin", path: "/movies/admin", admin: true, when: editor },
    ],
  },
];

/** The screening rooms: no bar. */
const HIDDEN = [/^\/watch\//, /^\/tv\//, /^\/arcade\/room\//, /^\/watch-together\//];

export function barHidden(pathname: string): boolean {
  return HIDDEN.some((re) => re.test(pathname));
}

export function sectionFor(pathname: string): SectionDef {
  const hit = SECTIONS.find((s) => s.prefixes.some((p) => pathname === p || pathname.startsWith(p + "/") || pathname.startsWith(p + "?")));
  return hit ?? SECTIONS[SECTIONS.length - 1];
}

export function tabsFor(section: SectionDef, user: SectionUser | null | undefined): SectionTab[] {
  return section.tabs.filter((t) => !t.when || t.when(user));
}

export function tabIsActive(tab: SectionTab, pathname: string): boolean {
  if (tab.exact) return pathname === tab.path || pathname === tab.path + "/";
  return pathname === tab.path || pathname.startsWith(tab.path + "/");
}
