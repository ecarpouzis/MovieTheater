import { useHistory } from "react-router-dom";
import type { ExploreRail, CardItem } from "../types";
import { ExploreCard } from "./CardRow";
import ScrollRow from "./ScrollRow";

/**
 * One name, looked at closely: a plate carrying the name large (a director, a franchise, a console,
 * an artist, a designer) and how much of it the library holds, beside a row of the works themselves.
 * The plate is the link to the whole set; each cover opens its own sheet.
 */
export default function FocusBlock({ rail, onOpen, action }: { rail: ExploreRail; onOpen: (item: CardItem) => void; action?: React.ReactNode }) {
  const history = useHistory();
  const f = rail.focus!;
  const href = f.href ?? rail.more?.href;
  return (
    <div className="xp-focus">
      <div className="xp-focus-plate">
        <div className="xp-focus-kicker">{rail.title}</div>
        <h2 className="xp-focus-name">{f.name}</h2>
        {f.blurb && <p className="xp-focus-blurb">{f.blurb}</p>}
        <div className="xp-focus-foot">
          {href && (
            <button type="button" className="xp-focus-all" onClick={() => history.push(href)}>
              {f.count ? `See all ${f.count.toLocaleString()}` : "See all"}
            </button>
          )}
          {action}
        </div>
      </div>
      <ScrollRow className="xp-focus-works">
        {rail.items.map((it) => <ExploreCard key={it.key} item={it} coverH={196} onOpen={onOpen} />)}
      </ScrollRow>
    </div>
  );
}
