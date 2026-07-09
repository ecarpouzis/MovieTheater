import { useCallback, useState } from "react";
import { Select } from "antd";
import { MovieAPI } from "../../MovieAPI";

/**
 * The card's cheat control: a searchable multi-select of the cheats available for the SELECTED version
 * (docs/arcade-cheats.md). Cheats are per-ROM — a USA code means nothing on the Japanese dump — so this
 * remounts whenever the version dropdown changes.
 *
 * Two things are deliberate:
 *
 * 1. **Lazy-loaded.** The list only fetches when the dropdown first opens. A popular title carries hundreds
 *    of community codes (Mario Kart 64: 941), so shipping every card's list with the grid would be absurd.
 *    The pre-selected defaults arrive with the card instead, which is why a widescreen patch still applies
 *    to a room started without ever opening this.
 *
 * 2. **Search is the point, not a nicety.** Upstream cheat files are long, unordered, and a minority of the
 *    descriptions aren't in English. We keep the upstream order (it's the community's, and it makes an entry
 *    checkable against the source file) and let the user type "infinite".
 */
export default function CheatPicker({ version, value, onChange, disabled }) {
  const [cheats, setCheats] = useState(null); // null = not fetched yet
  const [loading, setLoading] = useState(false);
  const gameId = version?.id;
  const count = version?.cheatCount || 0;

  const load = useCallback(() => {
    if (cheats !== null || loading || !gameId) return;
    setLoading(true);
    MovieAPI.getArcadeCheats(gameId)
      .then((rows) => setCheats(rows))
      .finally(() => setLoading(false));
  }, [cheats, loading, gameId]);

  if (count === 0) return null;

  // Before the first open we know how many cheats exist but not their names, so the options list is seeded
  // from the current selection. Without this antd renders a selected-but-unknown id as the raw "c123".
  const options = (cheats || (value || []).map((id) => ({ id, name: "Cheat" })))
    .map((c) => ({
      value: c.id,
      label: c.name,
      title: c.note ? `${c.name} — ${c.note}` : c.name,
    }));

  return (
    <span className="arcade-chip arcade-chip--select arcade-chip--cheats" onClick={(e) => e.stopPropagation()}>
      <Select
        mode="multiple"
        size="small"
        bordered={false}
        disabled={disabled}
        value={value}
        onChange={onChange}
        onDropdownVisibleChange={(open) => open && load()}
        loading={loading}
        options={options}
        // The chip is ~150px; individual cheat tags would blow it apart, so the collapsed state is a count.
        maxTagCount={0}
        maxTagPlaceholder={(omitted) => `⚡ ${omitted.length} cheat${omitted.length === 1 ? "" : "s"}`}
        placeholder={`⚡ Cheats (${count})`}
        getPopupContainer={(t) => t.parentElement}
        popupClassName="arcade-version-dropdown arcade-cheat-dropdown"
        dropdownMatchSelectWidth={260}
        optionFilterProp="label"
        notFoundContent={loading ? "Loading…" : "No cheats for this version."}
        aria-label="Cheats"
      />
    </span>
  );
}
