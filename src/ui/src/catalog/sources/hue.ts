/** A stable 0–359 hue from a string — shelf spines and placeholder tints before the art lands. */
export function hueOf(s: string): number {
  let h = 0;
  for (let i = 0; i < s.length; i += 1) h = (h * 31 + s.charCodeAt(i)) >>> 0;
  return h % 360;
}
