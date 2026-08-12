// A photo with an optional face box drawn over it (docs/photos-plan.md §2.8).
//
// This is the DEGRADATION path §2.4 promises in miniature: when the sidecar has cached a face crop we
// show it, and when it has not — Immich unreachable, never deployed, or thrown away — we draw the
// stored box over our OWN derivative instead. The box is kept as fractions of the image precisely so
// that works: one rectangle is correct on the grid thumb, the 1600px view, the zoom copy and the
// original alike, with no sidecar involved and no second measurement to keep in step.

export default function PhotoFace({ src, box, alt = "", fallback = "No preview." }) {
  if (!src) return <div className="photo-face-nopreview">{fallback}</div>;

  return (
    <div className="photo-face">
      <img className="photo-face-image" src={src} alt={alt} />
      {box && (
        <span
          className="photo-face-box"
          style={{
            left: `${box.x * 100}%`,
            top: `${box.y * 100}%`,
            width: `${box.w * 100}%`,
            height: `${box.h * 100}%`,
          }}
        />
      )}
    </div>
  );
}
