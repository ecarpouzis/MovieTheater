import { useEffect, useMemo, useRef, useState } from "react";

// The type-ahead person picker (docs/photos-plan.md §2.8), shared by the lightbox, the selection bar
// and the tag queue so the three cannot drift into three different ways of naming the same face.
//
// A family has tens of people, not thousands, so the whole list is fetched once by the caller and
// filtered here — no per-keystroke round trip, and the picker stays usable with a flaky connection.
//
// Keyboard-first, because the tag queue is hundreds of decisions: ↑ ↓ move, Enter picks, Escape
// closes. Typing a name the list does not have offers "Add <name>", which the server turns into a
// person in the same round trip as the tag.

export default function PhotoPersonPicker({
  people = [],
  onPick,
  placeholder = "Tag someone…",
  autoFocus = false,
  allowCreate = true,
  disabled = false,
}) {
  const [text, setText] = useState("");
  const [highlight, setHighlight] = useState(0);
  const inputRef = useRef(null);

  useEffect(() => {
    if (autoFocus) inputRef.current?.focus();
  }, [autoFocus]);

  const query = text.trim().toLowerCase();
  const matches = useMemo(() => {
    // Named rows only. An unnamed row is an imported face cluster, not a person, and offering it as a
    // blank choice would let somebody tag a photo with nobody (§2.8).
    const named = people.filter((p) => p.name);
    if (!query) return named.slice(0, 8);
    return named.filter((p) => p.name.toLowerCase().includes(query)).slice(0, 8);
  }, [people, query]);

  const exact = matches.some((p) => p.name.toLowerCase() === query);
  const canCreate = allowCreate && query.length > 0 && !exact;
  const options = canCreate ? matches.concat([{ id: null, name: text.trim(), create: true }]) : matches;

  useEffect(() => {
    setHighlight(0);
  }, [text]);

  const choose = (option) => {
    if (!option) return;
    setText("");
    setHighlight(0);
    onPick?.(option.create ? { name: option.name } : { familyPersonId: option.id, name: option.name });
  };

  const onKeyDown = (event) => {
    if (event.key === "ArrowDown") setHighlight((h) => Math.min(h + 1, options.length - 1));
    else if (event.key === "ArrowUp") setHighlight((h) => Math.max(h - 1, 0));
    else if (event.key === "Enter") choose(options[highlight]);
    else if (event.key === "Escape") setText("");
    else return;
    event.preventDefault();
    // The queue listens for single-key shortcuts on the window; a keystroke aimed at this box is not
    // one of them, and letting it through would confirm a tag while somebody was typing a name.
    event.stopPropagation();
  };

  return (
    <div className="photo-person-picker">
      <input
        ref={inputRef}
        className="photo-person-input"
        type="text"
        value={text}
        placeholder={placeholder}
        disabled={disabled}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={onKeyDown}
        aria-label={placeholder}
      />
      {options.length > 0 && (
        <ul className="photo-person-options">
          {options.map((option, i) => (
            <li key={option.create ? "new" : option.id}>
              <button
                type="button"
                className={i === highlight ? "photo-person-option highlighted" : "photo-person-option"}
                disabled={disabled}
                onMouseEnter={() => setHighlight(i)}
                onClick={() => choose(option)}
              >
                {option.create ? `Add “${option.name}”` : option.name}
                {!option.create && option.tagCount ? (
                  <span className="photo-person-count">{option.tagCount}</span>
                ) : null}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
