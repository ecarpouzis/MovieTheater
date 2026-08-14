import { useEffect, useMemo, useState } from "react";
import { useHistory } from "react-router-dom";
import { MovieAPI } from "../../MovieAPI";

// The timeline's edge index (docs/photos-plan.md §4, deep-browse addendum) — the thumb-cut index a
// thick physical album has along its page edge, drawn as a slim rail beside the grid. Twenty
// thousand photographs over seventy-five years are unbrowsable by scroll alone; the rail names the
// years that actually hold photographs and one press lands on any of them.
//
// The jump is a CURSOR SEED, not a new query mode: the timeline pages by (TakenAt DESC, Id DESC),
// so "start at 2011" is the ordinary keyset cursor (Jan 1 2012, id 0) — the id-0 tie-break makes the
// predicate strictly-before, and everything from scroll to lazy pages behaves exactly as it does
// from the top. That is why this component knows nothing about paging.

/** The keyset cursor that starts the timeline at the newest photograph of `year`. */
export function jumpCursorFor(year) {
  return { takenAt: `${year + 1}-01-01T00:00:00`, id: 0 };
}

/** Years grouped into decades, newest first — [{ decade: 2020, years: [...] }, ...]. Exported for
 *  tests: the grouping is the one piece of rail logic with a right answer per input. */
export function groupByDecade(years) {
  const groups = [];
  for (const entry of years || []) {
    const decade = Math.floor(entry.year / 10) * 10;
    const last = groups[groups.length - 1];
    if (last && last.decade === decade) last.years.push(entry);
    else groups.push({ decade, years: [entry] });
  }
  return groups;
}

export default function PhotoYearRail({ includeHidden = false, currentYear = null, activeYear = null, onJump }) {
  const history = useHistory();
  const [index, setIndex] = useState(null);

  useEffect(() => {
    let alive = true;
    (async () => {
      try {
        const response = await MovieAPI.getPhotosTimelineYears({ includeHidden });
        if (!response.ok) return;
        const body = await response.json();
        if (alive) setIndex(body);
      } catch {
        // The rail is navigation sugar — a timeline without it still scrolls, so a failed index
        // fetch renders nothing rather than an error the grid beside it would have to share space with.
      }
    })();
    return () => {
      alive = false;
    };
  }, [includeHidden]);

  const decades = useMemo(() => groupByDecade(index?.years), [index]);
  const maxCount = useMemo(
    () => Math.max(1, ...(index?.years || []).map((y) => y.count)),
    [index]
  );

  if (!index || !index.years?.length) return null;

  const highlight = activeYear ?? currentYear;

  return (
    <nav className="photo-year-rail" aria-label="Jump to a year">
      <button
        type="button"
        className={`photo-year-rail-newest${activeYear == null ? " is-active" : ""}`}
        onClick={() => onJump?.(null)}
      >
        Newest
      </button>
      {decades.map((group) => (
        <div className="photo-year-rail-decade" key={group.decade}>
          <span className="photo-year-rail-decade-label">{`${group.decade}s`}</span>
          {group.years.map((entry) => (
            <button
              key={entry.year}
              type="button"
              className={`photo-year-rail-year${entry.year === highlight ? " is-active" : ""}`}
              title={`${entry.count.toLocaleString()} photo${entry.count === 1 ? "" : "s"} in ${entry.year}`}
              aria-current={entry.year === highlight ? "true" : undefined}
              onClick={() => onJump?.(entry.year)}
            >
              <span className="photo-year-rail-year-label">{entry.year}</span>
              {/* sqrt, not linear: a 2,400-photo year should read "thick", not flatten every other
                  year's mark into invisibility. */}
              <span
                className="photo-year-rail-heat"
                style={{ width: `${Math.round(10 + 26 * Math.sqrt(entry.count / maxCount))}px` }}
                aria-hidden="true"
              />
            </button>
          ))}
        </div>
      ))}
      {index.undated > 0 && (
        <button
          type="button"
          className="photo-year-rail-undated"
          title={`${index.undated.toLocaleString()} photos with no date yet — mostly album scans`}
          onClick={() => history.push("/photos/undated")}
        >
          Undated
          <span className="photo-year-rail-undated-count">{index.undated.toLocaleString()}</span>
        </button>
      )}
    </nav>
  );
}
