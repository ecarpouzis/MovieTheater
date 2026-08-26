import type { ReactNode } from "react";

/** A rail's header: the title, a mono subtitle, and an action slot (Shuffle, More →). */
export default function RowHead({ title, subtitle, action }: { title: string; subtitle?: string; action?: ReactNode }) {
  return (
    <header className="xp-row-head">
      <div className="xp-row-headtext">
        <h2 className="xp-row-title">{title}</h2>
        {subtitle && <div className="xp-row-sub">{subtitle}</div>}
      </div>
      {action && <div className="xp-row-actions">{action}</div>}
    </header>
  );
}
