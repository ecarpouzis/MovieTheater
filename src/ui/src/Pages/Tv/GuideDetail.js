import { useState, useEffect } from "react";
import { useHistory } from "react-router-dom";
import { MovieAPI } from "../../MovieAPI";
import FallbackImage from "../../Components/FallbackImage";
import useFavoriteChannels from "./useFavoriteChannels";
import { clockLabel } from "./ChannelGrid";
import GuidePreview from "./GuidePreview";
import { programMeta, programHeadline, restartHref, PREVIEW_MIN_WIDTH } from "./guideModel";

// Desktop/tablet get the live preview column; phones get the poster (see guideModel.js).
function useWideEnough() {
  const query = `(min-width: ${PREVIEW_MIN_WIDTH}px)`;
  const [wide, setWide] = useState(() => (typeof window !== "undefined" && window.matchMedia ? window.matchMedia(query).matches : true));
  useEffect(() => {
    if (typeof window === "undefined" || !window.matchMedia) return undefined;
    const mq = window.matchMedia(query);
    const onChange = (e) => setWide(e.matches);
    mq.addEventListener?.("change", onChange);
    return () => mq.removeEventListener?.("change", onChange);
  }, [query]);
  return wide;
}

/**
 * The guide's detail panel (R9 S1c, v2 2026-09-04). Opens above the grid for the selected programme:
 * poster · headline · the meta line (year or S/E · certificate · length · IMDb · genre) · the plot ·
 * the actions · up next — and, on desktop, the live PREVIEW (GuidePreview) with the slot's progress.
 *
 * Actions: ▶ Tune in joins the channel at the live offset. ↺ Start over joins AND casts the room's
 * Restart vote in the same tune (`/tv/<id>?restart=1`): alone, the channel restarts at once; with
 * others watching the label says so ("Vote to start over · 2 watching") and the room shows the
 * tally. It is offered only for the programme airing now on an unfrozen channel — a future programme
 * cannot be started early on a shared timeline, and a frozen one has no clock to restart against.
 * Everything here comes from the guide payload the grid already holds — no second fetch.
 */
export default function GuideDetail({ channel, program, rowItems, row, userData, setUserData, onClose, previewArmed = false, onArmPreview, nowMs }) {
  const history = useHistory();
  const { isFavorite, toggle } = useFavoriteChannels(userData, setUserData);
  const wide = useWideEnough();
  if (!channel || !program) return null;
  const at = Number.isFinite(nowMs) ? nowMs : Date.now();
  const startMs = Date.parse(program.startUtc);
  const endMs = Date.parse(program.endUtc);
  const live = startMs <= at && at < endMs;
  const paused = !!row?.paused;
  const viewers = row?.viewers || 0;
  const upNext = (rowItems || []).filter((p) => Date.parse(p.startUtc) >= endMs).slice(0, 4);
  const poster = program.posterId ? MovieAPI.getPosterThumbnail(program.posterId, program.posterVersion, program.kind) : null;
  const fav = isFavorite(channel.id);
  const canOpenTitle = program.posterId > 0 && (program.kind === "movie" || program.kind === "series");
  const meta = programMeta(program, { full: true });
  const headline = programHeadline(program);
  const canStartOver = live && !paused;

  return (
    <section className={`guide-detail${wide ? " guide-detail--preview" : ""}`} aria-label={`${program.title} on ${channel.name}`}>
      <div className="guide-detail__poster">
        {poster ? (
          <FallbackImage src={poster} alt="" className="guide-detail__img" fallback={<div className="guide-detail__img guide-detail__img--empty" />} />
        ) : (
          <div className="guide-detail__img guide-detail__img--empty" />
        )}
      </div>
      <div className="guide-detail__body">
        <p className="guide-detail__eyebrow">
          {channel.name} · {clockLabel(startMs)} – {clockLabel(endMs)}{live ? " · now" : ""}{paused ? " · paused" : ""}
        </p>
        <h2 className="guide-detail__title">{headline}</h2>
        {meta && <p className="guide-detail__meta">{meta}</p>}
        {program.plot && <p className="guide-detail__plot">{program.plot}</p>}
        <div className="guide-detail__actions">
          <button type="button" className="guide-detail__btn guide-detail__btn--primary" title={`Watch ${channel.name} live`} onClick={() => history.push(`/tv/${channel.id}`)}>
            ▶ Tune in
          </button>
          {canStartOver && (
            <button
              type="button"
              className="guide-detail__btn guide-detail__btn--secondary"
              title={viewers > 0 ? "Others are watching — your vote to restart is cast when you tune in" : "Tune in and start this programme from the beginning"}
              onClick={() => history.push(restartHref(channel.id))}
            >
              {viewers > 0 ? `↺ Vote to start over · ${viewers} watching` : "↺ Start over"}
            </button>
          )}
          {canOpenTitle && (
            <button type="button" className="guide-detail__btn" onClick={() => history.push(`/?title=${program.kind}:${program.posterId}`)}>
              Open title
            </button>
          )}
          {userData && (
            <button type="button" className={`guide-detail__btn${fav ? " is-on" : ""}`} aria-pressed={fav} onClick={() => toggle(channel.id)}>
              {fav ? "♥ Favourite channel" : "♡ Favourite channel"}
            </button>
          )}
        </div>
        {upNext.length > 0 && (
          <div className="guide-detail__next">
            <p className="guide-detail__eyebrow">Up next</p>
            <ul className="guide-detail__next-list">
              {upNext.map((p) => (
                <li key={p.startUtc} className="guide-detail__next-item" title={p.title}>
                  {p.posterId ? (
                    <FallbackImage src={MovieAPI.getPosterThumbnail(p.posterId, p.posterVersion, p.kind)} alt="" className="guide-detail__next-img" fallback={<div className="guide-detail__next-img guide-detail__img--empty" />} />
                  ) : <div className="guide-detail__next-img guide-detail__img--empty" />}
                  <span className="guide-detail__next-time">{clockLabel(Date.parse(p.startUtc))}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
      {wide && (
        <GuidePreview
          channelId={channel.id}
          program={program}
          live={live}
          paused={paused}
          armed={previewArmed}
          onArm={onArmPreview}
          nowMs={at}
          poster={poster}
        />
      )}
      <button type="button" className="guide-detail__close" onClick={onClose} aria-label="Close details">×</button>
    </section>
  );
}
