/**
 * The library togglers as they appear in both readers' menus — exactly TWO (favourites was removed
 * from the product). One component because the two readers once carried byte-identical copies that
 * drifted. The reading POSITION is never changed by these; a book becomes Read only via the
 * explicit "Mark read" toggle.
 */
const PLUS = "M12 5v14M5 12h14";
const CHECK = "M5 12.5l4.5 4.5L19 7";

function Icon({ d }: { d: string }) {
  return (
    <svg className="ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.9} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={d} />
    </svg>
  );
}

export interface LibraryPillsProps {
  itemId: number;
  isMarked?: boolean;
  isWantToRead?: boolean;
  onToggleMarked?: (id: number) => void;
  onToggleWantToRead?: (id: number) => void;
  /** Prefixes the test hooks, e.g. "epub" → epub-want / epub-marked. */
  testIdPrefix: string;
}

export default function LibraryPills({ itemId, isMarked = false, isWantToRead = false, onToggleMarked, onToggleWantToRead, testIdPrefix }: LibraryPillsProps) {
  return (
    <div>
      <div className="rmx-label">Your library</div>
      <div className="rmx-libgrid">
        <button type="button" className={`rmx-lib${isWantToRead ? " on" : ""}`} onClick={() => onToggleWantToRead?.(itemId)} data-testid={`${testIdPrefix}-want`} data-reader-control>
          <Icon d={isWantToRead ? CHECK : PLUS} />
          <span>{isWantToRead ? "On your list" : "Want to read"}</span>
        </button>
        <button type="button" className={`rmx-lib${isMarked ? " on" : ""}`} onClick={() => onToggleMarked?.(itemId)} data-testid={`${testIdPrefix}-marked`} data-reader-control>
          <Icon d={CHECK} />
          <span>{isMarked ? "Read" : "Mark read"}</span>
        </button>
      </div>
    </div>
  );
}
