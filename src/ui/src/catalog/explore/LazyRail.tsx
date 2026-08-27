/**
 * A rail that reserves its height and mounts its cards only once it comes near the viewport.
 * Explore's first two rails render straight away (they are the first screen); everything below the
 * fold waits, so a landing with eight rails paints two rails' worth of images, not eight.
 *
 * The placeholder is the SAME height as the mounted rail's box, so revealing one never moves the
 * page under the reader (the engine's spacer discipline, one floor up).
 */
import type { ReactNode } from "react";
import { useNearViewport } from "./useNearViewport";

export default function LazyRail({ minHeight, children }: { minHeight: number; children: ReactNode }) {
  const [ref, near] = useNearViewport<HTMLDivElement>();
  return (
    <div ref={ref} className="xp-lazy" style={near ? undefined : { minHeight }} aria-busy={near ? undefined : "true"}>
      {near ? children : null}
    </div>
  );
}
