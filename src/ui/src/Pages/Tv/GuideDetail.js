import { useState, useEffect } from "react";
import { useHistory } from "react-router-dom";
import { MovieAPI } from "../../MovieAPI";
import FallbackImage from "../../Components/FallbackImage";
import useFavoriteChannels from "./useFavoriteChannels";
import { clockLabel } from "./ChannelGrid";
import GuidePreview from "./GuidePreview";
import { programMetaItems, programHeadline, restartHref, PREVIEW_MIN_WIDTH } from "./guideModel";

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

// Stroke icons on the guide's 24 grid — the cable box draws its own glyphs, not the emoji font's.
const PlayIcon = () => (
  <svg width="13" height="13" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4.5v15l12-7.5z" /></svg>
);
const RestartIcon = () => (
  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M3 12a9 9 0 1 0 3-6.7" /><path d="M3 4v5h5" />
  </svg>
);
const HeartIcon = ({ filled }) => (
  <svg width="13" height="13" viewBox="0 0 24 24" fill={filled ? "currentColor" : "none"} stroke="currentColor" strokeWidth="2" aria-hidden="true">
    <path d="M12 20.5s-7.5-4.6-7.5-10A4.3 4.3 0 0 1 12 8a4.3 4.3 0 0 1 7.5 2.5c0 5.4-7.5 10-7.5 10z" />
  </svg>
);

/**
 * The guide's detail panel (R9 S1c, v2 2026-09-04). Opens above the grid for the selected programme:
 * poster · headline · the meta line (year or S/E · certificate tag · length · IMDb · genre) · the
 * plot · the actions — and, on desktop, the live PREVIEW (GuidePreview) with the slot's progress.
 * No "up next" strip: the grid to the right of the selected block IS what's next, and the strip
 * wrapped under the buttons on a narrow body and doubled the panel's height (2026-09-05).
 * It is part of the cable box: it sits on the guide's own dark ground with the guide's blue
 * as its one accent (the light card tokens put a white slab on the black grid — the 2026-09-05 review).
 *
 * Actions: ▶ Tune in joins the channel at the live offset. ↺ Start over joins AND casts the room's
 * Restart vote in the same tune (`/tv/<id>?restart=1`): alone, the channel restarts at once; with
 * others watching the label says so ("Vote to start over · 2 watching") and the room shows the
 * tally. It is offered only for the programme airing now on an unfrozen channel — a future programme
 * cannot be started early on a shared timeline, and a frozen one has no clock to restart against.
 * Everything here comes from the guide payload the grid already holds — no second fetch.
 */
export default function GuideDetail({ channel, program, row, userData, setUserData, onClose, previewArmed = false, onArmPreview, nowMs }) {
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
  const poster = program.posterId ? MovieAPI.getPosterThumbnail(program.posterId, program.posterVersion, program.kind) : null;
  const fav = isFavorite(channel.id);
  const canOpenTitle = program.posterId > 0 && (program.kind === "movie" || program.kind === "series");
  const meta = programMetaItems(program, { full: true });
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
          <span className="guide-detail__channel">{channel.name}</span>
          <span className="guide-detail__sep" aria-hidden="true">·</span>
          <span>{clockLabel(startMs)} – {clockLabel(endMs)}</span>
          {live && !paused && (<><span className="guide-detail__sep" aria-hidden="true">·</span><span className="guide-detail__now">now</span></>)}
          {paused && (<><span className="guide-detail__sep" aria-hidden="true">·</span><span className="guide-detail__paused">paused</span></>)}
        </p>
        <h2 className="guide-detail__title">{headline}</h2>
        {meta.length > 0 && (
          <p className="guide-detail__meta">
            {meta.map((item, i) => (
              <span key={`${item.kind}-${i}`} className="guide-detail__meta-item">
                {i > 0 && <span className="guide-detail__sep" aria-hidden="true">·</span>}
                {item.kind === "tag" && <span className="guide-detail__tag">{item.text}</span>}
                {item.kind === "imdb" && (<span><span className="guide-detail__imdb">IMDb</span> {item.text}</span>)}
                {item.kind === "text" && <span>{item.text}</span>}
              </span>
            ))}
          </p>
        )}
        {program.plot && <p className="guide-detail__plot">{program.plot}</p>}
        <div className="guide-detail__actions">
          <button type="button" className="guide-detail__btn guide-detail__btn--primary" title={`Watch ${channel.name} live`} onClick={() => history.push(`/tv/${channel.id}`)}>
            <PlayIcon /> Tune in
          </button>
          {canStartOver && (
            <button
              type="button"
              className="guide-detail__btn guide-detail__btn--secondary"
              title={viewers > 0 ? "Others are watching — your vote to restart is cast when you tune in" : "Tune in and start this programme from the beginning"}
              onClick={() => history.push(restartHref(channel.id))}
            >
              <RestartIcon /> {viewers > 0 ? `Vote to start over · ${viewers} watching` : "Start over"}
            </button>
          )}
          {canOpenTitle && (
            <button type="button" className="guide-detail__btn" onClick={() => history.push(`/?title=${program.kind}:${program.posterId}`)}>
              Open title
            </button>
          )}
          {userData && (
            <button type="button" className={`guide-detail__btn${fav ? " is-on" : ""}`} aria-pressed={fav} onClick={() => toggle(channel.id)}>
              <HeartIcon filled={fav} /> Favourite channel
            </button>
          )}
        </div>
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
