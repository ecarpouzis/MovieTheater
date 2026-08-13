import { useCallback, useEffect, useState } from "react";
import { Input, Modal, Spin, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import PhotoPersonPicker from "./PhotoPersonPicker";
import PhotoVideo from "./PhotoVideo";

// The lightbox (docs/photos-plan.md §4). Shows the ~1600px `view` derivative by default and only
// reaches for a full-size image when the viewer actually zooms — a timeline click must not pull tens
// of megabytes off the NAS.
//
// The zoom target is the SERVER's decision, not this component's: `zoomUrl` is the untouched
// original when a browser can render it and the 3200px derivative when it cannot (§2.2's
// OriginalRenderable rule), so the format list lives in one place instead of two.

export default function PhotoLightbox({ assetId, onClose, onChanged, onOpenAsset, onUnavailable, people = [], onReloadPeople }) {
  const [detail, setDetail] = useState(null);
  const [state, setState] = useState("loading");
  const [zoomed, setZoomed] = useState(false);
  const [showInfo, setShowInfo] = useState(false);
  const [hidden, setHidden] = useState(false);
  const [albums, setAlbums] = useState([]);
  // People in this photo (§2.8) and the date editor (§2.7). Both write to the group MASTER, and the
  // server says so when it redirected — a member who tagged the copy in front of them is owed the
  // reason their tag appears against a different row (§2.6).
  const [tags, setTags] = useState(null);
  const [editingDate, setEditingDate] = useState(false);

  useEffect(() => {
    if (!assetId) return undefined;
    let cancelled = false;
    setState("loading");
    setZoomed(false);
    MovieAPI.getPhotoAsset(assetId)
      .then((response) => {
        if (cancelled) return undefined;
        // A photo that is gone, or hidden from this member, is a 404 by design (the server refuses to
        // say which). That is the ordinary fate of a link somebody shared last year — not an error to
        // shout about. The album closes the lightbox and shows the view behind it, and the URL loses
        // the parameter so a reload does not try again.
        if (response.status === 404 || response.status === 403) {
          setState("gone");
          (onUnavailable || onClose)?.();
          return undefined;
        }
        if (!response.ok) {
          setState("error");
          return undefined;
        }
        return response.json().then((body) => {
          if (cancelled) return;
          setDetail(body);
          setHidden(!!body.hidden);
          setState("ready");
        });
      })
      .catch(() => {
        if (!cancelled) setState("error");
      });
    // Which albums this photo is in — the one question the lightbox asks that the browse payload
    // deliberately does not carry (§2.9: multi-album membership is normal).
    MovieAPI.getPhotoAssetAlbums(assetId)
      .then((r) => (r.ok ? r.json() : { albums: [] }))
      .then((body) => {
        if (!cancelled) setAlbums(body.albums || []);
      })
      .catch(() => {});

    return () => {
      cancelled = true;
    };
  }, [assetId]);

  const loadTags = useCallback(() => {
    if (!assetId) return Promise.resolve();
    return MovieAPI.getPhotoAssetTags(assetId)
      .then((r) => (r.ok ? r.json() : null))
      .then((body) => setTags(body))
      .catch(() => setTags(null));
  }, [assetId]);

  useEffect(() => {
    setEditingDate(false);
    setTags(null);
    loadTags();
  }, [loadTags]);

  const addTag = async (pick) => {
    const response = await MovieAPI.addPhotoTags({
      assetIds: [assetId],
      familyPersonId: pick.familyPersonId,
      name: pick.name,
    });
    if (!response.ok) {
      message.error("Could not add that tag.");
      return;
    }
    const body = await response.json();
    if (body.redirectedToMasters) message.info("Recorded against the master copy of this photo.");
    if (!pick.familyPersonId) onReloadPeople?.();
    await loadTags();
    onChanged?.();
  };

  const removeTag = async (tag) => {
    const response = await MovieAPI.removePhotoTags({ assetIds: [assetId], familyPersonId: tag.personId });
    if (!response.ok) {
      message.error("Could not remove that tag.");
      return;
    }
    await loadTags();
    onChanged?.();
  };

  // Curation for one photo (§2.9). A flag, and only a flag: hiding takes it out of the timeline and
  // out of albums, leaves it in the folder view, and never touches the file.
  const toggleHidden = async () => {
    const next = !hidden;
    const response = await MovieAPI.setPhotosHidden([assetId], next);
    if (!response.ok) {
      message.error("Could not update that photo.");
      return;
    }
    setHidden(next);
    onChanged?.();
  };

  const card = detail?.card;
  const source = zoomed ? detail?.zoomUrl || detail?.viewUrl : detail?.viewUrl;

  return (
    <Modal
      open={!!assetId}
      onCancel={onClose}
      footer={null}
      width="90vw"
      centered
      destroyOnHidden
      className="photo-lightbox photos-modal"
      title={detail?.fileName || " "}
    >
      {state === "loading" && <Spin />}
      {state === "error" && <p className="photos-note">Could not load this item.</p>}
      {state === "ready" && (
        <div className="photo-lightbox-body">
          <div className="photo-lightbox-stage">
            {/* A video plays IN PLACE (§2.3) — the poster frame the video pass wrote is the <video>
                poster, so the tile the viewer clicked is the frame they keep looking at while it
                starts. Photos take the image branch exactly as before. */}
            {detail.video ? (
              <PhotoVideo
                assetId={assetId}
                poster={detail.viewUrl}
                durationSec={detail.video.durationSec ?? card?.durationSec}
                synced={detail.video.synced}
                playbackConfigured={detail.video.playbackConfigured}
              />
            ) : source ? (
              <img
                src={source}
                alt=""
                className={zoomed ? "photo-lightbox-image zoomed" : "photo-lightbox-image"}
                onClick={() => setZoomed((z) => !z)}
              />
            ) : (
              <div className="photo-lightbox-nopreview">No preview was generated for this file.</div>
            )}
          </div>

          <div className="photo-lightbox-actions">
            {detail.zoomUrl && (
              <button type="button" className="photos-button" onClick={() => setZoomed((z) => !z)}>
                {zoomed ? "Fit" : "Zoom"}
              </button>
            )}
            <button type="button" className="photos-button" onClick={() => setShowInfo((v) => !v)}>
              {showInfo ? "Hide info" : "Info"}
            </button>
            <button type="button" className="photos-button" onClick={toggleHidden}>
              {hidden ? "Unhide" : "Hide"}
            </button>
            <button type="button" className="photos-button" onClick={() => setEditingDate((v) => !v)}>
              {editingDate ? "Close date" : "Set date"}
            </button>
            {detail.downloadUrl && (
              // Always the untouched file, always an explicit action (§2.2) — never an <img> src.
              <a className="photos-button" href={detail.downloadUrl} download={detail.fileName}>
                Download original
              </a>
            )}
          </div>

          {/* "Other copies" (§2.6): the group's members, jumpable without leaving the picture. The
              master is marked, because it is the copy the timeline shows and the copy a tag would
              land on. */}
          {detail.group?.members?.length > 1 && (
            <div className="photo-lightbox-copies">
              <span className="photos-note">
                {detail.group.kind === "Variant"
                  ? "One capture, several files:"
                  : `${detail.group.members.length} copies of this photo:`}
              </span>
              {detail.group.members.map((member) => (
                <button
                  key={member.card.id}
                  type="button"
                  className={
                    member.card.id === assetId ? "photo-lightbox-copy current" : "photo-lightbox-copy"
                  }
                  onClick={() => member.card.id !== assetId && onOpenAsset?.(member.card.id)}
                  title={member.card.path}
                >
                  {member.fileName}
                  {member.isMaster ? " ★" : ""}
                </button>
              ))}
            </div>
          )}

          {/* Who is in this photo (§2.8). The tag lands on the group master, so one pass covers every
              copy of the same print — which is why dupes were resolved before mass tagging. */}
          <div className="photo-lightbox-tags">
            {(tags?.tags || []).map((tag) => (
              <span
                key={tag.id}
                className={tag.source === "Suggested" ? "photo-tag-chip suggested" : "photo-tag-chip"}
              >
                {tag.unnamed ? "Unnamed face group" : tag.name}
                {tag.source === "Suggested" ? " ?" : ""}
                <button
                  type="button"
                  className="photo-tag-chip-remove"
                  aria-label={`Remove ${tag.name}`}
                  onClick={() => removeTag(tag)}
                >
                  ×
                </button>
              </span>
            ))}
            <PhotoPersonPicker people={people} onPick={addTag} />
          </div>

          {editingDate && (
            <PhotoDateEditor
              card={card}
              earliestYearHint={tags?.earliestYearHint || 0}
              onSaved={(next) => {
                setDetail((current) => (current ? { ...current, card: { ...current.card, ...next } } : current));
                setEditingDate(false);
                onChanged?.();
              }}
              assetId={assetId}
            />
          )}

          {(hidden || albums.length > 0) && (
            <p className="photos-note">
              {hidden ? "Hidden from the timeline and albums. The file is untouched. " : ""}
              {albums.length > 0 ? `In: ${albums.map((a) => a.title).join(", ")}` : ""}
            </p>
          )}

          {showInfo && <PhotoInfoPanel detail={detail} />}
        </div>
      )}
    </Modal>
  );
}

/**
 * The date editor (docs/photos-plan.md §2.7).
 *
 * Two ways to answer, because a box of scans supports two different answers: an EXACT wall-clock
 * date, or a circa RANGE ("late 80s"). They are separate fields on purpose — a range must never be
 * written as January 1st, which would pile a decade onto one day while wearing a more convincing date
 * than the undated shelf it escaped.
 *
 * The birth-year hint (§2.7) is printed, never applied: if someone tagged in this photo was born in
 * year N, the photograph cannot be older than N. That is a fact worth showing a human staring at an
 * undated print, and it is emphatically not a date to write for them.
 *
 * Wall-clock in, wall-clock out: the value is sent as typed and never handed to anything
 * timezone-aware, because EXIF carries no offset and neither does a family's memory of which morning
 * it was.
 */
function PhotoDateEditor({ assetId, card, earliestYearHint, onSaved }) {
  const [exact, setExact] = useState(card?.takenAt ? String(card.takenAt).slice(0, 16) : "");
  const [yearMin, setYearMin] = useState(card?.yearMin ? String(card.yearMin) : "");
  const [yearMax, setYearMax] = useState(card?.yearMax ? String(card.yearMax) : "");
  const [busy, setBusy] = useState(false);

  const save = async (body) => {
    setBusy(true);
    try {
      const response = await MovieAPI.setPhotoAssetDate(assetId, body);
      if (!response.ok) {
        message.error("Could not save that date.");
        return;
      }
      const saved = await response.json();
      if (saved.redirected) message.info("Recorded against the master copy of this photo.");
      onSaved?.({
        takenAt: saved.takenAt,
        takenAtSource: saved.takenAtSource,
        yearMin: saved.yearMin,
        yearMax: saved.yearMax,
      });
    } finally {
      setBusy(false);
    }
  };

  const digits = (value) => value.replace(/[^0-9]/g, "").slice(0, 4);

  return (
    <div className="photo-date-editor">
      <div className="photo-date-row">
        <label className="photo-field">
          <span>Exact date and time</span>
          <Input
            value={exact}
            placeholder="2011-07-04T10:30"
            disabled={busy}
            onChange={(e) => setExact(e.target.value)}
          />
        </label>
        <button
          type="button"
          className="photos-button"
          disabled={busy}
          onClick={() => save({ takenAt: exact, takenAtSet: true })}
        >
          Set date
        </button>
      </div>

      <div className="photo-date-row">
        <label className="photo-field">
          <span>Roughly, between</span>
          <Input value={yearMin} placeholder="1986" disabled={busy} onChange={(e) => setYearMin(digits(e.target.value))} />
        </label>
        <label className="photo-field">
          <span>and</span>
          <Input value={yearMax} placeholder="1989" disabled={busy} onChange={(e) => setYearMax(digits(e.target.value))} />
        </label>
        <button
          type="button"
          className="photos-button"
          disabled={busy || !yearMin}
          onClick={() =>
            save({
              yearMin: Number(yearMin),
              yearMax: yearMax ? Number(yearMax) : Number(yearMin),
              yearsSet: true,
            })
          }
        >
          Set range
        </button>
      </div>

      {earliestYearHint > 0 && (
        <p className="photos-note">
          Someone tagged here was born in {earliestYearHint}, so this photo cannot be older than that.
          It is a hint — nothing is written until you press a button.
        </p>
      )}
      <p className="photos-note">
        A range leaves the exact date empty on purpose: a year is not a wall clock, and a made-up
        January 1st would look more certain than it is.
      </p>
    </div>
  );
}

/** The EXIF panel. Everything here was persisted by the metadata pass, so opening it costs no NAS
 *  read — which is the reason the raw readout is stored rather than recomputed (§2.5). */
function PhotoInfoPanel({ detail }) {
  const card = detail.card || {};
  const facts = [
    ["Folder", detail.folder || "(root)"],
    ["Taken", formatTaken(card)],
    ["Date source", card.takenAtSource],
    ["Dimensions", card.width && card.height ? `${card.width} × ${card.height}` : null],
    ["Camera", [detail.cameraMake, detail.cameraModel].filter(Boolean).join(" ") || null],
    ["Location", detail.locationLabel || (detail.gpsLat != null ? `${detail.gpsLat.toFixed(5)}, ${detail.gpsLon.toFixed(5)}` : null)],
    ["Size", detail.sizeBytes ? `${(detail.sizeBytes / 1048576).toFixed(1)} MB` : null],
    ["SHA-256", detail.sha256 ? detail.sha256.slice(0, 16) + "…" : null],
    ["Ingest batch", detail.ingestBatch],
    ["Ingest error", detail.ingestError],
  ].filter(([, value]) => value != null && value !== "");

  return (
    <div className="photo-info">
      <dl className="photo-info-facts">
        {facts.map(([label, value]) => (
          <div className="photo-info-fact" key={label}>
            <dt>{label}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>

      {detail.exif && (
        <div className="photo-info-exif">
          {Object.entries(detail.exif).map(([directory, tags]) => (
            <details key={directory}>
              <summary>{directory}</summary>
              <dl className="photo-info-facts">
                {Object.entries(tags).map(([name, value]) => (
                  <div className="photo-info-fact" key={name}>
                    <dt>{name}</dt>
                    <dd>{value}</dd>
                  </div>
                ))}
              </dl>
            </details>
          ))}
        </div>
      )}
    </div>
  );
}

/** Wall-clock, printed as wall-clock (§2.7): TakenAt carries no offset by design, so it must not be
 *  handed to a Date that would re-interpret it in the viewer's zone. */
export function formatTaken(card) {
  if (!card?.takenAt) {
    if (card?.yearMin && card.yearMin === card.yearMax) return `about ${card.yearMin}`;
    if (card?.yearMin) return `about ${card.yearMin}–${card.yearMax}`;
    return "Date unknown";
  }
  const [date, time = ""] = String(card.takenAt).split("T");
  return time ? `${date} ${time.slice(0, 5)}` : date;
}
