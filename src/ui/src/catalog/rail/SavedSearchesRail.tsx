/** The saved searches as rail pills (★ name · ×) and the inline name prompt. */
import { useState, type KeyboardEvent } from "react";
import type { SavedSearch } from "./savedSearches";

export function SavedSearchesRail({ list, onApply, onRemove }: { list: SavedSearch[]; onApply: (search: string) => void; onRemove: (id: string) => void }) {
  return (
    <div className="bx-rail-saved">
      {list.length === 0 && <div className="bx-rail-saved-empty">Save a filter set to pin it here.</div>}
      {list.map((s) => (
        <div key={s.id} className="bx-saved-pill">
          <button type="button" className="bx-saved-apply" onClick={() => onApply(s.search)}>★ {s.name}</button>
          <button type="button" className="bx-saved-del" onClick={() => onRemove(s.id)} aria-label={`Remove saved search ${s.name}`}>×</button>
        </div>
      ))}
    </div>
  );
}

export function SaveSearchPrompt({ onSave, onCancel }: { onSave: (name: string) => void; onCancel: () => void }) {
  const [name, setName] = useState("");
  const commit = () => { if (name.trim()) onSave(name.trim()); };
  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "Enter") commit();
    if (e.key === "Escape") onCancel();
  };
  return (
    <div className="bx-save-prompt">
      <input autoFocus className="bx-save-input" value={name} onChange={(e) => setName(e.target.value)} onKeyDown={onKey} placeholder="Search name…" aria-label="Search name" />
      <button type="button" className="bx-chip-save" onClick={commit}>Save</button>
      <button type="button" className="bx-chip-clear" onClick={onCancel}>Cancel</button>
    </div>
  );
}
