import { useState } from "react";
import useWallCapacity from "../cards/useWallCapacity";
import FlatCardStream from "./FlatCardStream";
import type { ViewProps } from "./ViewProps";

/**
 * Wall — a zero-gap cover mosaic at true aspect; covers stay silent at rest and a caption fades in
 * on hover. Its band size is the viewport's capacity (measured from the wrap element), so the
 * first band is exactly one screenful and later bands recycle whole.
 */
export const WALL_BASE_CELL = 140;
export const WALL_PAGE_SIZE = 120;

export default function WallView(props: ViewProps) {
  const cellH = Math.round(WALL_BASE_CELL * props.coverScale);
  const [capacity, setCapacity] = useState<number | null>(null);
  // The section's own tile aspect (square art for Music/Arcade, portrait for the rest) sizes the estimate;
  // the 0.66 default over-fetched square-art sections by a third.
  const aspect = props.source.defaultAspect ?? 0.66;
  const wrapRef = useWallCapacity(cellH, setCapacity, aspect);
  // Measure BEFORE the stream mounts: a probe the width of the wall, at the wall's own top, so band 0 is
  // fetched ONCE at the real capacity. Before this the stream fetched band 0 at the 120 fallback, then the
  // measured wrap re-read it at the true size — a double fetch on every cold start.
  if (capacity == null) return <div ref={wrapRef} className="bx-wall bx-wall-probe" aria-hidden="true" style={{ minHeight: 1 }} />;
  return (
    <FlatCardStream
      {...props}
      variant="wall"
      cellH={cellH}
      perBand={capacity}
      onWrapEl={wrapRef}
    />
  );
}
