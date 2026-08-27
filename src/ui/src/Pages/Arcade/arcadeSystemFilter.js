// How the lobby's system filter is encoded in the URL. Two surfaces write it — the console carousel
// above the grid and the rail's chips / the bar's SmartSearch (`system:snes`) — and they MUST agree
// byte for byte, or picking a console in one would read back as a different selection in the other.
//
// Encoding (R9 S2c): the rail's repeatable include, `?f=system:snes&f=system:genesis` — the same
// `f=` the other facets ride (catalog/rail/facetUrl.ts). Absent means "all systems", the untouched
// lobby. The pre-S2c form, a comma-joined `?system=snes,genesis`, is still READ here (a bookmark's
// first render, before `legacyToArcadeSearch` rewrites it) but never written.

export const SYSTEM_PARAM = "system";
const SYSTEM_TOKEN = "system:";

/** The selected system codes, from a URLSearchParams or a raw `?…` string. Always an array, lowercase, deduped. */
export function parseSystems(search) {
  const params = typeof search === "string" ? new URLSearchParams(search) : search;
  const out = [];
  const add = (raw) => {
    const code = String(raw || "").trim().toLowerCase();
    if (code && !out.includes(code)) out.push(code);
  };
  for (const entry of params.getAll("f")) if (entry.startsWith(SYSTEM_TOKEN)) add(entry.slice(SYSTEM_TOKEN.length));
  for (const code of (params.get(SYSTEM_PARAM) || "").split(",")) add(code);
  return out;
}

/** The API's csv for a set of codes — "" when empty (the lobby's filter object; the URL is the `f=` form). */
export function serializeSystems(systems) {
  return (systems || []).filter(Boolean).join(",");
}

/** Add `system` if absent, remove it if present. Order is preserved so tiles never reshuffle. */
export function toggleSystem(systems, system) {
  const code = String(system || "").toLowerCase();
  return systems.includes(code) ? systems.filter((s) => s !== code) : [...systems, code];
}
