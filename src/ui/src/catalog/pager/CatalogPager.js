import { memo, useEffect, useMemo, useRef, useState } from "react";
import "./CatalogPager.css";

// A catalog's faux-pagination strip, shared by the arcade lobby and the music library. "Faux" because
// the grid is one continuously scrolling, infinitely-appending list — the buttons don't slice it into
// pages, they SEEK into it: a click re-anchors the grid at that offset and infinite scroll carries on
// from there.
//
// Sorted A–Z (the default in both catalogs) the strip shows letters, because that's the landmark the
// list actually has: ~17k arcade cards is 289 pages, and "page 147" means nothing to anyone. Under any
// other sort (rating, year, system, players) alphabet buckets are meaningless, so it falls back to
// numbers.
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
 * Condensed numeric strip: 1 … 6 7 [8] 9 10 … 289. A gap is only inserted where it actually saves
 * buttons — eliding a single page ("1 … 3 4") is sillier than just showing page 2.
 */
export function pageStrip(current, totalPages, span = 2) {
  if (totalPages <= 1) return [{ type: "page", page: 1 }];
  const pages = new Set([1, totalPages]);
  for (let p = current - span; p <= current + span; p += 1) {
    if (p >= 1 && p <= totalPages) pages.add(p);
  }
  const strip = [];
  let prev = 0;
  for (const page of [...pages].sort((a, b) => a - b)) {
    const skipped = page - prev - 1;
    if (skipped === 1) strip.push({ type: "page", page: page - 1 });
    else if (skipped > 1) strip.push({ type: "gap", key: `gap-${prev}` });
    strip.push({ type: "page", page });
    prev = page;
  }
  return strip;
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
  const pages = useMemo(
    () => (mode === "letters" ? [] : pageStrip(currentPage, totalPages)),
    [mode, currentPage, totalPages]
  );
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
  // A tap on a phone often carries a pixel or two of drift, and a smooth scroll settles over a few
  // frames — both would fire a release immediately. Arm the listeners a beat later instead.
  useEffect(() => {
    if (!pinned) return undefined;
    const release = () => setPinned(null);
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

  // The strip is one swipeable row (see the CSS), so on a narrow screen part of the alphabet is
  // off-screen and the active button has to be brought back to where it can be read.
  //
  // ⚠ MINIMALLY, not centred. Centring was the original rule and it is what made "jump to M, then
  // A–L are gone" a real complaint: the scrollbar is hidden on both engines, so on a desktop with a
  // mouse there is no affordance at all for scrolling a horizontal strip back — centring M put half
  // the alphabet somewhere the user could not get to. Nudging just far enough to reveal the button
  // keeps every letter it did not have to hide, and the CSS below both stretches the strip (so it
  // rarely overflows at all) and restores a real scrollbar where a pointer can use one.
  const railRef = useRef(null);
  const activeRef = useRef(null);
  useEffect(() => {
    const rail = railRef.current;
    const btn = activeRef.current;
    if (!rail || !btn) return;
    if (rail.scrollWidth <= rail.clientWidth + 1) return; // fits: a scrollTo would be a no-op that still costs a layout read
    const pad = 8; // don't leave the button flush against the edge it was just pulled past
    const left = btn.offsetLeft - pad;
    const right = btn.offsetLeft + btn.offsetWidth + pad;
    let to = rail.scrollLeft;
    if (left < rail.scrollLeft) to = left;
    else if (right > rail.scrollLeft + rail.clientWidth) to = right - rail.clientWidth;
    if (Math.abs(to - rail.scrollLeft) < 1) return; // already visible — leave the view where it is
    rail.scrollTo({ left: Math.max(0, to), behavior: "smooth" });
  }, [currentLetter, currentPage, mode]);

  if (!total) return null;
  if (mode !== "letters" && totalPages <= 1) return null; // one page of results: nothing to seek to

  return (
    <nav
      ref={railRef}
      /* The letters modifier is what lets the buttons share the full width of the strip. Page
         numbers must NOT stretch: there are five of them and two ellipses, and spreading those
         across a 1500px page reads as a broken layout rather than as an alphabet. */
      className={`catalog-pager${mode === "letters" ? " catalog-pager--letters" : ""}`}
      aria-label={mode === "letters" ? "Jump to letter" : "Jump to page"}
    >
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
        : pages.map((item) =>
            item.type === "gap" ? (
              <span key={item.key} className="catalog-pager__gap" aria-hidden="true">…</span>
            ) : (
              <button
                key={item.page}
                type="button"
                ref={item.page === currentPage ? activeRef : undefined}
                className={`catalog-pager__btn${item.page === currentPage ? " catalog-pager__btn--active" : ""}`}
                disabled={disabled}
                aria-current={item.page === currentPage ? "true" : undefined}
                onClick={() => onJump((item.page - 1) * pageSize)}
              >
                {item.page}
              </button>
            )
          )}
    </nav>
  );
}

export default memo(CatalogPager);
