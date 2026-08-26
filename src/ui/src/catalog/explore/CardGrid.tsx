import type { CardItem } from "../types";
import { ExploreCard } from "./CardRow";

/** The same card as the strip, wrapped into a grid — a rail whose `kind` is "grid". */
export default function CardGrid({ items, onOpen }: { items: CardItem[]; onOpen: (item: CardItem) => void }) {
  return (
    <div className="xp-grid">
      {items.map((it) => <ExploreCard key={it.key} item={it} coverH={184} onOpen={onOpen} />)}
    </div>
  );
}
