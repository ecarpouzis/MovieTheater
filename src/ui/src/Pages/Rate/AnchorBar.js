import { InputNumber } from "antd";

// A "separator" bar the user drops between movies to peg an absolute score at that rank. Movies above it
// score higher than its value, movies below score lower; the bars between anchors interpolate. Dragged by
// the handle to reposition; the number input edits its pegged value.
export default function AnchorBar({ item, dragHandle, onChange, onRemove }) {
  return (
    <div className="rate-bar rate-bar--anchor">
      <button
        type="button"
        className="rate-drag-handle"
        aria-label="Drag to reposition anchor"
        {...dragHandle.attributes}
        {...dragHandle.listeners}
      >
        ⠿
      </button>
      <span className="rate-anchor-label">Score line</span>
      <div className="rate-anchor-line" aria-hidden="true" />
      <InputNumber
        className="rate-anchor-input"
        size="small"
        min={0}
        max={100}
        value={item.value}
        onChange={(v) => onChange(item, v)}
      />
      <button type="button" className="rate-bar-remove" title="Remove anchor" onClick={() => onRemove(item)}>
        ✕
      </button>
    </div>
  );
}
