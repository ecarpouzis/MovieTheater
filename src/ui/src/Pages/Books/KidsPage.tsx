/**
 * `/books/kids` — the kid-safe corner, in one of two skins the reader picks (Comic Pop / Bubble Gum;
 * a SITE user setting, `BooksKidsStyle`, so it follows the account). Three modes, all in the URL:
 * the Home (`/explore/kids`, seeded daily), Browse all (`?mode=browse`: the catalog Shelves over the
 * kids source) and one series' shelf (`?series=`, where Explore's "More" and a shelf header land).
 * Hearts are the real want-to-read toggle; every cover opens the item modal; "Start reading" opens
 * the reader. The active skin is mirrored onto `<html>` while mounted so the modal can follow it.
 */
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { mapExplore } from "../../catalog/explore/mapExplore";
import { exploreWithLiveArt } from "./booksExploreArt";
import { useMediaToken } from "./booksMedia";
import type { CardItem } from "../../catalog/types";
import { fetchExploreKids, fetchItemMarks, fetchKidsSeriesItems, putItemMark, setKidsStyle } from "./booksApi";
import { readHref } from "./booksLinks";
import { bk, invalidateAfter } from "./booksQuery";
import { readSeed } from "./ExplorePage";
import KidsBrowse from "./KidsBrowse";
import KidsHome, { KidCover, prettyGenre, StarsInline, type KidStyle } from "./KidsHome";
import { openEntity } from "./openEntity";
import { toKidCard } from "../../catalog/sources/kidsSource";
import "./css/books-kids.css";

export interface KidsPageProps {
  userData: { booksKidsStyle?: string | null } | null | undefined;
  setUserData?: (updater: unknown) => void;
  epoch?: number;
}

export type KidsMode = "home" | "browse" | "shelf";

export function readKidsMode(search: string): { mode: KidsMode; seriesId?: number } {
  const p = new URLSearchParams(search);
  const s = p.get("series");
  if (s && /^[0-9]+$/.test(s) && Number(s) > 0) return { mode: "shelf", seriesId: Number(s) };
  return { mode: p.get("mode") === "browse" ? "browse" : "home" };
}

export function kidStyleOf(value: string | null | undefined): KidStyle {
  return value === "bubble" ? "bubble" : "pop";
}

function KidsShelf({ seriesId, style, wanted, onToggleWant, onOpen, onRead }: { seriesId: number; style: KidStyle; wanted: ReadonlySet<number>; onToggleWant: (id: number) => void; onOpen: (item: CardItem) => void; onRead: (id: number) => void }) {
  const q = useQuery({ queryKey: bk.kidsSeries(seriesId), queryFn: ({ signal }) => fetchKidsSeriesItems(seriesId, 0, 40, undefined, signal), staleTime: 5 * 60 * 1000 });
  const p = style === "pop" ? "pop" : "bub";
  if (q.isLoading) return <div className="kids-msg">Finding the comics…</div>;
  if (q.isError || !q.data) return <div className="kids-msg">That shelf is not here — pick another one.</div>;
  const items = q.data.items.map((row) => toKidCard(row, q.data.covers));
  const genre = items[0] ? ((items[0].raw as { tagsCsv?: string | null })?.tagsCsv ?? "").split(",")[0]?.trim() : "";
  return (
    <section className={`${p}-sec k-shelf`}>
      <header className={`${p}-sec-head`}>
        {style === "pop" ? (
          <><span className="pop-sec-title">{q.data.series.name.toUpperCase()}</span><span className="pop-sec-tab">{q.data.total} ISSUES</span><span className="pop-sec-rule" /></>
        ) : (
          <><span className="bub-sec-pill" style={{ background: "var(--mint)" }}>{q.data.series.name}</span><span className="bub-sec-count">{q.data.total} issues</span></>
        )}
      </header>
      <div className={`${p}-hero-meta k-shelf-meta`}>
        <StarsInline rating={q.data.series.rating} light={style === "pop"} />
        {items[0] && <button type="button" className={`${p}-cta k-shelf-cta`} onClick={() => onRead(items[0].id)}>{style === "pop" ? "START READING →" : "Read it now ♥"}</button>}
      </div>
      <div className="k-row-cards k-wrap-cards">
        {items.map((it) => (
          <div className={`${p}-card`} key={it.key}>
            <KidCover item={it} height={style === "pop" ? 210 : 214} wanted={wanted} onToggleWant={onToggleWant} onOpen={onOpen} />
            <div className={`${p}-card-label`}>
              <div className={`${p}-card-title`}>{it.title}</div>
              <div className={`${p}-card-sub`}>{prettyGenre(genre) || it.label}</div>
            </div>
          </div>
        ))}
      </div>
    </section>
  );
}

export default function KidsPage({ userData, setUserData, epoch = 0 }: KidsPageProps) {
  const history = useHistory();
  const location = useLocation();
  const qc = useQueryClient();
  const style = kidStyleOf(userData?.booksKidsStyle);
  const { mode, seriesId } = readKidsMode(location.search);
  const seed = readSeed(location.search);

  useEffect(() => {
    document.documentElement.setAttribute("data-kids-style", style);
    return () => document.documentElement.removeAttribute("data-kids-style");
  }, [style]);

  const home = useQuery({ queryKey: bk.exploreKids(seed), queryFn: ({ signal }) => fetchExploreKids(seed, signal), staleTime: 30 * 60 * 1000, enabled: mode === "home" });
  const media = useMediaToken();
  const homeData = useMemo(() => (home.data ? exploreWithLiveArt(mapExplore(home.data)) : null), [home.data, media.epoch]);

  const want = useQuery({ queryKey: bk.itemMarks("want"), queryFn: ({ signal }) => fetchItemMarks("want", 0, 500, signal) });
  const [pending, setPending] = useState<Map<number, boolean>>(new Map());
  const wanted = useMemo(() => {
    const s = new Set<number>((want.data?.entries ?? []).map((e) => e.itemId));
    for (const [id, on] of pending) { if (on) s.add(id); else s.delete(id); }
    return s;
  }, [want.data, pending]);
  const toggleWant = useMutation({
    mutationFn: async ({ id, on }: { id: number; on: boolean }) => putItemMark(id, { wantToRead: on }),
    onMutate: ({ id, on }) => setPending((m) => new Map(m).set(id, on)),
    onSettled: async (_r, _e, { id }) => { await invalidateAfter(qc, { kind: "itemMark", itemId: id }); setPending((m) => { const n = new Map(m); n.delete(id); return n; }); },
  });
  const onToggleWant = useCallback((id: number) => toggleWant.mutate({ id, on: !wanted.has(id) }), [toggleWant, wanted]);

  const setStyle = (next: KidStyle) => {
    if (next === style) return;
    setUserData?.((prev: Record<string, unknown> | null) => (prev ? { ...prev, booksKidsStyle: next } : prev));
    void setKidsStyle(next).catch(() => { /* the toggle stays local until the next /API/Me */ });
  };
  const go = useCallback((params: Record<string, string | null>) => {
    const p = new URLSearchParams(location.search);
    p.delete("item");
    for (const [k, v] of Object.entries(params)) { if (v == null) p.delete(k); else p.set(k, v); }
    const s = p.toString();
    history.push({ pathname: location.pathname, search: s ? `?${s}` : "" });
  }, [history, location.pathname, location.search]);
  const onOpen = useCallback((item: CardItem) => openEntity(history, location, { kind: "item", id: item.id }), [history, location]);
  const onRead = useCallback((id: number) => history.push(readHref(id), { from: location }), [history, location]);
  const onOpenShelf = useCallback((id: number) => go({ series: String(id), mode: null }), [go]);

  const pop = style === "pop";
  const navLabel = mode === "home" ? (pop ? "BROWSE ALL →" : "Browse all →") : (pop ? "★ HOME" : "★ Home");
  const goNav = () => (mode === "home" ? go({ mode: "browse", series: null }) : go({ mode: null, series: null }));

  return (
    <div className={`kids-shell kids kids-${style}`} data-kids-style={style}>
      {style === "bubble" && (
        <>
          <span className="bub-blob" style={{ width: 180, height: 180, background: "#ffd0e6", top: 60, left: -40 }} />
          <span className="bub-blob" style={{ width: 130, height: 130, background: "#cfe6ff", top: 320, right: 30 }} />
          <span className="bub-blob" style={{ width: 150, height: 150, background: "#d2f7e4", bottom: 120, left: 60 }} />
        </>
      )}
      <div className={pop ? "pop-top" : "bub-top"}>
        {pop ? (
          <><span className="pop-mark">BOOKS</span><span className="pop-badge">KIDS!</span><span className="pop-top-spacer" /></>
        ) : (
          <><span className="bub-mark"><span className="bub-dot" />Books</span><span className="bub-chip">for kids</span><span className="bub-top-spacer" /></>
        )}
        <div className="kids-bar-controls">
          <button type="button" className="kids-bar-nav" onClick={goNav}>{navLabel}</button>
          <div className="kids-theme-toggle" role="tablist" aria-label="Kids view style">
            <button type="button" role="tab" aria-selected={pop} className={`kids-theme-btn${pop ? " on" : ""}`} onClick={() => setStyle("pop")}>◆ Comic Pop</button>
            <button type="button" role="tab" aria-selected={!pop} className={`kids-theme-btn${!pop ? " on" : ""}`} onClick={() => setStyle("bubble")}>♥ Bubble Gum</button>
          </div>
        </div>
      </div>

      <div className={pop ? "pop-stage" : "bub-stage"}>
        {mode === "browse" && <KidsBrowse epoch={epoch} onOpen={onOpen} onOpenShelf={onOpenShelf} />}
        {mode === "shelf" && seriesId != null && (
          <KidsShelf seriesId={seriesId} style={style} wanted={wanted} onToggleWant={onToggleWant} onOpen={onOpen} onRead={onRead} />
        )}
        {mode === "home" && home.isLoading && <div className="kids-msg">Loading the fun…</div>}
        {mode === "home" && !home.isLoading && (home.isError || !homeData || homeData.spotlight.length === 0) && (
          <div className="kids-msg">No kid-friendly comics or books are available yet. An admin can mark more of the library as all-ages.</div>
        )}
        {mode === "home" && homeData && homeData.spotlight.length > 0 && (
          <KidsHome data={homeData} style={style} wanted={wanted} onToggleWant={onToggleWant} onOpen={onOpen} onRead={onRead} onOpenShelf={onOpenShelf} />
        )}
      </div>
    </div>
  );
}
