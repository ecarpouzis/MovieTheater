import { useEffect, useMemo, useRef, useState } from "react";
import LoadFailure from "../../Components/LoadFailure";
import { Spin, Button } from "antd";
import {
  DndContext,
  closestCenter,
  PointerSensor,
  TouchSensor,
  KeyboardSensor,
  useSensor,
  useSensors,
} from "@dnd-kit/core";
import {
  SortableContext,
  verticalListSortingStrategy,
  arrayMove,
  sortableKeyboardCoordinates,
} from "@dnd-kit/sortable";
import { restrictToVerticalAxis, restrictToParentElement } from "@dnd-kit/modifiers";
import { MovieAPI } from "../../MovieAPI";
import RateRow from "./RateRow";
import { computeScores } from "./computeScores";
import { reconstructLayout, diffScores, anchorsToSave, movieKey } from "./rateLayout";
import { useDebouncedCallback } from "../../hooks/useDebounce";
import "./RatePage.css";

const SAVE_DEBOUNCE_MS = 800;
const CHUNK = 100; // server caps SetRatings at 200/call; stay well under and drive the loop here

const clampScore = (v) => Math.max(0, Math.min(100, Math.round(Number(v) || 0)));

// Apply a set of rating writes onto a plain {key: score} object (for keeping userData.ratings in sync).
function applyWrites(ratings, writes) {
  const next = { ...(ratings || {}) };
  for (const w of writes) {
    const k = `${w.kind}:${w.id}`;
    if (w.value == null) delete next[k];
    else next[k] = w.value;
  }
  return next;
}

function RatePage({ userData, setUserData }) {
  const [items, setItems] = useState([]); // ranked list, top → bottom: movie + anchor bars
  const [tray, setTray] = useState([]); // watched-but-unranked cards
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState(false);
  const [reloadNonce, setReloadNonce] = useState(0);
  const [status, setStatus] = useState("idle"); // idle | saving | saved | error

  const loadedRef = useRef(false);
  const loadOkRef = useRef(false); // set only once the initial load succeeds — autosave is gated on it
  const itemsRef = useRef(items);
  itemsRef.current = items;
  const baselineRef = useRef(new Map()); // last-saved score map (movieKey → score)
  const savingRef = useRef(false);
  const anchorSeq = useRef(1);

  const scores = useMemo(() => computeScores(items), [items]);

  // Load every watched title once, the first time userData is available.
  useEffect(() => {
    if (!userData || loadedRef.current) return;
    loadedRef.current = true;
    let cancelled = false;
    const seen = userData.moviesSeen || [];
    const miscSeen = userData.miscSeen || [];
    const anchors = Array.isArray(userData.ratingAnchors) ? userData.ratingAnchors : [];
    anchorSeq.current = 1 + anchors.reduce((mx, a) => Math.max(mx, parseInt(String(a.id).replace(/\D/g, ""), 10) || 0), 0);

    const finish = (cards) => {
      if (cancelled) return;
      const { items: built, unranked } = reconstructLayout(cards || [], userData.ratings || {}, anchors);
      setItems(built);
      setTray(unranked);
      baselineRef.current = computeScores(built);
      baselineRef.anchors = anchorsToSave(built);
      loadOkRef.current = true;
      setLoading(false);
    };

    Promise.all([
      seen.length ? MovieAPI.getTitlesByIds(seen).then((r) => r.json()) : Promise.resolve([]),
      miscSeen.length ? MovieAPI.getMiscByIds(miscSeen).then((r) => r.json()) : Promise.resolve([]),
    ])
      .then(([titleCards, miscCards]) => finish([...(titleCards || []), ...(miscCards || [])]))
      .catch(() => {
        if (!cancelled) {
          setLoadError(true);
          setLoading(false);
        }
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [userData, reloadNonce]);

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
    useSensor(TouchSensor, { activationConstraint: { delay: 200, tolerance: 6 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates })
  );

  // ── Autosave: debounce → diff vs baseline → write changed scores in bounded chunks → persist anchors ──
  const runSave = async () => {
    // Never write back off a load that errored or never finished — the baseline would be wrong, so a
    // diff against it could look like the user cleared ratings they simply never saw.
    if (!loadOkRef.current) return;
    if (savingRef.current) {
      scheduleSave();
      return;
    }
    const currentItems = itemsRef.current;
    const { current, writes } = diffScores(currentItems, baselineRef.current);
    const anchorPayload = anchorsToSave(currentItems);
    const anchorsChanged = JSON.stringify(anchorPayload) !== JSON.stringify(baselineRef.anchors || []);
    if (!writes.length && !anchorsChanged) {
      setStatus("idle");
      return;
    }

    savingRef.current = true;
    setStatus("saving");
    try {
      for (let i = 0; i < writes.length; i += CHUNK) {
        const res = await MovieAPI.setRatings(writes.slice(i, i + CHUNK));
        const data = await res.json();
        if (!data || !data.success) throw new Error((data && data.message) || "save failed");
      }
      const ar = await MovieAPI.setUserSetting("RatingAnchors", JSON.stringify(anchorPayload));
      const ad = await ar.json();
      if (!ad || !ad.success) throw new Error("anchor save failed");

      baselineRef.current = current;
      baselineRef.anchors = anchorPayload;
      setUserData((prev) => ({
        ...prev,
        ratings: applyWrites(prev && prev.ratings, writes),
        ratingAnchors: anchorPayload,
      }));
      setStatus("saved");
    } catch {
      setStatus("error");
    } finally {
      savingRef.current = false;
    }
  };

  // Shared trailing debounce; it re-arms on every edit and cancels itself on unmount. It always runs
  // the latest runSave, which is what lets runSave re-arm itself while a save is still in flight.
  const scheduleSave = useDebouncedCallback(() => runSave(), SAVE_DEBOUNCE_MS);

  // ── Mutations ──
  const onDragEnd = ({ active, over }) => {
    if (!over || active.id === over.id) return;
    setItems((prev) => {
      const from = prev.findIndex((it) => it.key === active.id);
      const to = prev.findIndex((it) => it.key === over.id);
      if (from === -1 || to === -1) return prev;
      return arrayMove(prev, from, to);
    });
    scheduleSave();
  };

  const onAnchorChange = (item, v) => {
    setItems((prev) => prev.map((it) => (it.key === item.key ? { ...it, value: clampScore(v) } : it)));
    scheduleSave();
  };

  const onAnchorRemove = (item) => {
    setItems((prev) => prev.filter((it) => it.key !== item.key));
    scheduleSave();
  };

  const onUnrank = (item) => {
    setItems((prev) => prev.filter((it) => it.key !== item.key));
    setTray((prev) => [item.card, ...prev]);
    scheduleSave();
  };

  const addFromTray = (card) => {
    setTray((prev) => prev.filter((c) => !(c.id === card.id && (c.kind || "movie") === (card.kind || "movie"))));
    setItems((prev) => [
      { type: "movie", key: movieKey(card), id: card.id, kind: card.kind || "movie", card },
      ...prev,
    ]);
    scheduleSave();
  };

  const addAnchor = () => {
    const id = `a${anchorSeq.current++}`;
    const movieScores = items.filter((it) => it.type === "movie").map((it) => scores.get(it.key) ?? 0);
    const def = movieScores.length ? clampScore(Math.min(...movieScores)) : 50;
    setItems((prev) => [...prev, { type: "anchor", key: `anchor:${id}`, id, value: def }]);
    scheduleSave();
  };

  if (!userData) {
    return <div className="rate-page rate-page--empty">Log in to rate movies.</div>;
  }

  const statusText =
    status === "saving" ? "Saving…" : status === "saved" ? "Saved" : status === "error" ? "Couldn’t save" : "";

  return (
    <div className="rate-page">
      <div className="rate-header">
        <div>
          <h1 className="rate-title">Rate Movies</h1>
          <p className="rate-sub">
            Drag titles so your favorites sit at the top. Drop in a <strong>score line</strong> to peg an exact
            number — everything above it scores higher, everything below scores lower.
          </p>
        </div>
        <div className="rate-actions">
          <span className={`rate-status rate-status--${status}`}>
            {statusText}
            {status === "error" ? (
              <Button type="link" size="small" onClick={runSave}>
                Retry
              </Button>
            ) : null}
          </span>
          <Button onClick={addAnchor}>+ Score line</Button>
        </div>
      </div>

      {loading ? (
        <div className="rate-loading">
          <Spin />
        </div>
      ) : loadError ? (
        <LoadFailure
          message="Couldn’t load your watched titles."
          onRetry={() => { loadedRef.current = false; setLoadError(false); setLoading(true); setReloadNonce((n) => n + 1); }}
        />
      ) : (
        <>
          {items.length === 0 ? (
            <div className="rate-empty-ranked">
              Nothing ranked yet. Add titles from “Not yet ranked” below to start.
            </div>
          ) : (
            <DndContext
              sensors={sensors}
              collisionDetection={closestCenter}
              modifiers={[restrictToVerticalAxis, restrictToParentElement]}
              onDragEnd={onDragEnd}
            >
              <SortableContext items={items.map((it) => it.key)} strategy={verticalListSortingStrategy}>
                <ul className="rate-list">
                  {items.map((it) => (
                    <RateRow
                      key={it.key}
                      item={it}
                      score={it.type === "movie" ? scores.get(it.key) ?? 0 : undefined}
                      onAnchorChange={onAnchorChange}
                      onAnchorRemove={onAnchorRemove}
                      onUnrank={onUnrank}
                    />
                  ))}
                </ul>
              </SortableContext>
            </DndContext>
          )}

          {tray.length > 0 && (
            <div className="rate-tray">
              <h2 className="rate-tray-title">Not yet ranked — {tray.length}</h2>
              <div className="rate-tray-list">
                {tray.map((card) => (
                  <button
                    type="button"
                    className="rate-tray-item"
                    key={movieKey(card)}
                    onClick={() => addFromTray(card)}
                    title="Add to ranking"
                  >
                    <img
                      className="rate-tray-poster"
                      alt=""
                      loading="lazy"
                      src={MovieAPI.getPosterThumbnail(card.id, card.posterVersion, card.kind)}
                      onError={(e) => {
                        e.currentTarget.style.visibility = "hidden";
                      }}
                    />
                    <span className="rate-tray-name">{card.title || card.simpleTitle}</span>
                    <span className="rate-tray-add">+</span>
                  </button>
                ))}
              </div>
            </div>
          )}
        </>
      )}
    </div>
  );
}

export default RatePage;
