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
  const wrapRef = useWallCapacity(cellH, setCapacity);
  return (
    <FlatCardStream
      {...props}
      variant="wall"
      cellH={cellH}
      perBand={capacity ?? WALL_PAGE_SIZE}
      onWrapEl={wrapRef}
    />
  );
}
