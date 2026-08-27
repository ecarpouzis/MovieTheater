import { useHistory } from "react-router-dom";
import { MovieAPI } from "../../MovieAPI";
import FallbackImage from "../../Components/FallbackImage";
import useFavoriteChannels from "./useFavoriteChannels";
import { clockLabel } from "./ChannelGrid";

/**
 * The guide's detail panel (R9 S1c — Eric: "a real TV guide lets you click a show and have it open
 * up a section at the top with the description and other details"). Opens above the grid when a
 * program cell is clicked: poster, title, channel + slot, the description, ▶ Watch on that channel,
 * Open title (the movie sheet on the landing), ♥ the channel, and what's up next on it. Everything
 * here comes from the guide payload the grid already holds — no second fetch.
 */
export default function GuideDetail({ channel, program, rowItems, userData, setUserData, onClose }) {
  const history = useHistory();
  const { isFavorite, toggle } = useFavoriteChannels(userData, setUserData);
  if (!channel || !program) return null;
  const startMs = Date.parse(program.startUtc);
  const endMs = Date.parse(program.endUtc);
  const live = startMs <= Date.now() && Date.now() < endMs;
  const upNext = (rowItems || []).filter((p) => Date.parse(p.startUtc) >= endMs).slice(0, 4);
  const poster = program.posterId ? MovieAPI.getPosterThumbnail(program.posterId, program.posterVersion, program.kind) : null;
  const fav = isFavorite(channel.id);
  const canOpenTitle = program.posterId > 0 && (program.kind === "movie" || program.kind === "series");

  return (
    <section className="guide-detail" aria-label={`${program.title} on ${channel.name}`}>
      <div className="guide-detail__poster">
        {poster ? (
          <FallbackImage src={poster} alt="" className="guide-detail__img" fallback={<div className="guide-detail__img guide-detail__img--empty" />} />
        ) : (
          <div className="guide-detail__img guide-detail__img--empty" />
        )}
      </div>
      <div className="guide-detail__body">
        <p className="guide-detail__eyebrow">
          {channel.name} · {clockLabel(startMs)} – {clockLabel(endMs)}{live ? " · now" : ""}
        </p>
        <h2 className="guide-detail__title">{program.title}</h2>
        {program.plot && <p className="guide-detail__plot">{program.plot}</p>}
        <div className="guide-detail__actions">
          <button type="button" className="guide-detail__btn guide-detail__btn--primary" onClick={() => history.push(`/tv/${channel.id}`)}>
            ▶ Watch on {channel.name}
          </button>
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
      <button type="button" className="guide-detail__close" onClick={onClose} aria-label="Close details">×</button>
    </section>
  );
}
