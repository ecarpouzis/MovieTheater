/**
 * The Books section root (`/books`, non-exact — the photos pattern): the gate plates, the kid
 * pinning, the inner Switch, the URL-driven modals, and the section skin (backdrop + type theme,
 * applied ONCE here from the tweaks store so every view and modal agrees).
 *
 * Gate: a member on a password-verified session sees the section; a member without a password sees
 * the "needs a password" plate; a non-member sees the "ask the admin" plate. Every `/API/Books/*`
 * route re-checks server-side — the plates are a courtesy, not the gate.
 */
import { Suspense, lazy, useEffect, useMemo, useRef, useState } from "react";
import { Redirect, Route, Switch, useLocation } from "react-router-dom";
import { SectionIndexTabs } from "../../catalog/rail/SectionIndexRail";
import { readTweaks, subscribeTweaks } from "../../catalog/tweaks/useTweaks";
import CardGridSkeleton from "../../Components/CardGridSkeleton";
import useBooksIndex from "../../hooks/useBooksIndex";
import useIsMobile from "../../hooks/useIsMobile";
import { setMediaUser, useMediaToken } from "./booksMedia";
import { booksNavViews, booksSection, isKidAccount, kidAllowedPath, type BooksMe } from "./booksNav";
import { applySectionSkin, siteTheme } from "../../catalog/skin/skin";
import { booksSkinContext } from "./booksTheme";
import BrowsePage from "./BrowsePage";
import { readEntityParams } from "./openEntity";
import "./css/books.css";
import "./css/books-modal.css";
import "./css/books-backdrops.css";

const ItemModal = lazy(() => import("./ItemModal"));
const SeriesModal = lazy(() => import("./SeriesModal"));
const ExplorePage = lazy(() => import("./ExplorePage"));
const ShelfPage = lazy(() => import("./ShelfPage"));
const NovelsPage = lazy(() => import("./NovelsPage"));
const KidsPage = lazy(() => import("./KidsPage"));
const ReadPage = lazy(() => import("./read/ReadPage"));
const AdminPage = lazy(() => import("./admin/AdminPage"));

export interface BooksPageProps {
  userData: (BooksMe & { username?: string | null }) | null | undefined;
  setUserData?: (updater: unknown) => void;
}

function Plate({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="books-plate" role="status">
      <h1 className="books-plate-title">{title}</h1>
      <p className="books-plate-body">{children}</p>
    </div>
  );
}

/** A route whose page lands in a later slice: the index links resolve, the shell says what is coming. */
function Coming({ what }: { what: string }) {
  return <Plate title={what}>This part of the Books section is on its way — the browse, the modals and the readers land first.</Plate>;
}

export default function BooksPage({ userData, setUserData }: BooksPageProps) {
  const location = useLocation();
  const rootRef = useRef<HTMLDivElement>(null);
  const username = userData?.username ?? "";
  const isMobile = useIsMobile();

  // The media plane wants to know who is signed in (a different user re-mints); kick the mint early.
  useEffect(() => { setMediaUser(username || null); }, [username]);
  useMediaToken();

  // The section skin, from the tweaks store the catalog host writes: applied here to the section
  // root, re-applied on every write and on a light/dark switch (the backdrop family follows it).
  // The store and view the skin resolves from follow the URL (the browse, Novels and Kids each have
  // their own store; the backdrop is remembered per view).
  const [themeTick, setThemeTick] = useState(0);
  useEffect(() => {
    const unsubs = ["books", "books-novels", "books-kids"].map((s) => subscribeTweaks(s, () => setThemeTick((t) => t + 1)));
    return () => { for (const u of unsubs) u(); };
  }, []);
  const skinCtx = booksSkinContext(location.pathname, location.search);
  useEffect(() => {
    const apply = () => applySectionSkin(rootRef.current, skinCtx.store, readTweaks(skinCtx.store).extras, siteTheme(), skinCtx.view);
    apply();
    const mo = new MutationObserver(apply);
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    return () => mo.disconnect();
  }, [themeTick, skinCtx.store, skinCtx.view]);

  const section = booksSection(location.pathname);
  const kid = isKidAccount(userData);
  // The reader is pushed OVER the page it came from (`state.from`): that page stays mounted
  // underneath (its scroll, its bands, its modal) with visibility hidden, and Close is one Back.
  const reading = section === "read";
  const under = (reading && (location.state as { from?: typeof location } | undefined)?.from) || location;
  const entity = useMemo(() => readEntityParams(reading ? "" : location.search), [reading, location.search]);
  // The phone's section tabs (the rail is behind the hamburger there); the counts are asked for on phones only.
  const counts = useBooksIndex(userData, isMobile);

  if (userData === undefined) return <div className="books-section"><CardGridSkeleton count={12} /></div>;

  if (!userData || !userData.booksAccess) {
    return (
      <div className="books-section" ref={rootRef}>
        <Plate title="Books">The library is a members-only room. Ask the site admin to open it for your account.</Plate>
      </div>
    );
  }
  if (!userData.hasPassword) {
    return (
      <div className="books-section" ref={rootRef}>
        <Plate title="Books">Reading needs a password-protected account — set a password in your settings to open the library.</Plate>
      </div>
    );
  }
  if (kid && !kidAllowedPath(location.pathname)) return <Redirect to="/books/kids" />;

  return (
    <div className="books-section" ref={rootRef} data-books-section={section} data-kids-style={userData.booksKidsStyle ?? undefined}>
      {isMobile && section !== "read" && (
        <SectionIndexTabs views={booksNavViews(userData, counts)} activeKey={section} ariaLabel="Books sections" className="books-tabs" />
      )}
      <div className="books-under" style={reading ? { visibility: "hidden" } : undefined} aria-hidden={reading || undefined}>
        <Suspense fallback={<CardGridSkeleton count={12} />}>
          <Switch location={under}>
            <Route path="/books/read/:itemId"><div /></Route>
            <Route path="/books/explore">{kid ? <Redirect to="/books/kids" /> : <ExplorePage />}</Route>
            <Route path="/books/shelf"><ShelfPage /></Route>
            <Route path="/books/novels">{kid ? <Redirect to="/books/kids" /> : <NovelsPage username={username} />}</Route>
            <Route path="/books/kids"><KidsPage userData={userData} setUserData={setUserData} /></Route>
            <Route path="/books/admin">{userData.isAdmin ? <AdminPage /> : <Redirect to="/books" />}</Route>
            <Route path="/books"><BrowsePage username={username} isKid={kid} /></Route>
          </Switch>
        </Suspense>
      </div>
      {reading && (
        <Suspense fallback={<div className="books-reader" />}>
          <Route path="/books/read/:itemId"><ReadPage userData={userData} /></Route>
        </Suspense>
      )}
      <Suspense fallback={null}>
        {entity.item != null && <ItemModal itemId={entity.item} isKid={kid} />}
        {/* On the kids page `?series=` is a SHELF (the kids' single-series view), never the series modal. */}
        {entity.series != null && section !== "kids" && <SeriesModal seriesId={entity.series} isKid={kid} />}
      </Suspense>
    </div>
  );
}
