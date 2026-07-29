import { useEffect, useState } from "react";

/**
 * Subscribe to a CSS media query from JS.
 *
 * For the layout decisions CSS can't make on its own — chiefly sizes that have to arrive at a
 * component as a PROP. GameCover is the reason this exists: its box is measured in exact pixels
 * (see coverBox), because a percentage height inside a flex item of indefinite height resolves to
 * `auto` and the art ends up sizing the card that was supposed to be sizing the art. So "make the
 * box art bigger on a big screen" can't be a media query in a stylesheet; it has to be a number.
 *
 * Prefer a plain media query in CSS whenever the change is expressible there.
 */
export default function useMediaQuery(query) {
  const [matches, setMatches] = useState(
    () => typeof window !== "undefined" && !!window.matchMedia && window.matchMedia(query).matches
  );

  useEffect(() => {
    if (typeof window === "undefined" || !window.matchMedia) return undefined;
    const mq = window.matchMedia(query);
    const handler = (e) => setMatches(e.matches);
    // Re-read on (re)subscribe: the query may have changed, or the viewport may have moved between
    // the initial render and this effect.
    setMatches(mq.matches);
    mq.addEventListener("change", handler);
    return () => mq.removeEventListener("change", handler);
  }, [query]);

  return matches;
}
