/**
 * `/books/admin?tab=` — the operator's ten tabs on antd, against the R6 admin API on the host.
 * The tab is in the URL (a link to "the Series tab" is a real link); a non-admin never reaches this
 * (BooksPage redirects), and the host re-checks `[Authorize(Policy = "admin")]` on every call.
 */
import { Tabs } from "antd";
import { lazy, Suspense } from "react";
import { useHistory, useLocation } from "react-router-dom";
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

export const ADMIN_TABS = [
  { key: "overview", label: "Overview" },
  { key: "library", label: "Library" },
  { key: "comicvine", label: "ComicVine" },
  { key: "series", label: "Series" },
  { key: "collections", label: "Collections" },
  { key: "normalization", label: "Normalization" },
  { key: "kids", label: "Kids" },
  { key: "duplicates", label: "Duplicates" },
  { key: "config", label: "Config" },
  { key: "system", label: "System" },
] as const;
export type AdminTabKey = (typeof ADMIN_TABS)[number]["key"];

export function readAdminTab(search: string): AdminTabKey {
  const t = new URLSearchParams(search).get("tab");
  return ADMIN_TABS.some((x) => x.key === t) ? (t as AdminTabKey) : "overview";
}

const BODY: Record<AdminTabKey, () => JSX.Element> = {
  overview: () => <OverviewTab />,
  library: () => <LibraryTab />,
  comicvine: () => <ComicVineTab />,
  series: () => <SeriesTab />,
  collections: () => <CollectionsTab />,
  normalization: () => <NormalizationTab />,
  kids: () => <KidsTab />,
  duplicates: () => <DuplicatesTab />,
  config: () => <ConfigTab />,
  system: () => <SystemTab />,
};

export default function AdminPage() {
  const history = useHistory();
  const location = useLocation();
  const tab = readAdminTab(location.search);
  const setTab = (next: string) => {
    const p = new URLSearchParams(location.search);
    p.set("tab", next);
    history.push({ pathname: location.pathname, search: `?${p.toString()}` });
  };
  return (
    <div className="books-admin books-surface">
      <header className="bka-head">
        <div className="bka-eyebrow">Library administration</div>
        <h1 className="bka-title">Admin</h1>
      </header>
      <Tabs
        activeKey={tab}
        onChange={setTab}
        className="bka-tabs"
        items={ADMIN_TABS.map((t) => ({ key: t.key, label: t.label, children: tab === t.key ? <Suspense fallback={<div className="bka-muted">Loading…</div>}>{BODY[t.key]()}</Suspense> : null }))}
      />
    </div>
  );
}
