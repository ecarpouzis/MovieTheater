/**
 * The phone facet sheet's open state, the way every section runs it: opened by the bar's Filters
 * pill, closed by the sheet itself, by any URL change (a facet click pushed a new search — the
 * results are the answer) and by the viewport growing into the desktop rail. It also answers the
 * phone top bar's search button (`requestSectionSearch`), since the section's search lives here.
 */
import { useCallback, useEffect, useState } from "react";
import { useLocation } from "react-router-dom";
import useIsMobile from "../../hooks/useIsMobile";
import { DRAWER_EVENT, SEARCH_EVENT, isNavDrawerOpen, publishRailSheet } from "../bar/useSlot";

export default function useRailSheet(): { open: boolean; show: () => void; hide: () => void; isMobile: boolean } {
  const isMobile = useIsMobile();
  const location = useLocation();
  const [open, setOpen] = useState(false);
  useEffect(() => { if (!isMobile) setOpen(false); }, [isMobile]);
  useEffect(() => { setOpen(false); }, [location.search]);
  // The nav drawer is the sider and carries the SAME rail — never both at once. (The drawer no
  // longer closes on a search change, so without this a facet clicked in the drawer would leave the
  // sheet stacked behind it.)
  useEffect(() => {
    const onDrawer = () => { if (isNavDrawerOpen()) setOpen(false); };
    window.addEventListener(DRAWER_EVENT, onDrawer);
    return () => window.removeEventListener(DRAWER_EVENT, onDrawer);
  }, []);
  // …and tell the drawer when this sheet is up, so it closes itself.
  useEffect(() => { publishRailSheet(open); }, [open]);
  useEffect(() => () => publishRailSheet(false), []);
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
