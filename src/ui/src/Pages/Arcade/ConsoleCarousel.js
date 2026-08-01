import { useCallback, useLayoutEffect, useMemo, useRef, useState } from "react";
import { byConsoleAge, consoleTile, systemLabel, systemYear, HEAVY_LANE_SYSTEMS } from "./arcadeSystems";

const COLLAPSE_KEY = "arcade.consoles.collapsed";
const STREAMED_KEY = "arcade.consoles.showStreamed";

function loadFlag(key) {
  try { return localStorage.getItem(key) === "1"; } catch { return false; }
}
function saveFlag(key, v) {
  try { localStorage.setItem(key, v ? "1" : "0"); } catch { /* private mode — not worth failing over */ }
}

/** One console tile. A toggle, not a link: pressing it ADDS its system to the filter, pressing it
 *  again removes it, which is why it reports `aria-pressed` rather than behaving like a radio. */
function ConsoleTile({ system, count, selected, onToggle }) {
  const art = consoleTile(system);
  const label = systemLabel(system);
  const year = systemYear(system);
  return (
    <button
      type="button"
      className={"arcade-console" + (selected ? " is-selected" : "")}
      aria-pressed={selected}
      title={`${label}${year ? ` (${year})` : ""} — ${count.toLocaleString()} game${count === 1 ? "" : "s"}`}
      onClick={() => onToggle(system)}
    >
      <span className="arcade-console__art">
        {art ? (
          /* alt="" because the visible name below already names the console — a screen reader
             reading both would announce every tile twice. */
          <img src={art} alt="" loading="lazy" decoding="async" width="200" height="130" />
        ) : (
          <span className="arcade-console__art-fallback">{label}</span>
        )}
        <span className="arcade-console__tick" aria-hidden="true">
          <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" strokeWidth="3.4"
            strokeLinecap="round" strokeLinejoin="round"><path d="M20 6 9 17l-5-5" /></svg>
        </span>
      </span>
      <span className="arcade-console__name">{label}</span>
      <span className="arcade-console__count">{count.toLocaleString()}</span>
    </button>
  );
}

/**
 * The console picker above the games grid: a horizontally scrolling shelf of console thumbnails that
 * drives the very same `?system=` filter the navbar rail's System dropdown does. Neither owns the
 * state — the URL does — so lighting up SNES here immediately shows SNES selected in the rail.
 *
 * It exists because a 47-entry dropdown is a bad way to answer "what have we got?". Recognising a
 * Dreamcast by sight is instant; finding "Dreamcast" in an alphabetical list of codenames is not.
 *
 * `systems` are the faceted counts from /API/Arcade/Filters, which the server computes EXCLUDING the
 * system filter itself. That's what keeps the shelf stable: every console keeps its real catalog
 * count and stays on screen while you pick, instead of the unpicked ones collapsing to zero.
 *
 * The order is by HARDWARE RELEASE DATE, newest first, so scrolling right walks back through console
 * generations. Sorting by catalog size instead put the shelf in an order nobody can predict — you
 * can't guess where the Dreamcast sits in a popularity ranking, but you know it came after the Saturn.
 */
export default function ConsoleCarousel({ systems, selected, onToggle, onClear }) {
  const railRef = useRef(null);
  const [collapsed, setCollapsed] = useState(() => loadFlag(COLLAPSE_KEY));
  const [showStreamed, setShowStreamed] = useState(() => loadFlag(STREAMED_KEY));
  const [edges, setEdges] = useState({ left: false, right: false });

  // Newest console first; the streamed (heavy/capture-lane) systems are held back until asked for,
  // since a handful of titles between them shouldn't sit at the head of the shelf advertising a
  // library we don't have yet. One that is already SELECTED always shows regardless — a filter you
  // can see but can't switch off is a trap, and a bookmarked ?system=switch would otherwise be one.
  const shelf = useMemo(() => (systems || [])
    .filter((s) => showStreamed || !HEAVY_LANE_SYSTEMS.has(s.value) || selected.includes(s.value))
    .slice()
    .sort((a, b) => byConsoleAge(a.value, b.value)), [systems, showStreamed, selected]);
  const streamedCount = useMemo(
    () => (systems || []).filter((s) => HEAVY_LANE_SYSTEMS.has(s.value)).length, [systems]);

  const measure = useCallback(() => {
    const el = railRef.current;
    if (!el) return;
    // 2px of slack: sub-pixel widths otherwise leave an arrow enabled that can't actually scroll.
    setEdges({
      left: el.scrollLeft > 2,
      right: el.scrollLeft + el.clientWidth < el.scrollWidth - 2,
    });
  }, []);

  // Measure after layout (not in an effect that races the images) and again whenever the box resizes.
  useLayoutEffect(() => {
    if (collapsed) return undefined;
    measure();
    const el = railRef.current;
    if (!el || typeof ResizeObserver === "undefined") return undefined;
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, [collapsed, measure, shelf]);

  const scrollBy = (dir) => {
    const el = railRef.current;
    if (!el) return;
    // Just under a viewport-width so a page-scroll always leaves an anchor tile in view.
    el.scrollBy({ left: dir * Math.max(240, el.clientWidth * 0.85), behavior: "smooth" });
  };

  if (!systems || systems.length === 0) return null;

  const selectedCount = selected.length;
  const total = systems.reduce((sum, s) => sum + (selected.includes(s.value) ? s.count : 0), 0);

  return (
    <section className="arcade-section arcade-consoles">
      <div className="arcade-section__head">
        <h2 className="arcade-section__title">Consoles</h2>
        <span className="arcade-section__count">
          {selectedCount === 0
            ? `${shelf.length} systems · newest first`
            : `${selectedCount} selected · ${total.toLocaleString()} game${total === 1 ? "" : "s"}`}
        </span>
        <div className="arcade-consoles__actions">
          {selectedCount > 0 && (
            <button type="button" className="arcade-consoles__link" onClick={onClear}>Clear consoles</button>
          )}
          <button
            type="button"
            className="arcade-consoles__link"
            aria-expanded={!collapsed}
            onClick={() => setCollapsed((c) => { saveFlag(COLLAPSE_KEY, !c); return !c; })}
          >
            {collapsed ? "Show" : "Hide"}
          </button>
        </div>
      </div>

      {!collapsed && (
        <div className="arcade-consoles__viewport">
          <button
            type="button" className="arcade-consoles__nav arcade-consoles__nav--prev"
            onClick={() => scrollBy(-1)} disabled={!edges.left} aria-label="Scroll consoles left" tabIndex={-1}
          >
            <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2.4"
              strokeLinecap="round" strokeLinejoin="round"><path d="m15 18-6-6 6-6" /></svg>
          </button>

          {/* The shelf itself is keyboard-reachable by tabbing through the tiles, so the arrows are
              taken out of the tab order (tabIndex -1) — they're a mouse/touch convenience only. */}
          <div className="arcade-consoles__rail" ref={railRef} onScroll={measure}>
            {shelf.map((s) => (
              <ConsoleTile
                key={s.value}
                system={s.value}
                count={s.count}
                selected={selected.includes(s.value)}
                onToggle={onToggle}
              />
            ))}
          </div>

          <button
            type="button" className="arcade-consoles__nav arcade-consoles__nav--next"
            onClick={() => scrollBy(1)} disabled={!edges.right} aria-label="Scroll consoles right" tabIndex={-1}
          >
            <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2.4"
              strokeLinecap="round" strokeLinejoin="round"><path d="m9 18 6-6-6-6" /></svg>
          </button>
        </div>
      )}

      {/* Off by default: the streamed lane is a handful of titles, and a shelf tile promises a library.
          Offered rather than hidden outright so the few that exist are still reachable — and the count
          in the label is honest about how little is behind it. */}
      {!collapsed && streamedCount > 0 && (
        <label className="arcade-consoles__streamed">
          <input
            type="checkbox"
            checked={showStreamed}
            onChange={(e) => { setShowStreamed(e.target.checked); saveFlag(STREAMED_KEY, e.target.checked); }}
          />
          <span>Show streamed systems ({streamedCount})</span>
        </label>
      )}
    </section>
  );
}
