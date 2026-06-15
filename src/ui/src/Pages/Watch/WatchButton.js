import { useHistory } from "react-router-dom";
import "./WatchButton.css";

// Ticket geometry, in SVG user units. Round notch holes are punched along the
// LEFT and RIGHT edges through a luminance mask; a notch is centred on each
// corner (cy 0 and cy H) so the corners cut inward, and the notch depth (NOTCH_R)
// stays clear of the inner ruled border and the side labels.
const W = 168;
const H = 66;
const NOTCH_R = 5;
const NOTCH_YS = Array.from({ length: 5 }, (_, i) => (i * H) / 4); // 0, 16.5, 33, 49.5, 66

/**
 * The ticket stub that opens the screening room. Rendered only when the movie has
 * a mapped file AND the viewer holds a password — streaming isn't advertised to
 * anyone who can't use it (only an admin can grant a first password anyway; the
 * server enforces this regardless). Drawn as an SVG admission ticket (🎟️): round
 * notches down each side, a ruled border, big WATCH centred with ADMIT / ONE set
 * vertically on the two stubs.
 */
function WatchButton({ movie, userData, onBeforeNavigate }) {
  const history = useHistory();

  if (!movie?.hasFile) return null;

  // Don't reveal that the site can stream to anyone without a password.
  if (!userData?.hasPassword) return null;

  return (
    <button
      type="button"
      className="watch-stub"
      aria-label="Watch"
      onClick={() => {
        onBeforeNavigate?.();
        history.push(`/watch/${movie.id}`);
      }}
    >
      <svg className="watch-stub-svg" viewBox={`0 0 ${W} ${H}`} width={W} height={H} aria-hidden="true">
        <defs>
          <linearGradient id="watch-stub-paper" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#f4bd58" />
            <stop offset="1" stopColor="#e49d2d" />
          </linearGradient>
          <mask id="watch-stub-notch">
            <rect x="0" y="0" width={W} height={H} rx="5" fill="#fff" />
            {NOTCH_YS.map((y) => (
              <g key={y}>
                <circle cx="0" cy={y} r={NOTCH_R} fill="#000" />
                <circle cx={W} cy={y} r={NOTCH_R} fill="#000" />
              </g>
            ))}
          </mask>
        </defs>

        {/* notched amber ticket stock */}
        <rect x="0" y="0" width={W} height={H} fill="url(#watch-stub-paper)" mask="url(#watch-stub-notch)" />
        {/* one big rounded rectangle around the whole ticket */}
        <rect x="8" y="8" width={W - 16} height={H - 16} rx="4" fill="none" stroke="#5a3d12" strokeOpacity="0.55" strokeWidth="1.4" />
        {/* vertical lines fencing off the two end stubs */}
        <line x1="26" y1="8" x2="26" y2={H - 8} stroke="#5a3d12" strokeOpacity="0.55" strokeWidth="1.4" />
        <line x1={W - 26} y1="8" x2={W - 26} y2={H - 8} stroke="#5a3d12" strokeOpacity="0.55" strokeWidth="1.4" />

        {/* ADMIT ONE set vertically, together, inside each end stub */}
        <text transform={`rotate(-90 17 ${H / 2})`} x="17" y={H / 2} textAnchor="middle" dominantBaseline="central" fontSize="6.5" letterSpacing="0.5" fontWeight="700" fill="#5a3d12" fillOpacity="0.72">
          ADMIT ONE
        </text>
        <text transform={`rotate(90 ${W - 17} ${H / 2})`} x={W - 17} y={H / 2} textAnchor="middle" dominantBaseline="central" fontSize="6.5" letterSpacing="0.5" fontWeight="700" fill="#5a3d12" fillOpacity="0.72">
          ADMIT ONE
        </text>

        {/* big WATCH with the play arrow after it, centred in the middle compartment */}
        <text x="75" y={H / 2} textAnchor="middle" dominantBaseline="central" fontSize="17" letterSpacing="1.5" fontWeight="800" fill="#5a3d12">
          WATCH
        </text>
        <path d="M 120 27 L 120 39 L 130 33 Z" fill="#5a3d12" />
      </svg>
    </button>
  );
}

export default WatchButton;
