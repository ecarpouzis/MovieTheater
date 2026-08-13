import { useCallback, useEffect, useRef, useState } from "react";
import { Spin, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import PhotoPersonPicker from "./PhotoPersonPicker";
import PhotoFace from "./PhotoFace";

// The tag queue (docs/photos-plan.md §2.8) — "keyboard-first review of untagged/suggested photos".
//
// Two modes, and the split is the whole design: `untagged` is the MANUAL lane and works the day this
// ships with no Immich anywhere, while `suggested` reads the rows a sync proposed. That ordering is
// §2.4's posture as a UI shape — hand-tagging is the feature, suggestions are an accelerator — and it
// is why turning the sidecar off later removes a tab rather than breaking a workflow.
//
// Nothing here auto-confirms. Y accepts a suggestion, N refuses it (leaving a tombstone so the next
// sync does not re-propose the identical face), S skips, and typing a name tags by hand.

const PAGE = 24;

export default function PhotoTagQueue({ people = [], onChanged, onReloadPeople }) {
  const [mode, setMode] = useState("untagged");
  const [items, setItems] = useState([]);
  const [index, setIndex] = useState(0);
  const [state, setState] = useState("loading"); // loading | ready | error
  const [remaining, setRemaining] = useState(0);
  const [busy, setBusy] = useState(false);
  const cursorRef = useRef(0);
  // Whether the SERVER says another page exists. `nextCursor` cannot answer that: it is the last id
  // on the page and stays non-zero at the end of the queue, so a read-ahead keyed on the cursor alone
  // re-requested the final page and appended the same photographs again — the reviewer meets cards
  // they already decided on and the position counter stops meaning anything.
  const hasMoreRef = useRef(false);
  // The same guard PhotoFolders and PhotoAlbumDetail use, and for a sharper reason here: the
  // read-ahead below fires from an effect that re-runs on every keystroke that moves `index`, while
  // the cursor only advances when a response comes BACK. Holding a key down under a slow round trip
  // therefore fired the same page request several times. One in-flight page at a time.
  const inFlightRef = useRef(false);
  // Which request is the current one. A lane change SUPERSEDES whatever is in the air rather than
  // waiting behind it, and the superseded response must not land in the new lane.
  const requestRef = useRef(0);

  const load = useCallback(
    async (reset) => {
      // A read-ahead is skippable — there is either one already running or no further page to get.
      // A RESET is not: it is the reviewer asking for a different queue, and dropping it left the
      // surface on a spinner that never resolved (switching lanes mid-read-ahead).
      if (!reset && (inFlightRef.current || !hasMoreRef.current)) return;
      const generation = requestRef.current + 1;
      requestRef.current = generation;
      inFlightRef.current = true;
      try {
        const response = await MovieAPI.getPhotoTagQueue({
          mode,
          afterId: reset ? 0 : cursorRef.current,
          take: PAGE,
        });
        if (generation !== requestRef.current) return;
        if (!response.ok) {
          setState("error");
          return;
        }
        const body = await response.json();
        if (generation !== requestRef.current) return;
        cursorRef.current = body.nextCursor || 0;
        hasMoreRef.current = !!body.hasMore;
        setRemaining(body.remaining || 0);
        setItems((prev) => (reset ? body.items || [] : prev.concat(body.items || [])));
        if (reset) setIndex(0);
        setState("ready");
      } catch {
        if (generation === requestRef.current) setState("error");
      } finally {
        // Only the current request owns the flag; a superseded one must not clear the reset's.
        if (generation === requestRef.current) inFlightRef.current = false;
      }
    },
    [mode]
  );

  useEffect(() => {
    cursorRef.current = 0;
    hasMoreRef.current = false;
    setState("loading");
    load(true);
  }, [load]);

  const item = items[index];
  const suggestions = (item?.tags || []).filter((t) => t.source === "Suggested");

  // Reach ahead before the reviewer runs out: a queue that stalls to fetch is a queue somebody stops
  // using. The site's infinite-scroll lesson, applied to a keyboard surface.
  useEffect(() => {
    if (state === "ready" && items.length - index <= 4) load(false);
  }, [state, items.length, index, load]);

  const advance = useCallback(() => setIndex((i) => i + 1), []);

  const decide = useCallback(
    async (action, tagId) => {
      if (busy) return;
      setBusy(true);
      try {
        const response =
          action === "confirm" ? await MovieAPI.confirmPhotoTag(tagId) : await MovieAPI.rejectPhotoTag(tagId);
        if (!response.ok) {
          message.error("Could not record that.");
          return;
        }
        // The decided tag leaves this card in place — the reviewer's position never jumps, and the
        // card falls out of the queue on its own the next time it is loaded.
        setItems((current) =>
          current.map((entry, i) =>
            i === index ? { ...entry, tags: entry.tags.filter((t) => t.id !== tagId) } : entry
          )
        );
        onChanged?.();
      } finally {
        setBusy(false);
      }
    },
    [busy, index, onChanged]
  );

  const tagByHand = useCallback(
    async (pick) => {
      if (!item || busy) return;
      setBusy(true);
      try {
        const response = await MovieAPI.addPhotoTags({
          assetIds: [item.card.id],
          familyPersonId: pick.familyPersonId,
          name: pick.name,
        });
        if (!response.ok) {
          message.error("Could not add that tag.");
          return;
        }
        const body = await response.json();
        message.success(
          `Tagged ${body.person.name}.` +
            // §2.6, said out loud: the tag landed on the copy the album actually shows.
            (body.redirectedToMasters ? " (Recorded against the master copy of this photo.)" : "")
        );
        if (!pick.familyPersonId) onReloadPeople?.();
        onChanged?.();
        advance();
      } finally {
        setBusy(false);
      }
    },
    [item, busy, advance, onChanged, onReloadPeople]
  );

  useEffect(() => {
    const onKey = (event) => {
      if (event.target && ["INPUT", "TEXTAREA"].includes(event.target.tagName)) return;
      const first = suggestions[0];
      if ((event.key === "y" || event.key === "Y") && first) decide("confirm", first.id);
      else if ((event.key === "n" || event.key === "N") && first) decide("reject", first.id);
      else if (event.key === "s" || event.key === "S" || event.key === "ArrowRight") advance();
      else if (event.key === "ArrowLeft") setIndex((i) => Math.max(0, i - 1));
      else return;
      event.preventDefault();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [suggestions, decide, advance]);

  if (state === "loading") return <Spin />;
  if (state === "error") return <p className="photos-note">Could not load the tag queue.</p>;

  return (
    <div className="photo-tag-queue">
      <div className="photo-tag-queue-head">
        <div className="photo-tag-queue-modes">
          <button
            type="button"
            className={mode === "untagged" ? "photos-button active" : "photos-button"}
            onClick={() => setMode("untagged")}
          >
            Untagged
          </button>
          <button
            type="button"
            className={mode === "suggested" ? "photos-button active" : "photos-button"}
            onClick={() => setMode("suggested")}
          >
            Suggestions
          </button>
        </div>
        <span className="photo-tag-queue-position">
          {items.length === 0 ? "0" : Math.min(index + 1, items.length)} of {items.length}
          {remaining > 0 ? ` · ${remaining.toLocaleString()} more waiting` : ""}
        </span>
      </div>

      <p className="photos-note">
        {mode === "suggested"
          ? "Suggestions are guesses. Nothing is ever tagged until you say so — and a “no” is remembered, so the same face is not proposed again."
          : "Photos nobody has tagged yet. Type a name; the tag lands on this photo and on every duplicate copy of it."}
      </p>

      {!item && (
        <p className="photos-note">
          {mode === "suggested"
            ? "No suggestions are waiting. They arrive when the Immich sync runs — and everything here works without it."
            : "Nothing untagged. That is the whole collection, tagged."}
        </p>
      )}

      {item && (
        <div className="photo-tag-card">
          <div className="photo-tag-stage">
            <PhotoFace
              src={item.viewUrl}
              box={suggestions[0]?.box}
              alt=""
              fallback="No preview was generated for this file."
            />
          </div>

          <div className="photo-tag-side">
            <div className="photo-tag-path" title={item.card.path}>
              {item.card.path}
            </div>

            {suggestions.length > 0 && (
              <ul className="photo-tag-suggestions">
                {suggestions.map((tag, i) => (
                  <li key={tag.id} className="photo-tag-suggestion">
                    {tag.faceCropUrl && <img className="photo-tag-crop" src={tag.faceCropUrl} alt="" />}
                    <span className="photo-tag-name">
                      {tag.unnamed ? "Unnamed face group" : tag.name}
                      {tag.confidence != null ? ` · ${Math.round(tag.confidence * 100)}%` : ""}
                    </span>
                    <button
                      type="button"
                      className="photos-button"
                      disabled={busy}
                      onClick={() => decide("confirm", tag.id)}
                    >
                      Yes{i === 0 ? " (Y)" : ""}
                    </button>
                    <button
                      type="button"
                      className="photos-button"
                      disabled={busy}
                      onClick={() => decide("reject", tag.id)}
                    >
                      No{i === 0 ? " (N)" : ""}
                    </button>
                  </li>
                ))}
              </ul>
            )}

            {item.tags.some((t) => t.source !== "Suggested") && (
              <p className="photos-note">
                Already tagged: {item.tags.filter((t) => t.source !== "Suggested").map((t) => t.name).join(", ")}
              </p>
            )}

            <PhotoPersonPicker people={people} onPick={tagByHand} disabled={busy} autoFocus />

            <div className="photo-tag-actions">
              <button type="button" className="photos-button" onClick={advance}>
                Skip (S)
              </button>
              <span className="photo-tag-hint">
                {/* Stated rather than discovered: a keyboard-first surface that keeps its shortcuts
                    secret is a mouse surface. */}
                Y accepts · N refuses (and is remembered) · S skips · ← → move
              </span>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
