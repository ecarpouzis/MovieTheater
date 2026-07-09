import GameCover from "./GameCover";
import { systemLabel } from "./arcadeSystems";

// The host's initial in a gradient avatar, matching the sidebar's account chip.
function Avatar({ name, size }) {
  return (
    <span className="arcade-avatar" style={{ width: size, height: size, fontSize: size <= 24 ? 10 : 11 }}>
      {(name || "?").charAt(0).toUpperCase()}
    </span>
  );
}

/**
 * Seat dots: one per seat, filled for a player present, dashed for a seat still free. The design
 * calls for a `seats[]` array; our /API/Arcade/Rooms returns `players[]` + `maxPlayers`, which is the
 * same information — a seat is occupied iff its index is inside `players`.
 */
function Seats({ players, maxPlayers }) {
  return (
    <div className="arcade-seats" aria-label={`${players.length} of ${maxPlayers} seats taken`}>
      {Array.from({ length: maxPlayers }, (_, i) => (
        <span key={i} className={`arcade-seat${i < players.length ? " arcade-seat--taken" : ""}`} />
      ))}
    </div>
  );
}

function RoomCard({ room, onJoin }) {
  const seatsFree = room.seatsFree;
  const host = room.host || room.players[0];
  return (
    <div className="arcade-room">
      <GameCover game={{ ...room.game, hasBoxArt: true }} artId={room.game.id} height={64} className="arcade-room__art" />

      <div className="arcade-room__info">
        <div className="arcade-room__title" title={room.game.title}>{room.game.title}</div>
        <div className="arcade-room__meta">
          <span className="arcade-chip arcade-chip--system">{systemLabel(room.game.system)}</span>
          <span className="arcade-room__status">
            {room.players.length} playing
            {room.starting ? " · starting…" : ` · ${seatsFree} seat${seatsFree === 1 ? "" : "s"} free`}
          </span>
        </div>
      </div>

      <div className="arcade-room__right">
        <Seats players={room.players} maxPlayers={room.maxPlayers} />
        {host && (
          <div className="arcade-room__host">
            <Avatar name={host} size={24} />
            <span className="arcade-room__hostname">{host} hosting</span>
          </div>
        )}
        <button type="button" className="arcade-btn arcade-btn--join" onClick={() => onJoin(room.roomCode)}>
          Join room
        </button>
      </div>
    </div>
  );
}

/** The Live-rooms strip. Rendered only when a room is actually open (README shows no empty state). */
function LiveRooms({ rooms, onJoin }) {
  if (!rooms.length) return null;
  return (
    <section className="arcade-section">
      <div className="arcade-section__head">
        <span className="arcade-dot-live" />
        <h2 className="arcade-section__title">Live rooms</h2>
        <span className="arcade-section__count">
          {rooms.length} open now
        </span>
      </div>
      <div className="arcade-rooms">
        {rooms.map((room) => <RoomCard key={room.roomCode} room={room} onJoin={onJoin} />)}
      </div>
    </section>
  );
}

export default LiveRooms;
