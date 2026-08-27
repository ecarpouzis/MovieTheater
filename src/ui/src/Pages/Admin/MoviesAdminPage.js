import { lazy } from "react";
import AdminShell from "../../admin/AdminShell";

// `/movies/admin?tab=` — the movie section's operator tools on the site's admin shell (R9 S6).
// FIRST PASS WRAPS today's pages: the ingest review queue, the two insert pages, the user
// administration that used to be a modal behind the sider's gear, and the Rate page. None of their
// internals changed; what changed is that they are TABS with a URL instead of scattered routes.
//
// The old routes keep working as redirects (App.js): /review-ingest, /insert, /batchinsert. `/rate`
// deliberately does NOT redirect — it is a member surface (the sider's "Rate Movies" row), and the
// tab here is the same page for an operator who is already in these tools.
const IngestReviewPage = lazy(() => import("../IngestReview/IngestReviewPage"));
const InsertPage = lazy(() => import("../InsertPage"));
const BatchInsertPage = lazy(() => import("../BatchInsertPage"));
const RatePage = lazy(() => import("../Rate/RatePage"));
const MoviesUsersTab = lazy(() => import("./MoviesUsersTab"));
const MoviesOverviewTab = lazy(() => import("./MoviesOverviewTab"));

export function moviesAdminTabs({ userData, setUserData }) {
  const isAdmin = !!userData?.isAdmin;
  return [
    { key: "overview", label: "Overview", render: () => <MoviesOverviewTab isAdmin={isAdmin} /> },
    { key: "review-ingest", label: "Review ingest", render: () => <IngestReviewPage userData={userData} /> },
    { key: "insert", label: "Insert", render: () => <InsertPage /> },
    { key: "batch-insert", label: "Batch insert", render: () => <BatchInsertPage /> },
    // Users is the one ADMIN-only tab here: an editor may fix the library, not hand out passwords.
    { key: "users", label: "Users", render: () => <MoviesUsersTab />, when: isAdmin },
    { key: "rate", label: "Rate", render: () => <RatePage userData={userData} setUserData={setUserData} /> },
  ];
}

export default function MoviesAdminPage({ userData, setUserData }) {
  // The bar shows this section's Admin tab to an editor OR an admin; the route re-checks the same
  // thing as a courtesy. Every endpoint behind these tabs is gated on the server, which is the gate.
  const allowed = !!userData?.isAdmin || !!userData?.canEditMovies;
  return (
    <AdminShell
      section="movies"
      eyebrow="Movie Theater administration"
      tabs={moviesAdminTabs({ userData, setUserData })}
      allowed={allowed}
      deniedBody="The library tools are for editors and administrators. Ask an admin for the 'Can edit' permission."
    />
  );
}
