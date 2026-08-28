import { memo, useEffect, useMemo, useRef, useState } from "react";
import "./CatalogPager.css";

// A catalog's faux-pagination strip, shared by the arcade lobby and the music library. "Faux" because
// the grid is one continuously scrolling, infinitely-appending list — the buttons don't slice it into
// pages, they SEEK into it: a click re-anchors the grid at that offset and infinite scroll carries on
// from there.
//
// Sorted A–Z (the default in both catalogs) the strip shows letters, because that's the landmark the
// list actually has: ~17k arcade cards is 440 pages, and "page 147" means nothing to anyone. Under any
// other sort (rating, year, system, players) alphabet buckets are meaningless, so it falls back to
// numbers.
//
// The two modes differ in their CONTENT and in nothing else: one nav class, one button class, one
// growth rule, one overflow-x strip, and BOTH render their run in full — every letter, every page.
//
// A run rendered in full is a run you have to be able to TRAVEL, so the strip owns three ways to
// move: the scrollbar (restored under a fine pointer, in the CSS), the wheel (translated to
// horizontal here), and the two sticky end caps that seek to the first and last of the run.
//
// The pure helpers are exported for tests — and they live in THIS file rather than a `catalogPager.js`
// beside it because Windows' filesystem is case-insensitive, so the two names are one file.

export const LETTERS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".split("");

/** How long a tapped letter is held before a hand scroll is allowed to release it. Long enough to
 *  cover the drift of a real thumb tap and the settle of a smooth scroll; short enough that the
 *  readout is honest again by the time anyone has read a card. */
export const PIN_ARM_MS = 350;

/** The full #, A–Z strip, with the caller's counts/offsets merged in (absent buckets → count 0). */
export function letterStrip(letters) {
  const byLetter = new Map((letters || []).map((l) => [l.letter, l]));
  return ["#", ...LETTERS].map((letter) => {
    const hit = byLetter.get(letter);
    return { letter, count: hit?.count ?? 0, offset: hit?.offset ?? 0 };
  });
}

/** Which letter bucket the card at `index` falls in — the last bucket that starts at or before it. */
export function activeLetter(letters, index) {
  let active = null;
  for (const l of letters || []) {
    if (l.count > 0 && l.offset <= index) active = l.letter;
  }
  return active;
}

/**
 * The FULL run of pages, 1…N — the way the letters mode renders every letter, and for the same
 * reason: the strip is an INDEX of the list, and an index you have to walk a page at a time to reach
 * the middle of is not one. It used to be condensed (1 … 6 7 [8] 9 10 … 289), which was the wrong
 * half of a pair of defects Eric named on 2026-08-28: seven buttons cannot overflow a desktop row, so
 * flex growth blew each of them out to ~290 px AND there was nothing left to scroll. *"What I had
 * meant about numbers not stretching is that the buttons themselves become too wide, and can't be
 * scrolled like the letters can be. Both are defects."* The full run is what makes the strip
 * scrollable, exactly like the alphabet on a phone; the `max-width` in the CSS is what keeps a SHORT
 * run's buttons from stretching.
 */
export function pageStrip(totalPages) {
  const n = Math.max(1, Math.floor(totalPages) || 1);
  return Array.from({ length: n }, (_, i) => i + 1);
}

/** The 1-based page an absolute card index lives on. */
export function pageOf(index, pageSize) {
  return Math.floor(Math.max(0, index) / pageSize) + 1;
}

/**
 * #, A–Z bucket offsets over an ALREADY-SORTED list held client-side — what the music library feeds
 * the strip, since its whole catalog is in the browser and there's no server to ask for buckets the
 * way the arcade does.
 *
 * Accumulated into a map rather than pushed per run: a sort key whose accents fold differently than
 * the server's collation ("Ángel" filed next to "Anderson") would otherwise open a SECOND bucket for
 * the same letter, and letterStrip keeps only one of them. First offset wins, every hit counts.
 */
export function bucketsFor(items, keyOf) {
  const map = new Map();
  (items || []).forEach((item, i) => {
    const key = (keyOf(item) || "").trim().normalize("NFD").replace(/[\u0300-\u036f]/g, "");
    const ch = key.charAt(0).toUpperCase();
    const letter = ch >= "A" && ch <= "Z" ? ch : "#";
    const hit = map.get(letter);
    if (hit) hit.count += 1;
    else map.set(letter, { letter, count: 1, offset: i });
  });
  return [...map.values()];
}

function CatalogPager({ mode, letters, total, pageSize, currentIndex, onJump, disabled, itemNoun = "game" }) {
  const strip = useMemo(() => letterStrip(letters), [letters]);
  const currentPage = pageOf(currentIndex, pageSize);
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  // The run depends on the LIST, not on where the reader is in it — so scrolling the grid re-marks a
  // button, it never rebuilds the strip.
  const pages = useMemo(() => (mode === "letters" ? [] : pageStrip(totalPages)), [mode, totalPages]);
  // ── The tapped letter wins, until the reader takes over ────────────────────────────────────────
  // The readout otherwise names whatever the grid's scroll-spy reports at the top of the list, and a
  // GRID ROW holds `cols` cards — so a letter whose first card is not in column 0 shares its top row
  // with the tail of the previous letter, and the spy (which reports the row's FIRST item) names that
  // one. Tap M, get L. Reported 2026-08-13: "I tap a letter and the bar instead highlights the letter
  // before it."
  //
  // The Long Box never hits this because its spy unit is a whole shelf, which cannot straddle a
  // letter boundary — so it can afford to let the spy speak for the rail unconditionally. Ours can,
  // so an explicit tap is held as the truth until the reader scrolls for themselves, at which point
  // the honest readout comes back. Same wheel/touchmove/key trio the jump itself is cancelled by.
  const [pinned, setPinned] = useState(null);
  const railRef = useRef(null);
  // A tap on a phone often carries a pixel or two of drift, and a smooth scroll settles over a few
  // frames — both would fire a release immediately. Arm the listeners a beat later instead.
  useEffect(() => {
    if (!pinned) return undefined;
    // Scrolling the STRIP is not "the reader took over the grid" — it's them looking for the next
    // letter to tap. Only a gesture aimed at the page releases the pin. (window-level events in the
    // tests target `window`, which is not a Node, so they still release.)
    const release = (e) => {
      const rail = railRef.current;
      if (rail && e?.target instanceof Node && rail.contains(e.target)) return;
      setPinned(null);
    };
    const onKey = (e) => {
      if (e.key?.startsWith("Arrow") || e.key === "PageUp" || e.key === "PageDown"
          || e.key === "Home" || e.key === "End" || e.key === " ") release();
    };
    const arm = setTimeout(() => {
      window.addEventListener("wheel", release, { passive: true, capture: true });
      window.addEventListener("touchmove", release, { passive: true, capture: true });
      window.addEventListener("keydown", onKey);
    }, PIN_ARM_MS);
    return () => {
      clearTimeout(arm);
      window.removeEventListener("wheel", release, { capture: true });
      window.removeEventListener("touchmove", release, { capture: true });
      window.removeEventListener("keydown", onKey);
    };
  }, [pinned]);
  // A different list (new filter, new shelf) or a switch to page mode: the pin describes a letter in
  // a catalog that no longer exists.
  useEffect(() => { setPinned(null); }, [letters, mode]);

  // Computed before the early returns below so the hook order stays fixed.
  const spyLetter = mode === "letters" ? activeLetter(strip, currentIndex) : null;
  const currentLetter = mode === "letters" ? (pinned ?? spyLetter) : null;

  // The strip is one swipeable row (see the CSS), so part of the run is off-screen — the whole
  // alphabet on a phone, and the whole page run at any size once a catalog is more than a screen's
  // worth of pages long — and the active button has to be brought back to where it can be read. It
  // is the STRIP that scrolls, never the page: `scrollIntoView` on a button inside a sticky bar
  // would drag the document with it.
  //
  // ⚠ MINIMALLY, not centred. Centring was the original rule and it is what made "jump to M, then
  // A–L are gone" a real complaint: the scrollbar is hidden on both engines, so on a desktop with a
  // mouse there is no affordance at all for scrolling a horizontal strip back — centring M put half
  // the alphabet somewhere the user could not get to. Nudging just far enough to reveal the button
  // keeps every letter it did not have to hide, and the CSS below both stretches the strip (so it
  // rarely overflows at all) and restores a real scrollbar where a pointer can use one.
  const activeRef = useRef(null);
  const startCapRef = useRef(null);
  const endCapRef = useRef(null);

  // Does the run actually overflow the row? That is the whole condition for the end caps below —
  // they exist because the strip has somewhere to go, so on a desktop where all 27 letters fit
  // there is nothing for them to do and they stay out of the row.
  const [overflowing, setOverflowing] = useState(false);
  useEffect(() => {
    const rail = railRef.current;
    if (!rail) return undefined;
    const measure = () => setOverflowing(rail.scrollWidth > rail.clientWidth + 1);
    measure();
    if (typeof ResizeObserver === "undefined") return undefined;
    const ro = new ResizeObserver(measure); // the row narrows with the window, and with the sider opening
    ro.observe(rail);
    return () => ro.disconnect();
  }, [mode, strip.length, pages.length]);

  // ── The wheel scrolls the strip ────────────────────────────────────────────────────────────────
  // The hidden-scrollbar note below is only half the story: even with the scrollbar handed back, a
  // mouse's only way to move a horizontal strip is to grab an 8px bar or hold shift. With hundreds
  // of page buttons that is not a control, which is what Eric reported on 2026-08-28: "There doesn't
  // appear to be a way to scroll the number paging buttons on a desktop." So a plain vertical wheel
  // over the strip moves it sideways.
  //
  // It gives the gesture back the moment the strip runs out of room — otherwise the sticky bar,
  // which lies across the bottom of the scrollport the pointer is already in, would swallow every
  // wheel tick that crossed it and the page would stop scrolling. Same reason ctrl+wheel (zoom) and
  // an already-horizontal trackpad swipe (handled natively) are left alone.
  useEffect(() => {
    const rail = railRef.current;
    if (!rail) return undefined;
    const onWheel = (e) => {
      if (e.ctrlKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) return;
      const max = rail.scrollWidth - rail.clientWidth;
      if (max <= 1) return;
      // deltaMode 1 is lines (Firefox), 2 is pages — both are unusable as raw pixels.
      const unit = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? rail.clientWidth : 1;
      const to = Math.min(max, Math.max(0, rail.scrollLeft + e.deltaY * unit));
      if (Math.abs(to - rail.scrollLeft) < 1) return; // at that end already: let the page have it
      e.preventDefault();
      rail.scrollLeft = to;
    };
    rail.addEventListener("wheel", onWheel, { passive: false });
    return () => rail.removeEventListener("wheel", onWheel);
  }, []);

  // The strip is one swipeable row (see the CSS), so part of the run is off-screen — the whole
  // alphabet on a phone, and the whole page run at any size once a catalog is more than a screen's
  // worth of pages long — and the active button has to be brought back to where it can be read. It
  // is the STRIP that scrolls, never the page: `scrollIntoView` on a button inside a sticky bar
  // would drag the document with it.
  //
  // ⚠ MINIMALLY, not centred. Centring was the original rule and it is what made "jump to M, then
  // A–L are gone" a real complaint: the scrollbar is hidden on both engines, so on a desktop with a
  // mouse there is no affordance at all for scrolling a horizontal strip back — centring M put half
  // the alphabet somewhere the user could not get to. Nudging just far enough to reveal the button
  // keeps every letter it did not have to hide, and the CSS below both stretches the strip (so it
  // rarely overflows at all) and restores a real scrollbar where a pointer can use one.
  useEffect(() => {
    const rail = railRef.current;
    const btn = activeRef.current;
    if (!rail || !btn) return;
    if (rail.scrollWidth <= rail.clientWidth + 1) return; // fits: a scrollTo would be a no-op that still costs a layout read
    // Don't leave the button flush against the edge it was just pulled past — and where an end cap
    // is parked on that edge, "revealed" means clear of the CAP, not of the rail.
    const padStart = 8 + (startCapRef.current?.offsetWidth || 0);
    const padEnd = 8 + (endCapRef.current?.offsetWidth || 0);
    const left = btn.offsetLeft - padStart;
    const right = btn.offsetLeft + btn.offsetWidth + padEnd;
    let to = rail.scrollLeft;
    if (left < rail.scrollLeft) to = left;
    else if (right > rail.scrollLeft + rail.clientWidth) to = right - rail.clientWidth;
    if (Math.abs(to - rail.scrollLeft) < 1) return; // already visible — leave the view where it is
    rail.scrollTo({ left: Math.max(0, to), behavior: "smooth" });
  }, [currentLetter, currentPage, mode, overflowing]);

  // ── The two ends of the run ────────────────────────────────────────────────────────────────────
  // A thousand-page run is a thousand buttons wide, and no amount of wheeling makes the far end of
  // that a place you can get to: "there also needs to be a way to fast-scroll to the end of the
  // line, since there's potentially thousands of pages" (2026-08-28). So the run keeps a first and a
  // last, pinned to the edges of the strip (sticky, so they never scroll away) — Home and End for a
  // control that has no keyboard. They SEEK, like every other button here, and drag the strip to
  // that end with them so the neighbourhood you landed in is on screen straight away.
  const firstBucket = strip.find((l) => l.count > 0) || null;
  const lastBucket = [...strip].reverse().find((l) => l.count > 0) || null;
  const ends = mode === "letters"
    ? {
        start: firstBucket && { name: firstBucket.letter, offset: firstBucket.offset, letter: firstBucket.letter },
        end: lastBucket && { name: lastBucket.letter, offset: lastBucket.offset, letter: lastBucket.letter },
      }
    : {
        start: { name: "1", offset: 0 },
        end: { name: String(totalPages), offset: (totalPages - 1) * pageSize },
      };
  const noun = mode === "letters" ? "letter" : "page";
  const seekEnd = (edge, toFar) => {
    if (!edge) return;
    if (edge.letter) setPinned(edge.letter);
    onJump(edge.offset);
    const rail = railRef.current;
    // Immediately, not by waiting on the grid to report back where it landed — the point of the
    // control is that the far end of the strip is one click away.
    if (rail) rail.scrollTo({ left: toFar ? rail.scrollWidth : 0, behavior: "smooth" });
  };

  if (!total) return null;
  if (mode !== "letters" && totalPages <= 1) return null; // one page of results: nothing to seek to

  return (
    <nav
      ref={railRef}
      /* ONE layout for both modes — same nav class, same button class, same growth rule, same
         overflow-x strip. What differs is only what is IN it: 27 letters or N pages. */
      className="catalog-pager"
      aria-label={mode === "letters" ? "Jump to letter" : "Jump to page"}
    >
      <button
        ref={startCapRef}
        type="button"
        hidden={!overflowing || !ends.start}
        className="catalog-pager__end catalog-pager__end--start"
        disabled={disabled}
        aria-label={`Jump to the first ${noun}`}
        title={ends.start ? `First ${noun} (${ends.start.name})` : `First ${noun}`}
        onClick={() => seekEnd(ends.start, false)}
      >
        «
      </button>
      {mode === "letters"
        ? strip.map(({ letter, count, offset }) => (
            <button
              key={letter}
              type="button"
              ref={letter === currentLetter ? activeRef : undefined}
              className={`catalog-pager__btn${letter === currentLetter ? " catalog-pager__btn--active" : ""}`}
              disabled={disabled || count === 0}
              title={count ? `${count.toLocaleString()} ${count === 1 ? itemNoun : `${itemNoun}s`}` : `No ${itemNoun}s`}
              aria-current={letter === currentLetter ? "true" : undefined}
              onClick={() => { setPinned(letter); onJump(offset); }}
            >
              {letter}
            </button>
          ))
        : pages.map((page) => (
            <button
              key={page}
              type="button"
              ref={page === currentPage ? activeRef : undefined}
              className={`catalog-pager__btn${page === currentPage ? " catalog-pager__btn--active" : ""}`}
              disabled={disabled}
              title={`Page ${page} of ${totalPages}`}
              aria-current={page === currentPage ? "true" : undefined}
              onClick={() => onJump((page - 1) * pageSize)}
            >
              {page}
            </button>
          ))}
      <button
        ref={endCapRef}
        type="button"
        hidden={!overflowing || !ends.end}
        className="catalog-pager__end catalog-pager__end--far"
        disabled={disabled}
        aria-label={`Jump to the last ${noun}`}
        title={ends.end ? `Last ${noun} (${ends.end.name})` : `Last ${noun}`}
        onClick={() => seekEnd(ends.end, true)}
      >
        »
      </button>
    </nav>
  );
}

export default memo(CatalogPager);
