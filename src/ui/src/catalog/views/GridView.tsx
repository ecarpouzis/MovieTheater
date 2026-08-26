import FlatCardStream from "./FlatCardStream";
import type { ViewProps } from "./ViewProps";

/**
 * Grid — a flat labeled-card catalog: every tile the same 0.66 cover box (wide art crops via
 * object-fit — true-aspect widths made the 2–3-column phone grid ragged), the metadata strip under
 * each, rows wrapping continuously across band pages.
 */
export const GRID_BASE_CELL = 220;

export default function GridView(props: ViewProps) {
  const cellH = Math.round(GRID_BASE_CELL * props.coverScale);
  return <FlatCardStream {...props} variant="grid" cellH={cellH} perBand={props.source.pageSize ?? 48} />;
}
