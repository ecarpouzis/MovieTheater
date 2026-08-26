/**
 * The item modal (`?item=<id>`): the standalone's ComicModal over the host's `ItemDetail`. Left: the
 * cover (the host's page-0 render, then the thumbnail, then a hue tile) and the actions. Right: the
 * kind row, the title, the series row (hidden for a one-issue series — the title already IS the
 * series), credits by role, the crossover event, the stats line, your rating, genre chips, the folder
 * row, the synopsis with its provenance, and the two related strips.
 *
 * Every link is a real URL: facet links start a fresh browse, the series link opens the series
 * modal (Back returns here), the folder link opens the Directory drilled in. A kid account gets the
 * same modal without the facet links.
 */
import { useQuery } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { hueSvg } from "../../catalog/cards/CardImage";
import { hueOf } from "../../catalog/sources/hue";
import { fetchCatalog, fetchGroupItems, fetchItem, type ItemDetail, type ItemSummary } from "./booksApi";
import { clampAspect, dateLabel, fileLabel, starsFromRating, synopsisFor } from "./booksFormat";
import { directoryHref, facetHref, readHref } from "./booksLinks";
import { fillPagesTemplate, thumbUrl, useMediaToken } from "./booksMedia";
import BooksModalShell, { ICON, Icon, MagnifierIcon } from "./BooksModalShell";
import { bk } from "./booksQuery";
import { closeEntity, openEntity } from "./openEntity";
import { RateTen, StarRating } from "./RatingStars";
import RelatedStrip from "./RelatedStrip";
import useItemState from "./useItemState";

const AUTHOR_ROLES = new Set(["writer", "author"]);
const ARTIST_ROLES = new Set(["penciller", "artist", "cover artist"]);

/** Credits by role, one row per person (the first leg that names them wins — the rows arrive ordered by source). */
export function creditNames(detail: ItemDetail, roles: Set<string>): string[] {
  const out: string[] = [];
  const seen = new Set<string>();
  for (const c of detail.credits) {
    if (!c.name || !c.role || !roles.has(c.role.toLowerCase())) continue;
    const k = c.name.trim().toLowerCase();
    if (seen.has(k)) continue;
    seen.add(k);
    out.push(c.name.trim());
  }
  return out;
}

/** Genre-ish chips: the item's genre/tag rows plus the series' tags, deduplicated. */
export function genreChips(detail: ItemDetail): string[] {
  const out: string[] = [];
  const seen = new Set<string>();
  for (const t of [...detail.tags, ...detail.seriesTags]) {
    if (!(t.category === "genre" || t.category === "tag")) continue;
    const k = t.value.trim().toLowerCase();
    if (!k || seen.has(k)) continue;
    seen.add(k);
    out.push(t.value.trim());
  }
  return out;
}

function ModalCover({ detail, hue }: { detail: ItemDetail; hue: number }) {
  const candidates = useMemo(() => [fillPagesTemplate(detail.pagesUrlTemplate, 0, 640), detail.thumbUrl ?? thumbUrl(detail.summary.id)].filter((u): u is string => !!u), [detail]);
  const [idx, setIdx] = useState(0);
  const src = candidates[idx] ?? hueSvg(hue, 260, 390);
  return <img src={src} alt={detail.summary.title ?? ""} loading="lazy" onError={() => setIdx((i) => i + 1)} />;
}

export interface ItemModalProps { itemId: number; isKid?: boolean }

export default function ItemModal({ itemId, isKid = false }: ItemModalProps) {
  const history = useHistory();
  const location = useLocation();
  const { token } = useMediaToken();
  const detailQuery = useQuery({ queryKey: [...bk.item(itemId), token?.token ?? ""], queryFn: () => fetchItem(itemId, token?.token), staleTime: 5 * 60 * 1000 });
  const state = useItemState(itemId);
  const detail = detailQuery.data ?? null;
  const summary = detail?.summary ?? null;

  const moreInSeries = useQuery({
    queryKey: [...bk.seriesGroup(summary?.seriesId ?? 0), "more", itemId],
    queryFn: () => fetchGroupItems("series", String(summary!.seriesId), { top: 9 }),
    enabled: !!summary?.seriesId && !summary?.isSingleIssueSeries,
    staleTime: 5 * 60 * 1000,
  });
  const authors = detail ? creditNames(detail, AUTHOR_ROLES) : [];
  const artists = detail ? creditNames(detail, ARTIST_ROLES) : [];
  const moreByAuthor = useQuery({
    queryKey: ["books", "more-by-author", authors[0] ?? "", itemId],
    queryFn: () => fetchCatalog({ kind: "comic", top: 9, exact: { author: [authors[0]] } }),
    enabled: authors.length > 0,
    staleTime: 5 * 60 * 1000,
  });

  const close = () => closeEntity(history, location);
  const go = (href: string) => history.push(href);
  const openItem = (item: ItemSummary) => openEntity(history, location, { kind: "item", id: item.id });
  const facet = (token: string, value: string | number) => (isKid ? undefined : () => go(facetHref(token, value)));

  const hue = hueOf(summary?.series ?? summary?.title ?? String(itemId));
  const title = summary?.title ?? "";
  const syn = detail ? synopsisFor(detail) : null;
  const chips = detail ? genreChips(detail) : [];
  const related = (moreInSeries.data?.items ?? []).filter((i) => i.id !== itemId).slice(0, 8);
  const byAuthor = (moreByAuthor.data?.items ?? []).filter((i) => i.id !== itemId && i.seriesId !== summary?.seriesId).slice(0, 8);
  const format = detail?.parsed?.formatRaw ?? detail?.parsed?.format ?? null;

  return (
    <BooksModalShell open onClose={close} ariaLabel={title || "Item"} variant="book">
      {detailQuery.isError || (!detailQuery.isLoading && !detail) ? (
        <div className="cm-loading">This item is not available.</div>
      ) : !detail || !summary ? (
        <div className="cm-loading">Loading…</div>
      ) : (
        <div className="cm-grid">
          <div className="cm-left">
            <div className="cm-cover" style={{ "--aspect": clampAspect(summary.coverAspect) } as React.CSSProperties}>
              <ModalCover detail={detail} hue={hue} />
            </div>
            <div className="cm-actions">
              <button type="button" className="cm-btn cm-btn-primary" onClick={() => go(readHref(itemId))}>
                <Icon d={ICON.book} /><span>Read now</span>
              </button>
              <button type="button" className={`cm-btn${state.wantToRead ? " on" : ""}`} onClick={state.toggleWant} data-testid="item-want">
                <Icon d={state.wantToRead ? ICON.check : ICON.plus} /><span>{state.wantToRead ? "On your list" : "Want to read"}</span>
              </button>
              <button type="button" className={`cm-btn${state.isRead ? " on" : ""}`} onClick={state.toggleRead} data-testid="item-marked">
                <Icon d={ICON.check} /><span>{state.isRead ? "Read" : "Mark read"}</span>
              </button>
            </div>
          </div>
          <div className="cm-right">
            <div className="cm-kindrow">
              <span className="cm-kind cm-kind-book"><Icon d={ICON.book} /> {format ?? (summary.kind === "book" ? "Book" : "Comic")}</span>
              {(summary.publisher || detail.topFolderName) && (
                <button type="button" className="cm-pub" onClick={summary.publisher ? facet("publisher", summary.publisher) : undefined} disabled={isKid || !summary.publisher}>
                  <span className="cm-pub-swatch" style={{ background: `oklch(0.74 0.15 ${hueOf(summary.publisher ?? detail.topFolderName ?? "")})` }} />
                  {summary.publisher ?? detail.topFolderName}
                </button>
              )}
            </div>
            <h2 className="cm-title">{title}</h2>

            {summary.series && summary.seriesId != null && !summary.isSingleIssueSeries && (
              <div className="cm-series">
                <span className="cm-k">Series</span>
                <button type="button" className="cm-link" onClick={() => openEntity(history, location, { kind: "series", id: summary.seriesId! })}>{summary.series}</button>
                {!isKid && (
                  <button type="button" className="cm-mag" title="Browse this series" onClick={() => go(facetHref("series", summary.seriesId!))}><MagnifierIcon /></button>
                )}
              </div>
            )}

            {(authors.length > 0 || artists.length > 0) && (
              <div className="cm-credits">
                {authors.length > 0 && <div><span className="cm-k">Written by</span><People names={authors} onPick={isKid ? undefined : (n) => go(facetHref("author", n))} /></div>}
                {artists.length > 0 && <div><span className="cm-k">Art by</span><People names={artists} onPick={isKid ? undefined : (n) => go(facetHref("artist", n))} /></div>}
              </div>
            )}

            {detail.parsed?.eventName && (
              <div className="cm-event">
                <Icon d={ICON.bookmark} fill />
                <span>Part of the <button type="button" className="cm-link" onClick={facet("event", detail.parsed.eventName)} disabled={isKid}>{detail.parsed.eventName}</button> crossover</span>
              </div>
            )}

            <div className="cm-stats">
              <StarRating value={starsFromRating(summary.rating)} />
              {summary.seriesRatingResolved != null && (
                <><span className="cm-dot">·</span><span className="cm-librating" title="The series' blended library score">Series <strong>{summary.seriesRatingResolved}</strong></span></>
              )}
              {dateLabel(summary.year, summary.month, summary.datePrecision) && <><span className="cm-dot">·</span><span>{dateLabel(summary.year, summary.month, summary.datePrecision)}</span></>}
              {format && <><span className="cm-dot">·</span><span>{format}</span></>}
              {summary.pageCount != null && <><span className="cm-dot">·</span><span>{summary.pageCount} pp</span></>}
            </div>

            <section className="cm-relsec">
              <h3 className="cm-h3">Your rating</h3>
              <RateTen value={state.rating} onChange={state.setRating} />
            </section>

            {chips.length > 0 && (
              <div className="cm-tags">
                {chips.map((g) => <button key={g} type="button" className="cm-tag" onClick={facet("tag", g)} disabled={isKid}>{g}</button>)}
              </div>
            )}

            {detail.folderName && (
              <div className="cm-folder-wrap">
                <div className="cm-folder-file">{fileLabel(summary)} located in:</div>
                <button type="button" className="cm-folder" onClick={() => go(directoryHref(summary.folderId))} title="Browse this folder" disabled={isKid}>
                  <Icon d={ICON.folder} /><span>{detail.folderPath ?? detail.folderName}</span>
                </button>
              </div>
            )}

            {syn?.text && (
              <>
                <p className="cm-synopsis">{syn.text}</p>
                {syn.label && <p className="cm-provenance">{syn.label}</p>}
              </>
            )}

            {related.length > 0 && <RelatedStrip title={`More in ${summary.series}`} items={related} onOpen={openItem} />}
            {byAuthor.length > 0 && <RelatedStrip title={`More by ${authors[0]}`} items={byAuthor} onOpen={openItem} />}
          </div>
        </div>
      )}
    </BooksModalShell>
  );
}

function People({ names, onPick }: { names: string[]; onPick?: (name: string) => void }) {
  return (
    <>
      {names.map((n, i) => (
        <span key={n}>
          {i > 0 && <span className="cm-sep">, </span>}
          <button type="button" className="cm-link" onClick={onPick ? () => onPick(n) : undefined} disabled={!onPick}>{n}</button>
        </span>
      ))}
    </>
  );
}
