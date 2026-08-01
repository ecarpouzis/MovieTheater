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
 *
 * 3. **Both counts are always on screen — how many exist, and how many are ON.** The chip used to show the
 *    available count as a placeholder and then *replace* it with the selected count ("⚡ 2 cheats") the moment
 *    you picked one, so the two numbers were never visible together and the collapsed chip read as though the
 *    game only had two cheats. It now always reads "N of M", and the open dropdown says so in words.
 */
export default function CheatPicker({ version, value, onChange, disabled, onOpenChange, block = false }) {
  const [cheats, setCheats] = useState(null); // null = not fetched yet
  const [loading, setLoading] = useState(false);
  const gameId = version?.id;
  const count = version?.cheatCount || 0;
  const selected = (value || []).length;

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

  const cheatWord = count === 1 ? "cheat" : "cheats";

  // The count-in-words header + Clear, shared by both layouts (chip has no room for it inline; the
  // block layout keeps it for the same "undo a default-on widescreen patch" reason).
  const popupRender = (menu) => (
    <div>
      <div className="arcade-cheat-dropdown__head">
        <span>
          <b>{selected}</b> of {count} {cheatWord} on
        </span>
        {selected > 0 && (
          <button
            type="button"
            className="arcade-cheat-dropdown__clear"
            // mousedown, not click: the select blurs on mousedown and would swallow the click.
            onMouseDown={(e) => { e.preventDefault(); onChange([]); }}
          >
            Clear
          </button>
        )}
      </div>
      {menu}
    </div>
  );

  // Block layout (the game modal): a full-width select with room to show the picked cheats as tags,
  // portalled to <body> like any normal select. No chip wrapper, no grid-overlap gymnastics.
  if (block) {
    return (
      <Select
        className="agm-cheat-select"
        mode="multiple"
        disabled={disabled}
        value={value}
        onChange={onChange}
        onOpenChange={(open) => { if (open) load(); onOpenChange?.(open); }}
        loading={loading}
        options={options}
        maxTagCount="responsive"
        placeholder={`⚡ ${count} ${cheatWord} available`}
        classNames={{ popup: { root: "arcade-version-dropdown arcade-cheat-dropdown" } }}
        optionFilterProp="label"
        notFoundContent={loading ? "Loading…" : "No cheats for this version."}
        aria-label={`Cheats — ${selected} of ${count} on`}
        popupRender={popupRender}
      />
    );
  }

  return (
    <span
      className={`arcade-chip arcade-chip--select arcade-chip--cheats${selected > 0 ? " arcade-chip--cheats-on" : ""}`}
      onClick={(e) => e.stopPropagation()}
      title={selected > 0
        ? `${selected} of ${count} ${cheatWord} on — click to change`
        : `${count} ${cheatWord} available for this version`}
    >
      <Select
        mode="multiple"
        size="small"
        variant="borderless"
        disabled={disabled}
        value={value}
        onChange={onChange}
        // The card has to know: an open popup renders inside this chip, and the card must be lifted
        // above the cards after it in the grid or they paint straight over the list (see GameCard).
        onOpenChange={(open) => { if (open) load(); onOpenChange?.(open); }}
        loading={loading}
        options={options}
        // The chip is ~150px; individual cheat tags would blow it apart, so the collapsed state is a count.
        // Selected → "⚡ 2 of 28": how many are ON, out of how many the version HAS. Never one without the other.
        maxTagCount={0}
        maxTagPlaceholder={(omitted) => `⚡ ${omitted.length} of ${count}`}
        placeholder={`⚡ ${count} ${cheatWord}`}
        getPopupContainer={(t) => t.parentElement}
        classNames={{ popup: { root: "arcade-version-dropdown arcade-cheat-dropdown" } }}
        popupMatchSelectWidth={260}
        optionFilterProp="label"
        notFoundContent={loading ? "Loading…" : "No cheats for this version."}
        aria-label={`Cheats — ${selected} of ${count} on`}
        // A header the chip has no room for: the count in words, plus the way back out. Without a Clear,
        // undoing a default-on cheat (PS2 widescreen) means hunting it down in a list of hundreds.
        popupRender={popupRender}
      />
    </span>
  );
}
