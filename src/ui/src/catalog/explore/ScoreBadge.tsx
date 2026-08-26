/**
 * The library's 0–100 score, colour-graded so a glance reads "how good": gold ≥ 85, green ≥ 75,
 * slate below (the standalone's tiers). `note` is the rationale when the section has one.
 */
export function scoreTier(score: number): "gold" | "green" | "base" {
  return score >= 85 ? "gold" : score >= 75 ? "green" : "base";
}

export default function ScoreBadge({ score, note, onDark, className }: { score?: number | null; note?: string | null; onDark?: boolean; className?: string }) {
  if (score == null) return null;
  const tier = scoreTier(score);
  return (
    <span className={`xp-score xp-score-${tier}${onDark ? " xp-score-ondark" : ""}${className ? ` ${className}` : ""}`} title={note || `Library score ${score}/100`}>
      {Math.round(score)}
    </span>
  );
}
