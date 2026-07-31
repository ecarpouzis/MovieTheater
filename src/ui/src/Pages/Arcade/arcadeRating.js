/**
 * The hover text behind a card's ★ score.
 *
 * The number itself is just "★ 82" wherever it appears, but it can come from three very different
 * places: a LaunchBox/IGDB community poll, a per-hack score researched from a site that actually
 * rates romhacks, or — for the hacks nobody polls — an editorial estimate. Showing all three as a
 * bare star would quietly present a judgement call as a measurement, so the source always rides
 * along in the tooltip. `ratingSource` is set by ArcadeController alongside `rating`.
 */
export function ratingTooltip(game) {
  const votes = game.ratingCount ? `${game.ratingCount.toLocaleString()} votes` : null;
  return [votes, game.ratingSource].filter(Boolean).join(" · ") || undefined;
}
