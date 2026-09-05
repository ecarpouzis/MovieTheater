import { useState, useEffect, useRef, useMemo, useCallback } from "react";
import { MovieAPI } from "../../MovieAPI";
import { preloadImages } from "../../preloadImages";
import "./ChannelGrid.css";
import FallbackImage from "../../Components/FallbackImage";
import { nowPlaying } from "./channelNow";
import { programHeadline, programMeta, rowMatches } from "./guideModel";

const MS_PER_MIN = 60_000;
const HOURS_DEFAULT = 6;
const HOURS_MAX = 24; // the server clamps GuideGrid to 1–24 h
const HOURS_STEP = 6;
const NAV_STEP_MIN = 3 * 60; // ‹ / › move the timeline by three hours

// "9:30" in the viewer's local time.
export function clockLabel(ms) {
  return new Date(ms).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
}

// Is the keyboard busy with a text field? (The section bar's search box lives above the guide.)
function typingTarget(e) {
  const t = e.target;
  if (!t || !t.tagName) return false;
  return t.tagName === "INPUT" || t.tagName === "TEXTAREA" || t.tagName === "SELECT" || t.isContentEditable;
}

/**
 * The cross-channel grid guide (EPG): channels down the side, time across the top, each program a
 * block sized by its runtime, with a live "now" line and the airing portion shaded. Clicking any
 * channel (label or program) tunes you to it at the live offset — you can't start a future movie early.
 *
 * Rows come from the caller's channel list (already age-gated and numbered, so the guide and the 1-9
 * hotkeys agree); lineups are joined in by id from /API/Channel/GuideGrid. Scales to many channels:
 * the endpoint is a bounded read, rows use CSS `content-visibility` so off-screen rows skip layout,
 * and the "now" line advances from a local clock tick rather than re-fetching.
 */
/**
 * `onPickProgram(channel, program, rowItems)` — when given, a program cell SELECTS the show (the
 * guide page opens its detail panel) instead of tuning the channel; the channel button still
 * tunes. `selectedKey` (`${channelId}:${startUtc}`) marks the selected cell.
 *
 * `query` and `favoriteIds` narrow which ROWS are drawn (the guide page binds them to the section
 * bar's search box and its Favourites pill). They filter here rather than in the caller because a
 * search has to reach the PROGRAMS, and the lineup is fetched in this component.
 *
 * `onLineup({ byId, serverNowUtc, skewMs })` fires after every successful load so the guide page can
 * auto-select a programme and keep its detail panel on the same server clock as the now line.
 *
 * Cells are title + a meta line (guide v2: `2002 · PG · 1h 35m`, `S03 E09 · Ep · TV-PG · 30 min`);
 * the plot lives in the page's detail panel. A block that began before the window is cut at the left
 * edge and marked with a ‹ so it reads as "continues" rather than "starts here".
 *
 * Guide v2.1 (2026-09-05), the four "further" moves:
 *   * **Titles hug the visible edge.** The scroller publishes its scrollLeft as `--epg-scrollx`; each
 *     block knows its drawn start/width in minutes (`--epg-l`, `--epg-w`) and CSS pads its text right
 *     by however much of the block is hidden under the sticky column — a two-hour film scrolled
 *     halfway keeps its name at the seam, as on a real guide, and ellipsizes as the visible part shrinks.
 *   * **Arrow keys** (page mode only — the room has its own hotkeys): ↑/↓ the same moment on the row
 *     above/below, ←/→ the previous/next programme, Enter tunes the selected channel. The selected cell
 *     is scrolled into view, clear of the sticky column.
 *   * **‹ Now ›** in the axis corner step the timeline three hours; stepping toward the fetched horizon
 *     asks the server for six more hours (up to its 24 h cap) — an evening can be planned without a day
 *     picker. Now jumps back to the window's start (which is ≤ 30 min before now).
 *   * **Denser rows on tall screens** live in the CSS (`min-height: 1100px` → 56px rows).
 */
function ChannelGrid({ open, channels, currentChannelId, onPick, onClose, onPickProgram, selectedKey, query = "", favoriteIds = null, onLineup }) {
  const [lineup, setLineup] = useState(null); // { serverNowUtc, hours, byId } or null while loading
  const [nowMs, setNowMs] = useState(() => Date.now());
  const [hours, setHours] = useState(HOURS_DEFAULT);
  const hoursRef = useRef(HOURS_DEFAULT);
  hoursRef.current = hours;
  const scrollRef = useRef(null);
  const axisRef = useRef(null);
  const onLineupRef = useRef(onLineup);
  onLineupRef.current = onLineup;

  // Track the server↔client clock skew captured at fetch time, so the "now" line sits where the
  // server thinks now is even if the browser clock is off.
  const skewRef = useRef(0);

  const load = useCallback(async () => {
    try {
      // Time-bound the request: on a stalled connection (congested public Wi-Fi, etc.) a hung fetch would
      // otherwise leave the guide on "Updating…" until the next slow poll. Aborting lets the caller retry.
      const ctrl = new AbortController();
      const timeout = setTimeout(() => ctrl.abort(), 12_000);
      let r;
      try {
        r = await MovieAPI.getGuideGrid(hoursRef.current, ctrl.signal);
      } finally {
        clearTimeout(timeout);
      }
      if (!r.ok) return false;
      const data = await r.json();
      const byId = new Map();
      for (const c of data.items || []) byId.set(c.id, c);
      const serverNow = Date.parse(data.serverNowUtc);
      skewRef.current = Number.isFinite(serverNow) ? serverNow - Date.now() : 0;
      setLineup({
        serverNowUtc: data.serverNowUtc,
        hours: data.hours || HOURS_DEFAULT,
        lookbackMinutes: data.lookbackMinutes ?? 30,
        byId,
      });
      onLineupRef.current?.({ byId, serverNowUtc: data.serverNowUtc, skewMs: skewRef.current });

      // Preload every channel's now-playing poster up front (~121 small thumbs, one per row) so
      // scrolling the guide never snaps a poster in. "auto" priority — here the posters are the content.
      const at = Number.isFinite(serverNow) ? serverNow : Date.now();
      preloadImages(
        (data.items || [])
          .map((c) => { const np = nowPlaying(c.items, at); return np?.posterId ? MovieAPI.getPosterThumbnail(np.posterId, np.posterVersion, np.kind) : null; })
          .filter(Boolean),
        "auto"
      );
      return true;
    } catch {
      return false; // transient (network/abort) — the caller retries
    }
  }, []);

  // (Re)load whenever the guide opens; refresh slowly while it stays open (schedules are stable, so
  // a frequent poll would be wasted work — the now line moves on its own between refreshes).
  useEffect(() => {
    if (!open) return undefined;
    let stopped = false;
    let handle;
    let attempt = 0;
    // Retry quickly until the first successful load (so a flaky connection doesn't leave every channel on
    // "Updating…" for a full minute), then settle into the slow 60s poll.
    const run = async () => {
      const ok = await load();
      if (stopped) return;
      if (ok) {
        handle = setInterval(load, 60_000);
      } else {
        attempt += 1;
        handle = setTimeout(run, Math.min(2_000 * 2 ** (attempt - 1), 15_000)); // 2s,4s,8s,15s…
      }
    };
    run();
    return () => { stopped = true; clearTimeout(handle); clearInterval(handle); };
  }, [open, load]);

  // A wider horizon (› toward the edge) re-fetches at once rather than waiting for the 60 s poll.
  useEffect(() => {
    if (!open || hours === HOURS_DEFAULT) return;
    load();
  }, [open, hours, load]);

  // Advance the now line + airing shading without re-fetching.
  useEffect(() => {
    if (!open) return undefined;
    setNowMs(Date.now() + skewRef.current);
    const t = setInterval(() => setNowMs(Date.now() + skewRef.current), 15_000);
    return () => clearInterval(t);
  }, [open, lineup]);

  // Esc closes; trap it here so it doesn't reach the page's channel hotkeys.
  useEffect(() => {
    if (!open) return undefined;
    const onKey = (e) => {
      if (e.key === "Escape") {
        e.stopPropagation();
        onClose();
      }
    };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
  }, [open, onClose]);

  // Publish the horizontal scroll as a CSS variable (rAF-throttled) so clipped blocks can keep their
  // text at the visible edge — see .epg-prog's padding-left in the CSS.
  useEffect(() => {
    if (!open) return undefined;
    const el = scrollRef.current;
    if (!el) return undefined;
    let frame = 0;
    let last = -1;
    const publish = () => {
      frame = 0;
      const x = el.scrollLeft;
      if (x === last) return;
      last = x;
      el.style.setProperty("--epg-scrollx", `${x}px`);
    };
    const onScroll = () => { if (!frame) frame = requestAnimationFrame(publish); };
    publish();
    el.addEventListener("scroll", onScroll, { passive: true });
    return () => { el.removeEventListener("scroll", onScroll); if (frame) cancelAnimationFrame(frame); };
  }, [open, lineup]);

  // The time window: floor to the previous half hour for clean tick labels, and run out to the
  // fetched horizon so no program is clipped on the right.
  const win = useMemo(() => {
    const hrs = lineup?.hours || HOURS_DEFAULT;
    const lookbackMin = lineup?.lookbackMinutes ?? 30;
    const serverNow = lineup ? Date.parse(lineup.serverNowUtc) : Date.now() + skewRef.current;
    const floor = new Date(serverNow);
    floor.setMinutes(floor.getMinutes() < 30 ? 0 : 30, 0, 0);
    // Never open the window earlier than the lineup we actually fetched. The strip left of "now" has to
    // be backed by programs on every channel — where it isn't, only the rows whose current program began
    // before the window fill to the edge and the rest start at a ragged x (the growing black gap).
    const startMs = Math.max(floor.getTime(), serverNow - lookbackMin * MS_PER_MIN);
    const endMs = serverNow + hrs * 60 * MS_PER_MIN;
    const totalMin = (endMs - startMs) / MS_PER_MIN;
    // Ticks sit on real clock half hours rather than at multiples of the window start, so the labels stay
    // ":00 / :30" even when the clamp above pulls the window start off the half hour.
    const ticks = [];
    const firstTick = new Date(startMs);
    firstTick.setSeconds(0, 0);
    firstTick.setMinutes(firstTick.getMinutes() <= 0 ? 0 : firstTick.getMinutes() <= 30 ? 30 : 60);
    for (let ms = firstTick.getTime(); ms <= endMs; ms += 30 * MS_PER_MIN) {
      ticks.push({ pct: ((ms - startMs) / MS_PER_MIN / totalMin) * 100, label: clockLabel(ms) });
    }
    return { startMs, endMs, totalMin, ticks };
  }, [lineup]);

  // ms → % across the window (clamped to the visible range).
  const pct = useCallback(
    (ms) => Math.min(Math.max(((ms - win.startMs) / (win.endMs - win.startMs)) * 100, 0), 100),
    [win]
  );

  const nowPct = pct(nowMs);

  // No first-paint scroll nudge. The window opens at most 30 min before now (the previous half hour),
  // so the now line is never more than ~270px into the track and everything is in view at scrollLeft 0.
  // The nudge this replaced added the track's own offsetLeft — which IS the sticky column's width — so it
  // scrolled the window's start under the column: the now line vanished and every live block's title
  // ("‹ Green Mile, The") sat hidden behind the channel cell (2026-09-05).

  // Pixels per minute as actually laid out (the CSS var changes per breakpoint).
  const ppm = useCallback(() => {
    const axis = axisRef.current;
    const v = axis && win.totalMin ? axis.offsetWidth / win.totalMin : NaN;
    return Number.isFinite(v) && v > 0 ? v : 9;
  }, [win.totalMin]);

  const scrollTo = useCallback((left, smooth = true) => {
    const el = scrollRef.current;
    if (!el) return;
    const x = Math.max(0, left);
    if (typeof el.scrollTo === "function") el.scrollTo({ left: x, behavior: smooth ? "smooth" : "auto" });
    else el.scrollLeft = x;
  }, []);

  // ‹ / › step the timeline by three hours; › near the fetched horizon widens the fetch by six hours.
  const stepTimeline = useCallback((dir) => {
    const el = scrollRef.current;
    if (!el) return;
    const step = NAV_STEP_MIN * ppm();
    const target = el.scrollLeft + dir * step;
    scrollTo(target);
    if (dir > 0 && target + el.clientWidth >= el.scrollWidth - step && hoursRef.current < HOURS_MAX) {
      setHours((h) => Math.min(HOURS_MAX, h + HOURS_STEP));
    }
  }, [ppm, scrollTo]);
  const jumpToNow = useCallback(() => scrollTo(0), [scrollTo]);

  // The guide's filter (guideModel.rowMatches — shared with the page, which auto-selects the first
  // VISIBLE row's programme). `idx` is taken BEFORE filtering: it is the channel NUMBER, which has to
  // keep matching the 1-9 tune hotkeys.
  const numbered = useMemo(() => {
    const all = channels.map((ch, idx) => ({ ch, idx }));
    const needle = query.trim().toLowerCase();
    if (!needle && !favoriteIds) return all;
    return all.filter(({ ch }) => rowMatches(ch, lineup?.byId.get(ch.id)?.items, needle, favoriteIds));
  }, [channels, query, favoriteIds, lineup]);

  // Arrow-key navigation (page mode). The selection is the caller's (`selectedKey`); we only propose
  // the next cell through onPickProgram, so the page keeps one source of truth.
  useEffect(() => {
    if (!open || !onPickProgram || !lineup) return undefined;
    const onKey = (e) => {
      if (typingTarget(e) || e.altKey || e.ctrlKey || e.metaKey) return;
      const keys = ["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Enter"];
      if (!keys.includes(e.key)) return;
      const rows = numbered.filter(({ ch }) => lineup.byId.get(ch.id)?.items?.length);
      if (rows.length === 0) return;
      let rowIdx = -1;
      let prog = null;
      if (selectedKey) {
        const sep = selectedKey.indexOf(":");
        const chId = Number(selectedKey.slice(0, sep));
        const startUtc = selectedKey.slice(sep + 1);
        rowIdx = rows.findIndex(({ ch }) => Number(ch.id) === chId);
        if (rowIdx >= 0) prog = lineup.byId.get(rows[rowIdx].ch.id).items.find((p) => p.startUtc === startUtc) || null;
      }
      const pick = (r, p) => {
        const items = lineup.byId.get(r.ch.id).items;
        if (p) onPickProgram(r.ch, p, items);
      };
      const at = (items, t) => items.find((p) => Date.parse(p.startUtc) <= t && t < Date.parse(p.endUtc)) || items.find((p) => Date.parse(p.endUtc) > t) || items[items.length - 1];
      e.preventDefault();
      if (rowIdx < 0 || !prog) {
        // Nothing selected: any of the keys lands on the first row's current programme.
        pick(rows[0], nowPlaying(lineup.byId.get(rows[0].ch.id).items, nowMs));
        return;
      }
      const items = lineup.byId.get(rows[rowIdx].ch.id).items;
      const i = items.indexOf(prog);
      if (e.key === "Enter") onPick(rows[rowIdx].ch);
      else if (e.key === "ArrowRight") { if (i < items.length - 1) pick(rows[rowIdx], items[i + 1]); }
      else if (e.key === "ArrowLeft") { if (i > 0) pick(rows[rowIdx], items[i - 1]); }
      else {
        const next = rows[rowIdx + (e.key === "ArrowDown" ? 1 : -1)];
        if (!next) return;
        // The same moment on the other row: now for a live programme, else the selected one's start.
        const live = Date.parse(prog.startUtc) <= nowMs && nowMs < Date.parse(prog.endUtc);
        const t = live ? nowMs : Math.max(Date.parse(prog.startUtc), win.startMs);
        pick(next, at(lineup.byId.get(next.ch.id).items, t));
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onPickProgram, onPick, lineup, numbered, selectedKey, nowMs, win.startMs]);

  // Keep the selected cell in view, clear of the sticky column (a keyboard move can land off-screen).
  useEffect(() => {
    if (!open || !selectedKey) return undefined;
    const frame = requestAnimationFrame(() => {
      const el = scrollRef.current;
      const cell = el?.querySelector('.epg-prog[aria-pressed="true"]');
      const chan = el?.querySelector(".epg-chan");
      if (!el || !cell || typeof cell.getBoundingClientRect !== "function") return;
      const r = cell.getBoundingClientRect();
      const s = el.getBoundingClientRect();
      if (!r.width || !s.width) return;
      const head = el.querySelector(".epg-timehead")?.offsetHeight || 0;
      if (r.top < s.top + head) el.scrollTop -= s.top + head - r.top + 8;
      else if (r.bottom > s.bottom) el.scrollTop += r.bottom - s.bottom + 8;
      const visibleLeft = s.left + (chan?.offsetWidth || 0);
      // Mostly hidden to the left, or off to the right: bring its start to the seam.
      if (r.right < visibleLeft + 120 || r.left > s.right - 120) scrollTo(el.scrollLeft + (r.left - visibleLeft) - 8, false);
    });
    return () => cancelAnimationFrame(frame);
  }, [open, selectedKey, scrollTo]);

  // Group channels by category so each shelf appears exactly once, even when a category's channels
  // aren't contiguous in sort order (e.g. a non-catalog channel wedged between them, or a sort-order
  // collision). Category order = first appearance; channel order within a category preserves the
  // incoming sort; the channel number stays its own position (so it still matches the tune hotkeys).
  const grouped = useMemo(() => {
    const order = [];
    const byCat = new Map();
    numbered.forEach(({ ch, idx }) => {
      const cat = ch.category || "Channels";
      if (!byCat.has(cat)) { byCat.set(cat, []); order.push(cat); }
      byCat.get(cat).push({ ch, idx });
    });
    const out = [];
    for (const cat of order) {
      out.push({ header: cat });
      for (const item of byCat.get(cat)) out.push(item);
    }
    return out;
  }, [numbered]);

  if (!open) return null;

  return (
    /* eslint-disable jsx-a11y/no-static-element-interactions, jsx-a11y/click-events-have-key-events */
    <div className="epg" role="dialog" aria-label="Channel guide">
      <div className="epg-head">
        <span className="epg-title">Channel Guide</span>
        <span className="epg-clock">{new Date(nowMs).toLocaleDateString([], { weekday: "short", month: "short", day: "numeric" })} · {clockLabel(nowMs)}</span>
        <button className="epg-close" onClick={onClose} aria-label="Close guide">×</button>
      </div>

      <div className="epg-scroll" ref={scrollRef} style={{ "--epg-totalmin": win.totalMin }}>
        <div className="epg-body">
          {/* time axis */}
          <div className="epg-timehead">
            <div className="epg-corner">
              <span className="epg-corner-label">Channel</span>
              <span className="epg-nav" role="group" aria-label="Move the timeline">
                <button type="button" className="epg-nav-btn" onClick={() => stepTimeline(-1)} aria-label="Earlier" title="Earlier (3 h)">‹</button>
                <button type="button" className="epg-nav-btn epg-nav-btn--now" onClick={jumpToNow} title="Back to now">Now</button>
                <button type="button" className="epg-nav-btn" onClick={() => stepTimeline(1)} aria-label="Later" title="Later (3 h)">›</button>
              </span>
            </div>
            <div className="epg-axis" ref={axisRef}>
              {win.ticks.map((t, i) => (
                <div key={i} className="epg-tick" style={{ left: `${t.pct}%` }}>
                  <span className="epg-tick-label">{t.label}</span>
                </div>
              ))}
              <div className="epg-nowflag" style={{ left: `${nowPct}%` }} aria-hidden="true">
                <span className="epg-nowflag-time">{clockLabel(nowMs)}</span>
              </div>
            </div>
          </div>

          {channels.length > 0 && numbered.length === 0 && (
            <div className="epg-nomatch">
              {favoriteIds && favoriteIds.size === 0
                ? "No favourite channels yet — open a show and use ♡ Favourite channel to add one."
                : "No channel or programme matches that."}
            </div>
          )}

          {/* one row per channel, grouped by category (rows numbered by position) */}
          {grouped.map((g) => {
            if (g.header)
              return (
                <div key={`h-${g.header}`} className="epg-group"><span className="epg-group-label">{g.header}</span></div>
              );
            const { ch, idx } = g;
            const row = lineup?.byId.get(ch.id);
            const isCurrent = ch.id === currentChannelId;
            // The truly-airing program (a long movie that started before the window has its block
            // scrolled off the left, so this is the only place its title/plot stays readable). It also
            // supplies the poster/kind.
            const np = nowPlaying(row?.items, nowMs);
            return (
              <div key={ch.id} className={`epg-row${isCurrent ? " epg-row--current" : ""}`}>
                <button className="epg-chan" onClick={() => onPick(ch)} title={np ? `${ch.name} — now: ${np.title}` : `Watch ${ch.name}`}>
                  {np?.posterId ? (
                    <FallbackImage
                      className="epg-chan-poster"
                      src={MovieAPI.getPosterThumbnail(np.posterId, np.posterVersion, np.kind)}
                      alt=""
                      loading="lazy"
                      decoding="async"
                      fallback={<span className="epg-chan-poster epg-chan-poster--blank" aria-hidden="true" />}
                    />
                  ) : (
                    <span className="epg-chan-poster epg-chan-poster--blank" aria-hidden="true" />
                  )}
                  <span className="epg-chan-body">
                    <span className="epg-chan-id">
                      <span className="epg-chan-num">{idx + 1}</span>
                      <span className="epg-chan-name">{ch.name}</span>
                      {row?.viewers > 0 && <span className="epg-chan-viewers">👁 {row.viewers}</span>}
                      {row?.paused && <span className="epg-chan-paused" title="Paused">❚❚</span>}
                    </span>
                    {np ? (
                      <span className="epg-chan-now">
                        <span className="epg-chan-now-title">{np.title}</span>
                      </span>
                    ) : (
                      <span className="epg-chan-now epg-chan-now--off">Off air</span>
                    )}
                  </span>
                </button>

                <div className="epg-track">
                  <div className="epg-nowline" style={{ left: `${nowPct}%` }} aria-hidden="true" />

                  {row == null && <div className="epg-filler">Updating…</div>}
                  {row != null && row.items.length === 0 && <div className="epg-filler">Off air</div>}

                  {row?.items.map((prog, i) => {
                    const startMs = Date.parse(prog.startUtc);
                    const endMs = Date.parse(prog.endUtc);
                    const left = pct(startMs);
                    const width = pct(endMs) - left;
                    if (width <= 0) return null;
                    const live = startMs <= nowMs && nowMs < endMs;
                    // Shade against the *drawn* span, not the program's true one — a block that began
                    // before the window is clipped at the left edge, so its real start would overstate
                    // how far the bar has filled and push the shading past the now line.
                    const drawnStart = Math.max(startMs, win.startMs);
                    const drawnEnd = Math.min(endMs, win.endMs);
                    const elapsedPct = live ? ((nowMs - drawnStart) / (drawnEnd - drawnStart)) * 100 : 0;
                    const clipped = startMs < win.startMs;
                    const meta = programMeta(prog);
                    // The block's drawn start and width in MINUTES — the CSS turns them into px with
                    // --epg-ppm and pads the text past whatever the sticky column hides (--epg-scrollx).
                    const drawnLeftMin = (drawnStart - win.startMs) / MS_PER_MIN;
                    const drawnWidthMin = (drawnEnd - drawnStart) / MS_PER_MIN;
                    return (
                      <button
                        key={i}
                        className={`epg-prog${live ? " epg-prog--live" : ""}${endMs <= nowMs ? " epg-prog--past" : ""}${clipped ? " epg-prog--clipped" : ""}`}
                        style={{ left: `${left}%`, width: `${width}%`, "--epg-l": drawnLeftMin, "--epg-w": drawnWidthMin }}
                        aria-pressed={selectedKey != null && selectedKey === `${ch.id}:${prog.startUtc}` ? true : undefined}
                        onClick={() => (onPickProgram ? onPickProgram(ch, prog, row.items) : onPick(ch))}
                        title={`${prog.title} · ${clockLabel(startMs)}–${clockLabel(endMs)}`}
                      >
                        {live && <span className="epg-prog-elapsed" style={{ width: `${elapsedPct}%` }} aria-hidden="true" />}
                        {/* Headline + start time on one line; the meta line beneath. An episode is headlined by
                            its series — the S/E and episode title are the meta's lead, as on a real guide. */}
                        <span className="epg-prog-head">
                          {clipped && <span className="epg-prog-clip" aria-hidden="true">‹</span>}
                          <span className="epg-prog-title">{programHeadline(prog)}</span>
                          <span className="epg-prog-time">{clockLabel(startMs)}</span>
                        </span>
                        {meta && <span className="epg-prog-meta">{meta}</span>}
                      </button>
                    );
                  })}
                </div>
              </div>
            );
          })}

          {channels.length === 0 && <div className="epg-empty">No channels are broadcasting.</div>}
        </div>
      </div>
    </div>
  );
}

export default ChannelGrid;
