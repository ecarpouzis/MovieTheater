import type { CardBadgeSpec } from "./types";
import "./catalog.css";

/**
 * The small label chip a card wears (rating, issue number, system tag). Tones map onto the site's
 * chip tokens in theme.css so a badge looks native in every section and in both themes. This is
 * the first component of the catalog package; the views compose it, sections never style it.
 */
export function CardBadge({ label, tone = "neutral", title }: CardBadgeSpec) {
  return (
    <span className={`catalog-badge catalog-badge--${tone}`} title={title ?? label} data-tone={tone}>
      {label}
    </span>
  );
}

export function CardBadges({ badges }: { badges?: CardBadgeSpec[] }) {
  if (!badges || badges.length === 0) return null;
  return (
    <span className="catalog-badges" role="list">
      {badges.map((b, i) => (
        <span role="listitem" key={`${b.tone ?? "neutral"}:${b.label}:${i}`}>
          <CardBadge {...b} />
        </span>
      ))}
    </span>
  );
}
