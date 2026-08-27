/**
 * The Books rail: the user block, then the section's counted index (Explore · Browse · Novels · Kids /
 * Shelf / Admin), gated the way the pages are — a kid account sees Kids + Shelf, a non-member sees
 * nothing. On the browse, the filter rail hangs beneath the index — in the phone DRAWER too, since
 * the drawer is the sider (2026-08-27); the bar's Filters pill still raises the page's sheet as the
 * quick path.
 */
import { useLocation } from "react-router-dom";
import SectionIndexRail from "../catalog/rail/SectionIndexRail";
import useBooksIndex from "../hooks/useBooksIndex";
import { booksNavGroups, booksSection, isKidAccount, type BooksMe } from "../Pages/Books/booksNav";
import BooksSiderRail from "../Pages/Books/BooksSiderRail";
import NovelsSiderRail from "../Pages/Books/NovelsSiderRail";
import { NavUserBlock } from "./navShared";

interface BooksNavContentProps {
  userData: (BooksMe & Record<string, unknown>) | null | undefined;
  onUserLoggedIn: (...args: unknown[]) => void;
  setSettingsModalOpen: (open: boolean) => void;
  /** NavBar's: true on desktop, true on a phone only while the drawer (the sider) is open. */
  railVisible?: boolean;
}

export default function BooksNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, railVisible = true }: BooksNavContentProps) {
  const location = useLocation();
  const counts = useBooksIndex(userData);
  const groups = booksNavGroups(userData, counts);
  const section = booksSection(location.pathname);
  const member = !!userData?.booksAccess && !!userData?.hasPassword;
  const railable = member && railVisible && !isKidAccount(userData);
  const username = String(userData?.username ?? "");
  return (
    <>
      <NavUserBlock userData={userData} onUserLoggedIn={onUserLoggedIn} setSettingsModalOpen={setSettingsModalOpen} />
      <SectionIndexRail groups={groups} activeKey={section} ariaLabel="Books sections" />
      {railable && section === "browse" && <BooksSiderRail username={username} />}
      {railable && section === "novels" && <NovelsSiderRail username={username} />}
    </>
  );
}
