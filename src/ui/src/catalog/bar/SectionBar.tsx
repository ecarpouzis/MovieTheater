/**
 * The ONE content-top bar every section shares (R9 S1, the Long Box's header brought over as the
 * hybrid navbar's top half): tabs on the left, the section's search in the middle, the command pills
 * + ⚙ Tweaks on the right, and the light/dark toggle at the end. The bar itself knows nothing about
 * catalogs — pages contribute their controls through the portal slots (`#section-bar-search`,
 * `#section-bar-tools`), exactly the Long Box's `#hdr-browse-tools` seam.
 *
 * Phones: the fixed 48 px top bar (NavBar) carries the GENERIC controls (search, ⚙, theme) in
 * `#topbar-tools`; this bar becomes one swipeable strip under it that is content navigation only —
 * tabs, then the section's pills. Nothing generic rides in the scroller (Eric, canvas 2026-08-27).
 */
import { useLayoutEffect, type ReactNode } from "react";
import { useHistory, useLocation } from "react-router-dom";
import useIsMobile from "../../hooks/useIsMobile";
import { barHidden, sectionFor, tabIsActive, tabsFor, type SectionUser } from "./sections";
import { announceSlots, BAR_SEARCH_SLOT, BAR_TOOLS_SLOT } from "./useSlot";
import "./bar.css";

export interface SectionBarProps {
  userData: SectionUser | null | undefined;
  theme: "light" | "dark" | string;
  toggleTheme: () => void;
}

export function ThemeButton({ theme, toggleTheme, className = "sbar-theme" }: { theme: string; toggleTheme: () => void; className?: string }) {
  const dark = theme === "dark";
  return (
    <button type="button" className={className} onClick={toggleTheme} title={dark ? "Switch to light theme" : "Switch to dark theme"} aria-label={dark ? "Switch to light theme" : "Switch to dark theme"}>
      {dark ? (
        <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z" /></svg>
      ) : (
        <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" aria-hidden="true"><circle cx="12" cy="12" r="4" /><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" /></svg>
      )}
    </button>
  );
}

export default function SectionBar({ userData, theme, toggleTheme }: SectionBarProps) {
  const location = useLocation();
  const history = useHistory();
  const isMobile = useIsMobile();
  const hidden = barHidden(location.pathname);
  const section = sectionFor(location.pathname);
  const tabs = tabsFor(section, userData);

  // The slots' identity changes with the layout; tell mounted consumers to re-target.
  useLayoutEffect(() => { announceSlots(); }, [isMobile, section.key, hidden]);

  if (hidden) return null;

  const tabNodes: ReactNode = tabs.map((t) => {
    const active = tabIsActive(t, location.pathname);
    return (
      <button
        key={t.key} type="button"
        className={`sbar-tab${active ? " on" : ""}${t.admin ? " sbar-tab--admin" : ""}`}
        aria-current={active ? "page" : undefined}
        onClick={() => { if (!active) history.push(t.path); }}
      >
        {t.label}
      </button>
    );
  });

  if (isMobile) {
    return (
      <div className="sbar sbar--phone" data-section={section.key} role="navigation" aria-label={`${section.title} sections`}>
        <div className="sbar-strip">
          <nav className="sbar-tabs">{tabNodes}</nav>
          <span className="sbar-divider" aria-hidden="true" />
          <span id={BAR_TOOLS_SLOT} className="sbar-slot" />
        </div>
      </div>
    );
  }

  return (
    <div className="sbar" data-section={section.key} role="navigation" aria-label={`${section.title} sections`}>
      <nav className="sbar-tabs">{tabNodes}</nav>
      <div id={BAR_SEARCH_SLOT} className="sbar-search" data-placeholder={section.searchPlaceholder} />
      <div className="sbar-tools">
        <span id={BAR_TOOLS_SLOT} className="sbar-slot" />
        <ThemeButton theme={theme} toggleTheme={toggleTheme} />
      </div>
    </div>
  );
}
