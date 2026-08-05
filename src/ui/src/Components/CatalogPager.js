import { memo, useMemo } from "react";
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

  if (!total) return null;
  if (mode !== "letters" && totalPages <= 1) return null; // one page of results: nothing to seek to

  const currentLetter = mode === "letters" ? activeLetter(strip, currentIndex) : null;

  return (
    <nav className="catalog-pager" aria-label={mode === "letters" ? "Jump to letter" : "Jump to page"}>
      {mode === "letters"
        ? strip.map(({ letter, count, offset }) => (
            <button
              key={letter}
              type="button"
              className={`catalog-pager__btn${letter === currentLetter ? " catalog-pager__btn--active" : ""}`}
              disabled={disabled || count === 0}
              title={count ? `${count.toLocaleString()} ${count === 1 ? itemNoun : `${itemNoun}s`}` : `No ${itemNoun}s`}
              aria-current={letter === currentLetter ? "true" : undefined}
              onClick={() => onJump(offset)}
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
