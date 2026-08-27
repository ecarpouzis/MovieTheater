/**
 * Where the old operator routes went (R9 S6).
 *
 * Every section's tools used to own a route of their own; they are tabs of `/<section>/admin` now.
 * A link somebody kept — a bookmark, a note, the sider's gear menu, an old message — must still
 * land, so App (and PhotosPage, whose tools live inside its own Switch) renders one `<Redirect>`
 * per row below. The table is a MODULE rather than inline JSX so the mapping is a fact a test can
 * assert without mounting the whole app.
 */

/** Aliases rendered by App's top-level Switch. */
export const SITE_ADMIN_ALIASES = [
  { from: "/insert", to: "/movies/admin?tab=insert" },
  { from: "/batchinsert", to: "/movies/admin?tab=batch-insert" },
  { from: "/review-ingest", to: "/movies/admin?tab=review-ingest" },
  { from: "/boardgames/batchinsert", to: "/boardgames/admin?tab=batchinsert" },
];

/**
 * Aliases rendered inside PhotosPage's Switch — the album's tools need the section's live state
 * (the people list, the refresh beat), so its admin page is mounted there rather than in App.
 * `/photos/google` never had a route of its own (PhotoGoogle renders inside PhotoReview); it gets
 * one here because the Google reconciler is now a tab that can be linked to.
 */
export const PHOTOS_ADMIN_ALIASES = [
  { from: "/photos/tag", to: "/photos/admin?tab=tag" },
  { from: "/photos/dupes", to: "/photos/admin?tab=dupes" },
  { from: "/photos/review", to: "/photos/admin?tab=review" },
  { from: "/photos/google", to: "/photos/admin?tab=google" },
];

/** `/rate` is NOT an alias: it is a member surface (the sider's "Rate Movies" row) that the movie
 *  admin also offers as a tab. Anything added here must be an OPERATOR route. */
export const NOT_ALIASED = ["/rate"];
