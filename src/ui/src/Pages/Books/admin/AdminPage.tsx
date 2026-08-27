/**
 * `/books/admin?tab=` — the operator's ten tabs against the R6 admin API on the host, on the SITE's
 * admin shell (R9 S6: `src/ui/src/admin/AdminShell` IS this page's own tab row, lifted out so every
 * section wears it). The tab is in the URL (a link to "the Series tab" is a real link); a non-admin
 * never reaches this (BooksPage redirects), and the host re-checks `[Authorize(Policy = "admin")]`
 * on every call.
 */
import { lazy } from "react";
import AdminShell, { readAdminTab as readShellTab, type AdminTabDef } from "../../../admin/AdminShell";
import "../css/books-admin.css";

const OverviewTab = lazy(() => import("./tabs/OverviewTab"));
const LibraryTab = lazy(() => import("./tabs/LibraryTab"));
const ComicVineTab = lazy(() => import("./tabs/ComicVineTab"));
const SeriesTab = lazy(() => import("./tabs/SeriesTab"));
const CollectionsTab = lazy(() => import("./tabs/CollectionsTab"));
const NormalizationTab = lazy(() => import("./tabs/NormalizationTab"));
const KidsTab = lazy(() => import("./tabs/KidsTab"));
const DuplicatesTab = lazy(() => import("./tabs/DuplicatesTab"));
const ConfigTab = lazy(() => import("./tabs/ConfigTab"));
const SystemTab = lazy(() => import("./tabs/SystemTab"));

export const ADMIN_TABS: AdminTabDef[] = [
  { key: "overview", label: "Overview", render: () => <OverviewTab /> },
  { key: "library", label: "Library", render: () => <LibraryTab /> },
  { key: "comicvine", label: "ComicVine", render: () => <ComicVineTab /> },
  { key: "series", label: "Series", render: () => <SeriesTab /> },
  { key: "collections", label: "Collections", render: () => <CollectionsTab /> },
  { key: "normalization", label: "Normalization", render: () => <NormalizationTab /> },
  { key: "kids", label: "Kids", render: () => <KidsTab /> },
  { key: "duplicates", label: "Duplicates", render: () => <DuplicatesTab /> },
  { key: "config", label: "Config", render: () => <ConfigTab /> },
  { key: "system", label: "System", render: () => <SystemTab /> },
];

/** Books' own reading of `?tab=`, kept for callers that link into a tab. */
export const readAdminTab = (search: string) => readShellTab(search, ADMIN_TABS);

export default function AdminPage() {
  return <AdminShell section="books" eyebrow="Library administration" tabs={ADMIN_TABS} className="books-admin books-surface" />;
}
