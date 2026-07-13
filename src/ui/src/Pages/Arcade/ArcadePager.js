import { memo, useMemo } from "react";

// The lobby's faux-pagination strip. "Faux" because the grid is one continuously scrolling,
// infinitely-appending list — the buttons don't slice it into pages, they SEEK into it: a click
// re-anchors the grid at that offset and infinite scroll carries on from there.
//
// Sorted A–Z (the default) the strip shows letters, because that's the landmark the catalog actually
// has: ~17k cards is 289 pages, and "page 147" means nothing to anyone. Under any other sort
// (rating, year, system, players) alphabet buckets are meaningless, so it falls back to numbers.
//
// The pure helpers are exported for tests — and they live in THIS file rather than an `arcadePager.js`
// beside it because Windows' filesystem is case-insensitive, so the two names are one file.

export const LETTERS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".split("");

/** The full #, A–Z strip, with the server's counts/offsets merged in (absent buckets → count 0). */
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

function ArcadePager({ mode, letters, total, pageSize, currentIndex, onJump, disabled }) {
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
    <nav className="arcade-pager" aria-label={mode === "letters" ? "Jump to letter" : "Jump to page"}>
      {mode === "letters"
        ? strip.map(({ letter, count, offset }) => (
            <button
              key={letter}
              type="button"
              className={`arcade-pager__btn${letter === currentLetter ? " arcade-pager__btn--active" : ""}`}
              disabled={disabled || count === 0}
              title={count ? `${count.toLocaleString()} ${count === 1 ? "game" : "games"}` : "No games"}
              aria-current={letter === currentLetter ? "true" : undefined}
              onClick={() => onJump(offset)}
            >
              {letter}
            </button>
          ))
        : pages.map((item) =>
            item.type === "gap" ? (
              <span key={item.key} className="arcade-pager__gap" aria-hidden="true">…</span>
            ) : (
              <button
                key={item.page}
                type="button"
                className={`arcade-pager__btn${item.page === currentPage ? " arcade-pager__btn--active" : ""}`}
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

export default memo(ArcadePager);
