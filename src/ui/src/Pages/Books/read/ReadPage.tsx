/**
 * `/books/read/:itemId` — the reader route. Loads the item, picks the surface (`.epub` → the EPUB
 * reader, anything else → the canvas reader), owns the per-user state the menu shows (want /
 * read via the ONE progress API + the item marks) and the two exits: Close goes BACK when the
 * reader was pushed over a page (`state.from`), else replaces to the browse; the reading-order
 * hand-off REPLACES the route so Back still leaves the reader in one step.
 */
import { useQuery } from "@tanstack/react-query";
import { useCallback, useMemo } from "react";
import { useHistory, useLocation, useParams } from "react-router-dom";
import CardGridSkeleton from "../../../Components/CardGridSkeleton";
import { fetchItem } from "../booksApi";
import { readHref } from "../booksLinks";
import { currentMediaToken, fillPagesTemplate, pageUrl, useMediaToken } from "../booksMedia";
import type { BooksMe } from "../booksNav";
import { isKidAccount } from "../booksNav";
import { bk } from "../booksQuery";
import { kidStyleOf } from "../KidsPage";
import useItemState from "../useItemState";
import EpubReaderView from "./EpubReaderView";
import ReaderView from "./ReaderView";
import useReadingPosition from "./useReadingPosition";
import "../css/books-reader.css";

export interface ReadPageProps {
  userData: BooksMe | null | undefined;
}

export function readerItemId(raw: string | undefined): number | null {
  if (!raw || !/^[0-9]+$/.test(raw)) return null;
  const n = Number(raw);
  return Number.isSafeInteger(n) && n > 0 ? n : null;
}

export default function ReadPage({ userData }: ReadPageProps) {
  const { itemId: rawId } = useParams<{ itemId: string }>();
  const itemId = readerItemId(rawId) ?? 0;
  const history = useHistory();
  const location = useLocation<{ from?: unknown } | undefined>();
  const from = location.state?.from;
  const { epoch: mediaEpoch } = useMediaToken();

  const detailQ = useQuery({
    queryKey: [...bk.item(itemId), "read", mediaEpoch],
    queryFn: ({ signal }) => fetchItem(itemId, currentMediaToken()?.token ?? null, signal),
    enabled: itemId > 0,
    staleTime: 10 * 60 * 1000,
  });
  const detail = detailQ.data ?? null;
  const pageCount = detail?.summary.pageCount ?? null;
  const state = useItemState(itemId > 0 ? itemId : null);
  const position = useReadingPosition(itemId, pageCount);

  const close = useCallback(() => {
    if (from) history.goBack();
    else history.replace("/books");
  }, [from, history]);
  const openItem = useCallback((nextId: number) => history.replace(readHref(nextId), location.state), [history, location.state]);

  const pageSrc = useCallback((page: number, maxWidth?: number) =>
    fillPagesTemplate(detail?.pagesUrlTemplate, page, maxWidth) ?? pageUrl(itemId, page, maxWidth) ?? "", [detail?.pagesUrlTemplate, itemId]);

  const toggleMarked = useCallback(() => { if (position.isFinished) void position.reset(); else void position.markFinished(); }, [position]);
  const toggleWant = useCallback(() => state.toggleWant(), [state]);

  const kidsStyle = useMemo(() => {
    const fromKids = typeof document !== "undefined" && document.documentElement.hasAttribute("data-kids-style");
    return isKidAccount(userData) || fromKids ? kidStyleOf(userData?.booksKidsStyle) : undefined;
  }, [userData]);

  const isEpub = (detail?.summary.extension ?? "").toLowerCase() === ".epub";

  return (
    <div className="books-reader" data-kids-style={kidsStyle} data-testid="books-reader">
      {itemId <= 0 || detailQ.isError ? (
        <div className="rdr-center">
          <div className="rdr-error rdr-error-box">
            <div className="rdr-error-t">This book is not available.</div>
            <button type="button" className="rmx-btn" style={{ marginTop: 12 }} onClick={close}>Back to the library</button>
          </div>
        </div>
      ) : !detail ? (
        <div className="rdr-loading"><CardGridSkeleton count={1} /></div>
      ) : isEpub ? (
        <EpubReaderView
          itemId={itemId}
          detail={detail}
          position={position}
          onClose={close}
          isMarked={state.isRead}
          isWantToRead={state.wantToRead}
          onToggleMarked={toggleMarked}
          onToggleWantToRead={toggleWant}
          kidsStyle={kidsStyle}
        />
      ) : (
        <ReaderView
          itemId={itemId}
          detail={detail}
          pageSrc={pageSrc}
          position={position}
          onClose={close}
          onOpenItem={openItem}
          isMarked={state.isRead}
          isWantToRead={state.wantToRead}
          onToggleMarked={toggleMarked}
          onToggleWantToRead={toggleWant}
          kidsStyle={kidsStyle}
        />
      )}
    </div>
  );
}
