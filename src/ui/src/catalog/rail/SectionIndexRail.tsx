/**
 * A section's counted index in the left rail — the photos rail's shape, generalized: groups with a
 * heading, rows that are real URLs, a count in tabular mono, a "waiting on you" tint. The section
 * hands in the groups (`booksNavGroups`, `photosNavGroups`) and which key is active; this only draws.
 * Class prefix defaults to `navbar-index` (NavBar.css); photos keeps `navbar-photos` until R9.
 */
import { useHistory } from "react-router-dom";

export interface IndexView { key: string; label: string; path: string; count?: number | null; waiting?: boolean }
export interface IndexGroup { key: string; label?: string; views: IndexView[] }

export interface SectionIndexRailProps {
  groups: IndexGroup[];
  activeKey: string;
  ariaLabel: string;
  classPrefix?: string;
  onNavigate?: (path: string) => void;
}

export default function SectionIndexRail({ groups, activeKey, ariaLabel, classPrefix = "navbar-index", onNavigate }: SectionIndexRailProps) {
  const history = useHistory();
  const go = onNavigate ?? ((path: string) => history.push(path));
  if (groups.length === 0) return null;
  return (
    <nav className={`${classPrefix}-nav`} aria-label={ariaLabel}>
      {groups.map((group) => (
        <div className={`${classPrefix}-group`} key={group.key}>
          {group.label && <span className={`${classPrefix}-heading`}>{group.label}</span>}
          {group.views.map((view) => (
            <button
              key={view.key}
              type="button"
              className={`${classPrefix}-link${activeKey === view.key ? " is-active" : ""}`}
              aria-current={activeKey === view.key ? "page" : undefined}
              onClick={() => go(view.path)}
            >
              <span className={`${classPrefix}-link-label`}>{view.label}</span>
              {view.count != null && (
                <span className={`${classPrefix}-count${view.waiting ? " is-waiting" : ""}`}>{view.count.toLocaleString()}</span>
              )}
            </button>
          ))}
        </div>
      ))}
    </nav>
  );
}

/** The phone form: one flat strip, no headings (a section's top tab row). */
export function SectionIndexTabs({ views, activeKey, ariaLabel, className = "photos-tabs", onNavigate }: {
  views: IndexView[]; activeKey: string; ariaLabel: string; className?: string; onNavigate?: (path: string) => void;
}) {
  const history = useHistory();
  const go = onNavigate ?? ((path: string) => history.push(path));
  if (views.length === 0) return null;
  return (
    <nav className={className} aria-label={ariaLabel}>
      {views.map((view) => (
        <button
          key={view.key}
          type="button"
          className={`${className.replace(/s$/, "")}${activeKey === view.key ? " is-active" : ""}`}
          aria-current={activeKey === view.key ? "page" : undefined}
          onClick={() => go(view.path)}
        >
          {view.label}
          {view.count != null && <span className={`${className.replace(/s$/, "")}-count`}>{view.count.toLocaleString()}</span>}
        </button>
      ))}
    </nav>
  );
}
