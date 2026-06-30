import { useSortable } from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import MovieBar from "./MovieBar";
import AnchorBar from "./AnchorBar";

// Sortable wrapper for one row in the ranking list; dispatches to a movie or anchor bar by item type.
// The drag handle (attributes + listeners) is forwarded to the bar so only the handle starts a drag,
// leaving the number input and ✕ buttons clickable.
export default function RateRow({ item, score, onAnchorChange, onAnchorRemove, onUnrank }) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: item.key });
  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    zIndex: isDragging ? 2 : undefined,
    opacity: isDragging ? 0.9 : 1,
  };
  const dragHandle = { attributes, listeners };
  return (
    <li ref={setNodeRef} style={style} className={`rate-row${isDragging ? " rate-row--dragging" : ""}`}>
      {item.type === "anchor" ? (
        <AnchorBar item={item} dragHandle={dragHandle} onChange={onAnchorChange} onRemove={onAnchorRemove} />
      ) : (
        <MovieBar item={item} score={score} dragHandle={dragHandle} onUnrank={onUnrank} />
      )}
    </li>
  );
}
