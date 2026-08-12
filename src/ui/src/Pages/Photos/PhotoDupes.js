import { useCallback, useEffect, useRef, useState } from "react";
import { Spin, message } from "antd";
import { MovieAPI } from "../../MovieAPI";

// The duplicate review surface (docs/photos-plan.md §2.6) — the one interaction this phase exists
// for. A group is a claim that several files are one photograph; settling it means picking which copy
// represents the group, and that pick collapses the others out of the timeline and albums the moment
// it is made. Nothing here touches a file: every copy stays on disk, in its folder, untouched.
//
// The compare pane zooms and pans BOTH copies together from one shared transform. That is the whole
// point of side-by-side: two images at different zooms and offsets cannot be compared, and a reviewer
// asked to align them by hand will stop reviewing. The transform lives here, in the parent, so the
// panes cannot drift.
//
// Keyboard-first, because a merge-needed folder is hundreds of decisions: ← → walk the group's copies,
// J / K walk the groups, Enter makes the highlighted copy the master, R rejects the group.

const PAGE = 20;

export default function PhotoDupes({ onChanged }) {
  const [state, setState] = useState("loading"); // loading | ready | error
  const [groups, setGroups] = useState([]);
  const [index, setIndex] = useState(0);
  const [pick, setPick] = useState(0);
  const [busy, setBusy] = useState(false);
  const [zoom, setZoom] = useState(1);
  const [pan, setPan] = useState({ x: 0, y: 0 });
  // How many groups are pending on the SERVER, which is not the same as how many are on screen: this
  // surface holds one page of PAGE groups at a time. Kept so the header can say what is really waiting
  // and so a drained page can be told apart from a drained queue.
  const [total, setTotal] = useState(0);
  const inFlightRef = useRef(false);

  const load = useCallback(async () => {
    if (inFlightRef.current) return;
    inFlightRef.current = true;
    try {
      const response = await MovieAPI.getPhotoDupeGroups({ status: "pending", take: PAGE });
      if (!response.ok) {
        setState("error");
        return;
      }
      const body = await response.json();
      setGroups(body.groups || []);
      setTotal(body.total || 0);
      setIndex(0);
      setPick(0);
      setState("ready");
    } catch {
      setState("error");
    } finally {
      inFlightRef.current = false;
    }
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  const group = groups[index];
  const members = group?.members || [];

  // A new group (or a new copy) starts from a clean transform: carrying a zoom across a decision
  // would hand the next reviewer a corner of a photo they have not seen the whole of.
  const resetView = useCallback(() => {
    setZoom(1);
    setPan({ x: 0, y: 0 });
  }, []);

  useEffect(() => {
    resetView();
  }, [index, resetView]);

  const step = useCallback(
    (delta) => {
      setIndex((current) => {
        const next = Math.min(Math.max(current + delta, 0), Math.max(groups.length - 1, 0));
        return next;
      });
      setPick(0);
    },
    [groups.length]
  );

  const decide = useCallback(
    async (action, masterAssetId) => {
      if (!group || busy) return;
      setBusy(true);
      try {
        const response =
          action === "reject"
            ? await MovieAPI.rejectPhotoDupeGroup(group.id)
            : await MovieAPI.resolvePhotoDupeGroup(group.id, masterAssetId);
        if (!response.ok) {
          message.error("Could not record that decision.");
          return;
        }
        if (action === "reject") message.success("Marked as different photos. Nothing was collapsed.");
        else message.success("Master picked. The other copies are collapsed out of the timeline.");

        // Decided groups leave the queue in place, so the reviewer's position does not jump.
        const rest = groups.filter((g) => g.id !== group.id);
        setGroups(rest);
        setTotal((n) => Math.max(0, n - 1));
        setIndex((current) => Math.max(0, Math.min(current, rest.length - 1)));
        setPick(0);
        onChanged?.();

        // The list on screen is ONE PAGE. Filtering it locally is how a decision stays instant, but
        // running it out is not the same as running the queue out — a collection with hundreds of
        // pending groups used to show "Nothing is waiting. Run photos-dupes" after twenty decisions,
        // with everything else still sitting on the server. Draining the page fetches the next one.
        if (rest.length === 0) await load();
      } finally {
        setBusy(false);
      }
    },
    [group, busy, groups, load, onChanged]
  );

  useEffect(() => {
    const onKey = (event) => {
      if (event.target && ["INPUT", "TEXTAREA"].includes(event.target.tagName)) return;
      const count = members.length;
      if (event.key === "ArrowRight" && count) setPick((p) => (p + 1) % count);
      else if (event.key === "ArrowLeft" && count) setPick((p) => (p - 1 + count) % count);
      else if (event.key === "j" || event.key === "J") step(1);
      else if (event.key === "k" || event.key === "K") step(-1);
      else if (event.key === "Enter" && members[pick]) decide("resolve", members[pick].card.id);
      else if (event.key === "r" || event.key === "R") decide("reject");
      else if (event.key === "0") resetView();
      else return;
      event.preventDefault();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [members, pick, step, decide, resetView]);

  if (state === "loading") return <Spin />;
  if (state === "error") return <p className="photos-note">Could not load the duplicate groups.</p>;

  if (!groups.length) {
    // An empty PAGE with groups still pending server-side is the next page arriving, not an empty
    // queue. Saying "nothing is waiting" there would be a lie that ends the review session.
    if (total > 0) return <Spin />;
    return (
      <div className="photo-dupes">
        <p className="photos-note">
          Nothing is waiting. Run <code>photos-dupes</code> to look for copies — it proposes, it never
          decides, and it never changes a file.
        </p>
      </div>
    );
  }

  return (
    <div className="photo-dupes">
      <div className="photo-dupes-head">
        <div>
          <span className="photo-dupes-position">
            Group {index + 1} of {groups.length}
            {/* The true pending count, when the page on screen is not the whole queue. */}
            {total > groups.length ? ` · ${total.toLocaleString()} waiting` : ""}
          </span>
          <span className="photo-dupes-kind">{group.kind === "Exact" ? "identical copies" : "looks like the same photo"}</span>
        </div>
        <div className="photo-dupes-nav">
          <button type="button" className="photos-button" disabled={index === 0} onClick={() => step(-1)}>
            ← Previous
          </button>
          <button type="button" className="photos-button" disabled={index >= groups.length - 1} onClick={() => step(1)}>
            Next →
          </button>
        </div>
      </div>

      <p className="photos-note">
        Picking a copy keeps it in the timeline and collapses the others behind it. Every file stays on
        disk, in its folder — the folder view still shows all of them.
      </p>

      <div className="photo-dupes-zoom">
        <button type="button" className="photos-button" onClick={() => setZoom((z) => Math.min(8, z * 1.5))}>
          Zoom in
        </button>
        <button type="button" className="photos-button" onClick={() => setZoom((z) => Math.max(1, z / 1.5))}>
          Zoom out
        </button>
        <button type="button" className="photos-button" onClick={resetView}>
          Fit
        </button>
        <span className="photo-dupes-hint">
          {/* Stated rather than discovered: a keyboard-first surface that keeps its shortcuts secret
              is a mouse surface. */}
          ← → choose · J / K next group · Enter picks · R marks them different
        </span>
      </div>

      <div className="photo-dupes-panes">
        {members.map((member, i) => (
          <ComparePane
            key={member.card.id}
            member={member}
            selected={i === pick}
            zoom={zoom}
            pan={pan}
            onPan={setPan}
            onSelect={() => setPick(i)}
            onPick={() => decide("resolve", member.card.id)}
            busy={busy}
          />
        ))}
      </div>

      <div className="photo-dupes-actions">
        <button
          type="button"
          className="photos-button"
          disabled={busy || !members[pick]}
          onClick={() => decide("resolve", members[pick]?.card.id)}
        >
          Keep the selected copy
        </button>
        <button type="button" className="photos-button" disabled={busy} onClick={() => decide("reject")}>
          These are different photos
        </button>
      </div>
    </div>
  );
}

/**
 * One copy, with the facts a human actually decides on: resolution, file size, format, date, and
 * WHICH FOLDER it lives in — the merge-needed phone-backup folders' entire story (§2.6).
 *
 * The image is transformed by the SHARED zoom/pan, so dragging inside either pane moves both.
 */
function ComparePane({ member, selected, zoom, pan, onPan, onSelect, onPick, busy }) {
  const dragRef = useRef(null);

  const onPointerDown = (event) => {
    onSelect();
    if (zoom <= 1) return;
    dragRef.current = { x: event.clientX, y: event.clientY, pan };
    event.currentTarget.setPointerCapture?.(event.pointerId);
  };

  const onPointerMove = (event) => {
    const drag = dragRef.current;
    if (!drag) return;
    onPan({ x: drag.pan.x + (event.clientX - drag.x), y: drag.pan.y + (event.clientY - drag.y) });
  };

  const endDrag = () => {
    dragRef.current = null;
  };

  const card = member.card;
  const facts = [
    member.width && member.height ? `${member.width} × ${member.height}` : null,
    member.format || null,
    member.sizeBytes ? `${(member.sizeBytes / 1048576).toFixed(1)} MB` : null,
    member.takenAt ? String(member.takenAt).split("T")[0] : "date unknown",
  ].filter(Boolean);

  return (
    <div className={selected ? "photo-dupes-pane selected" : "photo-dupes-pane"}>
      <div
        className="photo-dupes-stage"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={endDrag}
        onPointerCancel={endDrag}
      >
        {member.viewUrl ? (
          <img
            src={member.viewUrl}
            alt=""
            draggable={false}
            style={{ transform: `translate(${pan.x}px, ${pan.y}px) scale(${zoom})` }}
          />
        ) : (
          <div className="photo-dupes-nopreview">No preview was generated for this file.</div>
        )}
        {member.isMaster && <span className="photo-dupes-master">current pick</span>}
      </div>

      <div className="photo-dupes-facts">
        <div className="photo-dupes-name" title={card.path}>
          {member.fileName}
        </div>
        <div className="photo-dupes-folder">{member.folder || "(root)"}</div>
        <div className="photo-dupes-meta">
          {facts.join(" · ")}
          {member.similarity != null ? ` · ${Math.round(member.similarity * 100)}% match` : ""}
        </div>
        <button type="button" className="photos-button" disabled={busy} onClick={onPick}>
          Keep this one
        </button>
      </div>
    </div>
  );
}
