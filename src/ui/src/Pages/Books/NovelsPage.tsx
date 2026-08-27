/**
 * `/books/novels` — the prose books: the three flat catalog views over the Novels source, scoped by
 * the Novels facet state in the URL. On desktop the rail lives in the section's sider
 * (`NovelsSiderRail`); on phones the page raises the full-page sheet behind a Filters pill. The count
 * and the active chips sit over the results. A first landing with no filters of its own gets the
 * standalone's default chip — "not adult-romance" — which the reader can clear like any other.
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import CatalogHost from "../../catalog/CatalogHost";
import ActiveChips from "../../catalog/rail/ActiveChips";
import FacetRail from "../../catalog/rail/FacetRail";
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
import { seededNovelsSearch, sessionStorageOrNull, useNovelsTotal } from "./useNovelsBrowse";

export interface NovelsPageProps {
  username: string;
  epoch?: number;
}

function FilterGlyph() {
  return (
    <svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true">
      <line x1="2" y1="4" x2="14" y2="4" /><line x1="2" y1="8" x2="14" y2="8" /><line x1="2" y1="12" x2="14" y2="12" />
      <circle cx="6" cy="4" r="1.7" fill="currentColor" stroke="none" /><circle cx="10" cy="8" r="1.7" fill="currentColor" stroke="none" /><circle cx="5" cy="12" r="1.7" fill="currentColor" stroke="none" />
    </svg>
  );
}

export default function NovelsPage({ username, epoch = 0 }: NovelsPageProps) {
  const history = useHistory();
  const location = useLocation();
  const isMobile = useIsMobile();
  const spec = useMemo(() => novelsFacetSpec(username), [username]);
  const { state, actions, activeCount } = useFacetState(spec);
  const { epoch: mediaEpoch } = useMediaToken();
  const facets = useFacetOptions(spec);
  const total = useNovelsTotal(state);
  const saved = useSavedSearches("books-novels");
  const [sheetOpen, setSheetOpen] = useState(false);
  const [savePrompt, setSavePrompt] = useState(false);
  useEffect(() => { if (!isMobile) setSheetOpen(false); }, [isMobile]);
  useEffect(() => { setSheetOpen(false); }, [location.search]);

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

  // The bar's tools: the phone's Filters pill. The page title is the bar's active tab and the count
  // is on the rail's head line (R9 S1) — the toolbar carries neither any more.
  const barTools = isMobile ? (
    <button type="button" className="bx-filter-pill" onClick={() => setSheetOpen(true)} aria-label="Filters" title="Filters">
      <FilterGlyph />
      {activeCount > 0 && <span className="bx-tool-num">{activeCount}</span>}
    </button>
  ) : null;
  const chips = (
    <div className="bx-rail-surface books-browse-chips">
      {savePrompt
        ? <SaveSearchPrompt onSave={saveCurrent} onCancel={() => setSavePrompt(false)} />
        : <ActiveChips spec={spec} state={state} actions={actions} facets={facets.data} onSave={activeCount > 0 ? () => setSavePrompt(true) : undefined} />}
    </div>
  );

  return (
    <div className="books-novels">
      {/* The SmartSearch lives in the SectionBar's centre slot on desktop (R9 S1d); the phone's
          sheet keeps its own. */}
      {!isMobile && (
        <BarSearchSlot>
          <SmartSearch spec={spec} facets={facets.data} onAdd={actions.add} onText={actions.setText} placeholder="author:Le Guin, tag:space-opera…" />
        </BarSearchSlot>
      )}
      {isMobile && (
        <FacetRail
          variant="sheet"
          title="Novels"
          open={sheetOpen}
          onClose={() => setSheetOpen(false)}
          spec={spec}
          state={state}
          actions={actions}
          activeCount={activeCount}
          facets={facets.data}
          facetsLoading={facets.isLoading}
          total={total.data}
          grouped={false}
          saved={{ list: saved.list, onApply: actions.replaceSearch, onRemove: saved.remove, onSave: (name) => saved.save(name, savableSearch(location.search)) }}
        />
      )}
      {!seeding && <CatalogHost section="books-novels" source={source} tools={barTools} beforeResults={chips} />}
    </div>
  );
}
