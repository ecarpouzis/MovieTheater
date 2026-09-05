/**
 * A collapsible rail section: a title, the active count as a badge, a carat. COLLAPSED by default,
 * every section, every site section (Eric, 2026-09-05: phones and tablets are first-class, and an
 * open Genre list is most of a phone's drawer) — an active filter shows as the badge, not as an open
 * body. The reader's toggle holds while the section stays mounted. `defaultOpen` is accepted for
 * compatibility and ignored.
 */
import { useState, type ReactNode } from "react";

export interface RailSectionProps {
  title: string;
  count?: number;
  /** @deprecated ignored since 2026-09-05 — every section starts collapsed. */
  defaultOpen?: boolean;
  children: ReactNode;
}

export default function RailSection({ title, count = 0, children }: RailSectionProps) {
  const [open, setOpen] = useState(false);
  return (
    <div className={`bx-rsec${open ? " open" : ""}`}>
      <button type="button" className="bx-rsec-head" onClick={() => setOpen((o) => !o)} aria-expanded={open}>
        <span className="bx-rsec-title">{title}</span>
        {count > 0 && <span className="bx-rsec-badge">{count}</span>}
        <svg className="bx-rsec-carat" viewBox="0 0 10 6" width="10" height="6" fill="currentColor" aria-hidden="true"><path d="M0 0h10L5 6z" /></svg>
      </button>
      {open && <div className="bx-rsec-body">{children}</div>}
    </div>
  );
}
