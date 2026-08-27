/**
 * The shared modal chrome for the item and series modals: the SITE's full-page sheet
 * (`Components/SheetModal.css`) at EVERY size — the hero-dialog rule the movie and game modals
 * follow, edge-to-edge, 100dvh, one ✕ chip, the body is the scroller, content on the readable
 * column. `books-modal.css` repeats the shell's sheet block unconditionally and re-points the site
 * surface tokens `sheet-modal--themed` paints from at the Books skin.
 *
 * It was the standalone's `.cm` box — a `min(1240px, 94vw)` card with its own radius, shadow and
 * pop animation — until R9 S4: "a critical failure … adapting the smaller modals Longbox had"
 * (Eric, canvas 2026-08-27). The `.cm` element survives ONLY as the token bridge the `.cm-*` rules
 * are written against; it is no longer a box, and there is no card mode to fall back into.
 *
 * The wrap carries the section skin as inline tokens so the portal, which renders outside the
 * section root, wears the same backdrop and type.
 */
import { Modal } from "antd";
import { useEffect, useState, type CSSProperties, type ReactNode } from "react";
import { useLocation } from "react-router-dom";
import { readTweaks, subscribeTweaks } from "../../catalog/tweaks/useTweaks";
import { SHEET_Z } from "../../Components/sheetModal";
import "../../Components/SheetModal.css";
import { booksSkinContext, booksThemeStyle, siteTheme } from "./booksTheme";

export interface BooksModalShellProps {
  open: boolean;
  onClose: () => void;
  ariaLabel: string;
  variant?: "book" | "series";
  kidsStyle?: string | null;
  children: ReactNode;
}

function useBooksSkinStyle(): CSSProperties {
  const location = useLocation();
  const [, setTick] = useState(0);
  useEffect(() => {
    const unsubs = ["books", "books-novels", "books-kids"].map((s) => subscribeTweaks(s, () => setTick((t) => t + 1)));
    return () => { for (const u of unsubs) u(); };
  }, []);
  const ctx = booksSkinContext(location.pathname, location.search);
  return booksThemeStyle(readTweaks(ctx.store).extras, siteTheme(), ctx.view) as CSSProperties;
}

export default function BooksModalShell({ open, onClose, ariaLabel, variant = "book", kidsStyle, children }: BooksModalShellProps) {
  const skin = useBooksSkinStyle();
  return (
    <Modal
      open={open}
      onCancel={onClose}
      footer={null}
      destroyOnHidden
      centered={false}
      // Above the site's fixed top bar (1300) and the facet rail's phone sheet (1350) — the one
      // number every site modal uses; antd's default 1000 slid the sheet under both.
      zIndex={SHEET_Z}
      // `sheet-modal` is the site's shell, `--themed` makes it paint from the section tokens
      // (which `books-modal.css` re-points at the Books skin), `books-modal` takes it the rest of
      // the way to a sheet at every size. No `closable={false}` and no hand-rolled `.cm-close`:
      // the shell's ✕ chip is the one every full-page dialog on this site wears.
      wrapClassName="sheet-modal sheet-modal--themed books-modal"
      rootClassName="books-modal-root"
      // The skin tokens ride `styles.wrapper`, which the dialog MERGES into the wrap's own inline style.
      // They used to ride `wrapProps.style`, which the dialog spreads AFTER its own props — so the wrap
      // lost its inline `zIndex` (and its display toggle) and the mask, still at 1500, painted OVER the
      // modal: "click a book and the whole screen blurs with the modal behind it" (Eric, 2026-08-26).
      wrapProps={{ "data-kids-style": kidsStyle ?? undefined }}
      styles={{ wrapper: skin }}
    >
      <div className={`cm cm--${variant}`} role="dialog" aria-label={ariaLabel}>
        {children}
      </div>
    </Modal>
  );
}

/** Inline icons the modals share (the standalone's). */
export const ICON = {
  book: "M4 5a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H8a4 4 0 0 1-4-4V5z M8 3v14",
  check: "M4 12l5 5L20 6",
  plus: "M12 5v14M5 12h14",
  bookmark: "M6 4h12v16l-6-4-6 4z",
  folder: "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z",
  grid: "M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z",
  layers: "M12 2l9 5-9 5-9-5 9-5zM3 12l9 5 9-5M3 17l9 5 9-5",
};

export function Icon({ d, fill }: { d: string; fill?: boolean }) {
  return (
    <svg viewBox="0 0 24 24" width="16" height="16" fill={fill ? "currentColor" : "none"} stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={d} />
    </svg>
  );
}

export function MagnifierIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" aria-hidden="true">
      <circle cx="7" cy="7" r="4.5" />
      <line x1="10.5" y1="10.5" x2="14" y2="14" />
    </svg>
  );
}
