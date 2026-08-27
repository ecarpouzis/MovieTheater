/**
 * `/books/novels` — the prose books: the three flat catalog views over the Novels source, scoped by
 * the Novels facet state in the URL. The rail lives in the section's sider (`NovelsSiderRail`) — and
 * on a phone that sider IS the nav drawer, the one place the filters live (2026-08-28: the page's
 * own Filters pill and full-page sheet offered the drawer's options a second time, and are gone).
 * The active chips sit over the results. A first landing with no filters of its own gets the
 * standalone's default chip — "not adult-romance" — which the reader can clear like any other.
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import CatalogHost from "../../catalog/CatalogHost";
import ActiveChips from "../../catalog/rail/ActiveChips";
import SmartSearch from "../../catalog/rail/SmartSearch";
import { BarSearchSlot } from "../../catalog/bar/BarSearch";
import { savableSearch, useSavedSearches } from "../../catalog/rail/savedSearches";
import { SaveSearchPrompt } from "../../catalog/rail/SavedSearchesRail";
import useFacetOptions from "../../catalog/rail/useFacetOptions";
import useFacetState from "../../catalog/rail/useFacetState";
import { createNovelsSource } from "../../catalog/sources/novelsSource";
import type { CardItem } from "../../catalog/types";
import useIsMobile from "../../hooks/useIsMobile";
import { useMediaToken } from "./booksMedia";
import { novelsFacetSpec } from "./novelsFacetSpec";
import { openEntity } from "./openEntity";
import { seededNovelsSearch, sessionStorageOrNull } from "./useNovelsBrowse";

export interface NovelsPageProps {
  username: string;
  epoch?: number;
}

export default function NovelsPage({ username, epoch = 0 }: NovelsPageProps) {
  const history = useHistory();
  const location = useLocation();
  const isMobile = useIsMobile();
  const spec = useMemo(() => novelsFacetSpec(username), [username]);
  const { state, actions, activeCount } = useFacetState(spec);
  const { epoch: mediaEpoch } = useMediaToken();
  const facets = useFacetOptions(spec);
  const saved = useSavedSearches("books-novels");
  const [savePrompt, setSavePrompt] = useState(false);

  // The default content exclusion, once per session, on a landing that carries no filters of its own.
  // Decided on the FIRST render so the host never pages the unseeded query; the replace lands next tick.
  const [seeded] = useState(() => seededNovelsSearch(location.search, spec, state, sessionStorageOrNull()));
  const [seeding, setSeeding] = useState(seeded != null);
  useEffect(() => {
    if (seeded != null) history.replace({ pathname: location.pathname, search: seeded, state: location.state });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(() => {
    // Once the seeded URL is the URL, the page is live — later changes (the reader clearing the chip) are theirs.
    if (seeding && location.search === seeded) setSeeding(false);
  }, [seeding, seeded, location.search]);

  const onOpen = useCallback((item: CardItem) => openEntity(history, location, { kind: "item", id: item.id }), [history, location]);
  const source = useMemo(
    () => createNovelsSource({ facetState: state, spec, epoch, mediaEpoch, onOpen }),
    [state, spec, epoch, mediaEpoch, onOpen],
  );
  const saveCurrent = (name: string) => { saved.save(name, savableSearch(location.search)); setSavePrompt(false); };

  // The page title is the bar's active tab and the count is on the rail's head line (R9 S1) — the
  // toolbar carries neither any more, and the bar carries no filter tool at all.
  const chips = (
    <div className="bx-rail-surface books-browse-chips">
      {savePrompt
        ? <SaveSearchPrompt onSave={saveCurrent} onCancel={() => setSavePrompt(false)} />
        : <ActiveChips spec={spec} state={state} actions={actions} facets={facets.data} onSave={activeCount > 0 ? () => setSavePrompt(true) : undefined} />}
    </div>
  );

  return (
    <div className="books-novels">
      {/* The SmartSearch lives in the SectionBar's centre slot on desktop (R9 S1d); on a phone the
          bar has no centre slot and the drawer's own rail carries it (`NovelsSiderRail`). */}
      {!isMobile && (
        <BarSearchSlot>
          <SmartSearch spec={spec} facets={facets.data} onAdd={actions.add} onText={actions.setText} placeholder="author:Le Guin, tag:space-opera…" />
        </BarSearchSlot>
      )}
      {!seeding && <CatalogHost section="books-novels" source={source} beforeResults={chips} />}
    </div>
  );
}
