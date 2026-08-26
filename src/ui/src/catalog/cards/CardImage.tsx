import { useEffect, useRef, useState } from "react";

/**
 * A card's artwork, absolutely filling its box. A transient failure (server restart, a Wi-Fi blip
 * under a fling's request burst) is RETRIED with backoff before the hue placeholder takes over —
 * the standalone site used to swap in the placeholder permanently for the band's whole mounted
 * life. Two house rules meet here: no DOM-mutating onError (the retry is React state — the <img>
 * remounts under a new key), and never a fallback that a windowing scheme could mistake for a
 * loaded image (the placeholder is an inert SVG, not a second request).
 */
export const RETRY_LIMIT = 3;
export const RETRY_STEP_MS = 1500;

export function hueSvg(hue: number | undefined, w = 100, h = 150): string {
  const fill = hue == null ? "oklch(0.35 0.02 260)" : `oklch(0.52 0.18 ${Math.round(hue)})`;
  return `data:image/svg+xml,${encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}"><rect width="100%" height="100%" fill="${fill}"/></svg>`)}`;
}

export interface CardImageProps {
  src: string;
  alt?: string;
  hue?: number;
  /** Above-the-fold art loads eagerly; everything else is lazy. */
  eager?: boolean;
  className?: string;
}

export default function CardImage({ src, alt = "", hue, eager, className }: CardImageProps) {
  const [attempt, setAttempt] = useState(0);
  const [failed, setFailed] = useState(false);
  const timerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  useEffect(() => {
    setAttempt(0);
    setFailed(false);
    return () => { if (timerRef.current) clearTimeout(timerRef.current); };
  }, [src]);

  const onError = () => {
    if (attempt >= RETRY_LIMIT) { setFailed(true); return; }
    if (timerRef.current) clearTimeout(timerRef.current);
    timerRef.current = setTimeout(() => setAttempt((a) => a + 1), (attempt + 1) * RETRY_STEP_MS);
  };

  const style = { position: "absolute", inset: 0, width: "100%", height: "100%", objectFit: "cover" } as const;
  if (failed) return <img className={className} src={hueSvg(hue)} alt={alt} style={style} data-fallback="1" />;
  return (
    <img
      key={attempt}
      className={className}
      src={src}
      alt={alt}
      loading={eager ? "eager" : "lazy"}
      decoding="async"
      style={style}
      data-attempt={attempt || undefined}
      onError={onError}
    />
  );
}
