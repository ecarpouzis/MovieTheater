/**
 * The phone facet sheet's open state, the way every section runs it: opened by the bar's Filters
 * pill, closed by the sheet itself, by any URL change (a facet click pushed a new search — the
 * results are the answer) and by the viewport growing into the desktop rail. It also answers the
 * phone top bar's search button (`requestSectionSearch`), since the section's search lives here.
 */
import { useCallback, useEffect, useState } from "react";
import { useLocation } from "react-router-dom";
import useIsMobile from "../../hooks/useIsMobile";
import { SEARCH_EVENT } from "../bar/useSlot";

export default function useRailSheet(): { open: boolean; show: () => void; hide: () => void; isMobile: boolean } {
  const isMobile = useIsMobile();
  const location = useLocation();
  const [open, setOpen] = useState(false);
  useEffect(() => { if (!isMobile) setOpen(false); }, [isMobile]);
  useEffect(() => { setOpen(false); }, [location.search]);
  // The phone top bar's search button: this sheet carries the section's search, so it takes the request.
  useEffect(() => {
    if (!isMobile) return undefined;
    const onSearch = (e: Event) => { e.preventDefault(); setOpen(true); };
    window.addEventListener(SEARCH_EVENT, onSearch);
    return () => window.removeEventListener(SEARCH_EVENT, onSearch);
  }, [isMobile]);
  const show = useCallback(() => setOpen(true), []);
  const hide = useCallback(() => setOpen(false), []);
  return { open, show, hide, isMobile };
}
