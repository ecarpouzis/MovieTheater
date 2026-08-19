/**
 * What a channel is showing at `atMs`: the airing program, or the next one up if it's between
 * programs; null when the whole lineup is behind us (off air).
 *
 * This is the ONE reading of "now" over a GuideGrid lineup — the EPG and the channel-card lineup
 * both take it from here. It exists because /API/Channel/GuideGrid deliberately reaches
 * GuideLookbackMinutes (30) into the PAST so the strip left of the EPG's now line is backed by
 * programs on every row: `items[0]` is therefore the programme that was airing half an hour ago and
 * calling it "now" is wrong for the whole first half hour of every programme.
 *
 * `atMs` must be the SERVER's clock (GuideGrid states it as serverNowUtc) — the schedule is shared,
 * so a viewer with a skewed browser clock must still be told what everyone else is watching.
 */
export function nowPlaying(items, atMs) {
  if (!items?.length) return null;
  return (
    items.find((p) => Date.parse(p.startUtc) <= atMs && atMs < Date.parse(p.endUtc)) ||
    items.find((p) => Date.parse(p.endUtc) > atMs) ||
    null
  );
}

export default nowPlaying;
