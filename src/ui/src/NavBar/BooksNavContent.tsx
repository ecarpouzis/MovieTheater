/**
 * The Books rail: the user block, then the section's counted index (Explore · Browse · Novels · Kids /
 * Shelf / Admin), gated the way the pages are — a kid account sees Kids + Shelf, a non-member sees
 * nothing. On the browse, the filter rail hangs beneath the index — on desktop only: the phone's
 * drawer closes on every URL change, so there the browse raises its own full-page sheet instead.
 */
import { useLocation } from "react-router-dom";
import SectionIndexRail from "../catalog/rail/SectionIndexRail";
import useBooksIndex from "../hooks/useBooksIndex";
import useIsMobile from "../hooks/useIsMobile";
import { booksNavGroups, booksSection, isKidAccount, type BooksMe } from "../Pages/Books/booksNav";
import BooksSiderRail from "../Pages/Books/BooksSiderRail";
import NovelsSiderRail from "../Pages/Books/NovelsSiderRail";
import { NavUserBlock } from "./navShared";

interface BooksNavContentProps {
  userData: (BooksMe & Record<string, unknown>) | null | undefined;
  onUserLoggedIn: (...args: unknown[]) => void;
  setSettingsModalOpen: (open: boolean) => void;
  setAdminModalOpen: (open: boolean) => void;
}

export default function BooksNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, setAdminModalOpen }: BooksNavContentProps) {
  const location = useLocation();
  const isMobile = useIsMobile();
  const counts = useBooksIndex(userData);
  const groups = booksNavGroups(userData, counts);
  const section = booksSection(location.pathname);
  const member = !!userData?.booksAccess && !!userData?.hasPassword;
  const railable = member && !isMobile && !isKidAccount(userData);
  const username = String(userData?.username ?? "");
  return (
    <>
      <NavUserBlock userData={userData} onUserLoggedIn={onUserLoggedIn} setSettingsModalOpen={setSettingsModalOpen} setAdminModalOpen={setAdminModalOpen} />
      <SectionIndexRail groups={groups} activeKey={section} ariaLabel="Books sections" />
      {railable && section === "browse" && <BooksSiderRail username={username} />}
      {railable && section === "novels" && <NovelsSiderRail username={username} />}
    </>
  );
}
