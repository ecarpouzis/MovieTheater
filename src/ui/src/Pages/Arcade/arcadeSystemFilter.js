// How the lobby's system filter is encoded in the URL. Two surfaces now write it — the navbar rail's
// System dropdown and the console carousel above the grid — and they MUST agree byte for byte, or
// picking a console in one would read back as a different selection in the other.
//
// Encoding: a comma-joined list of system codes, `?system=snes,genesis`. Empty/absent means "all
// systems", which is the untouched lobby. A single value is just a one-element list, so every link
// minted before the filter went multi-select (`?system=nes`) still resolves to exactly what it meant.

export const SYSTEM_PARAM = "system";

/** The selected system codes, from a URLSearchParams or a raw `?…` string. Always an array. */
export function parseSystems(search) {
  const params = typeof search === "string" ? new URLSearchParams(search) : search;
  return (params.get(SYSTEM_PARAM) || "")
    .split(",")
    .map((s) => s.trim().toLowerCase())
    .filter(Boolean);
}

/** The param value for a set of codes — "" when empty, which callers drop from the URL entirely. */
export function serializeSystems(systems) {
  return (systems || []).filter(Boolean).join(",");
}

/** Add `system` if absent, remove it if present. Order is preserved so tiles never reshuffle. */
export function toggleSystem(systems, system) {
  const code = String(system || "").toLowerCase();
  return systems.includes(code) ? systems.filter((s) => s !== code) : [...systems, code];
}
