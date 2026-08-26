/**
 * The kids "Browse all": the catalog's Shelves (and Extended) over the kids source, inside the
 * kid-skinned case — the same engine the grown-up browse uses, so a big kid library streams and jumps
 * by letter. A shelf header opens that series' single shelf (`?series=`); a cover opens the item.
 */
import { useMemo } from "react";
import CatalogHost from "../../catalog/CatalogHost";
import { createKidsSource } from "../../catalog/sources/kidsSource";
import type { CardGroup, CardItem } from "../../catalog/types";
import { useMediaToken } from "./booksMedia";

export interface KidsBrowseProps {
  epoch?: number;
  onOpen: (item: CardItem) => void;
  onOpenShelf: (seriesId: number) => void;
}

export default function KidsBrowse({ epoch = 0, onOpen, onOpenShelf }: KidsBrowseProps) {
  const { epoch: mediaEpoch } = useMediaToken();
  const source = useMemo(() => createKidsSource({
    epoch,
    mediaEpoch,
    onOpen,
    onOpenGroup: (group: CardGroup) => { if (/^\d+$/.test(group.key)) onOpenShelf(Number(group.key)); },
  }), [epoch, mediaEpoch, onOpen, onOpenShelf]);
  return (
    <div className="kids-browse">
      <div className="kb-case">
        <CatalogHost section="books-kids" source={source} />
      </div>
    </div>
  );
}
