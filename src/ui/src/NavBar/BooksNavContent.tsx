/**
 * The Books rail: the user block, then the section's counted index (Explore · Browse · Novels · Kids /
 * Shelf / Admin), gated the way the pages are — a kid account sees Kids + Shelf, a non-member sees
 * nothing. The facet rail (S2) mounts below this on /books.
 */
import { useLocation } from "react-router-dom";
import SectionIndexRail from "../catalog/rail/SectionIndexRail";
import useBooksIndex from "../hooks/useBooksIndex";
import { booksNavGroups, booksSection, type BooksMe } from "../Pages/Books/booksNav";
import { NavUserBlock } from "./navShared";

interface BooksNavContentProps {
  userData: (BooksMe & Record<string, unknown>) | null | undefined;
  onUserLoggedIn: (...args: unknown[]) => void;
  setSettingsModalOpen: (open: boolean) => void;
  setAdminModalOpen: (open: boolean) => void;
}

export default function BooksNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, setAdminModalOpen }: BooksNavContentProps) {
  const location = useLocation();
  const counts = useBooksIndex(userData);
  const groups = booksNavGroups(userData, counts);
  return (
    <>
      <NavUserBlock userData={userData} onUserLoggedIn={onUserLoggedIn} setSettingsModalOpen={setSettingsModalOpen} setAdminModalOpen={setAdminModalOpen} />
      <SectionIndexRail groups={groups} activeKey={booksSection(location.pathname)} ariaLabel="Books sections" />
      <div id="books-rail-slot" />
    </>
  );
}
