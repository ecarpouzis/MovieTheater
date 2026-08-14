// ── Transport glyphs (shared by the play bar and Now Playing) ───────────────
// Drawn, not typed. ⏸ and ▶ carry an EMOJI presentation on Android, so the middle of a transport
// rendered as a colour emoji (amber, rounded, a different weight) between two monochrome text
// glyphs — three buttons that are one control reading as three unrelated ones. A variation selector
// only asks nicely; an inline SVG is the same three shapes on every platform.
//
// Sized in `em` on purpose: both hosts set the size with font-size on the button, so these drop into
// a 15px bar button and a 24px full-player button without either needing to know about the other.
// They live here rather than in either component because two copies of the same four paths is two
// chances for the surfaces to drift apart, and they are meant to read as one control in two places.

const ICON = { viewBox: "0 0 24 24", width: "1em", height: "1em", fill: "currentColor", "aria-hidden": true, focusable: "false" };

export function IconPrev() {
  return <svg {...ICON}><path d="M7 6h2.4v12H7zM19 6v12l-9-6z" /></svg>;
}

export function IconNext() {
  return <svg {...ICON}><path d="M14.6 6H17v12h-2.4zM5 6l9 6-9 6z" /></svg>;
}

export function IconPlay() {
  return <svg {...ICON}><path d="M8 5l12 7-12 7z" /></svg>;
}

export function IconPause() {
  return <svg {...ICON}><path d="M7 5h3.4v14H7zM13.6 5H17v14h-3.4z" /></svg>;
}
