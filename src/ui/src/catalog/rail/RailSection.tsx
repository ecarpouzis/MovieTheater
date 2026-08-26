/** A collapsible rail section: a title, the active count as a badge, a carat. Opens by default when it has an active filter. */
import { useState, type ReactNode } from "react";

export interface RailSectionProps {
  title: string;
  count?: number;
  defaultOpen?: boolean;
  children: ReactNode;
}

export default function RailSection({ title, count = 0, defaultOpen, children }: RailSectionProps) {
  const [open, setOpen] = useState(!!defaultOpen || count > 0);
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
