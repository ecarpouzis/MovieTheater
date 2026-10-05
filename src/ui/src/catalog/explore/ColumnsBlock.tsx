import { useHistory } from "react-router-dom";
import CardImage from "../cards/CardImage";
import type { CardItem, ExploreColumn } from "../types";
import ScoreBadge from "./ScoreBadge";

/**
 * Three short lists side by side — the dense module. Each row is a small cover, the title, one line
 * of facts and the score; each list ends in a link to its whole browse. On a phone the lists become
 * a swipe of full-width panels instead of one very long column.
 */
export default function ColumnsBlock({ columns, onOpen }: { columns: ExploreColumn[]; onOpen: (item: CardItem) => void }) {
  const history = useHistory();
  return (
    <div className="xp-cols" style={{ "--xp-cols": columns.length } as React.CSSProperties}>
      {columns.map((col) => (
        <section key={col.key} className="xp-col" aria-label={col.title}>
          <h3 className="xp-col-title">{col.title}</h3>
          <ul className="xp-col-list">
            {col.items.slice(0, 6).map((it) => (
              <li key={it.key}>
                <button type="button" className="xp-col-row" onClick={() => onOpen(it)} aria-label={it.title}>
                  <span className="xp-col-art" style={{ aspectRatio: `${Math.max(0.6, Math.min(1, it.aspect || 0.66))}` }}>
                    <CardImage src={it.imageThumbUrl ?? it.imageUrl} hue={it.hue} />
                  </span>
                  <span className="xp-col-text">
                    <span className="xp-col-name">{it.title}</span>
                    {(it.subtitle || it.label) && (
                      <span className="xp-col-sub">{[it.subtitle, it.label].filter(Boolean).join(", ")}</span>
                    )}
                  </span>
                  <ScoreBadge score={it.rating} className="xp-col-score" />
                </button>
              </li>
            ))}
          </ul>
          {col.more && (
            <button type="button" className="xp-col-more" onClick={() => history.push(col.more!.href)}>See all</button>
          )}
        </section>
      ))}
    </div>
  );
}
