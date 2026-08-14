// Applying a curation write to the cards a browse list is already holding (docs/photos-plan.md §2.9).
//
// The alternative — re-fetching the list after every batch action — is what made batch work not worth
// starting: a keyset-paged timeline can only restart from the newest photograph, so hiding forty
// pictures threw the reader back to the top of a list they had scrolled a thousand pixels into, every
// single round. Nothing here INVENTS a fact: the changes applied are the ones the write itself
// reported, and the header's counts still come from the server.
//
// A pure function so it can be asserted on directly. What it encodes is one rule that is easy to get
// wrong by hand: a photograph that no longer belongs on this surface has to LEAVE it, or a picture
// that was just sent to the gallery sits there on the family timeline until the next reload, and the
// member presses the button a second time.

/**
 * `items` with `patch.changes` merged into every card whose id is in `patch.ids`, minus the cards
 * that `stays` no longer wants on this surface.
 *
 * @param items    the cards currently laid out
 * @param patch    `{ seq, ids, changes }` from PhotosPage's `curated`, or null
 * @param stays    (patchedItem) => boolean — this surface's own membership rule
 * @returns        the same array reference when nothing changed, so React can skip the re-render
 */
export function applyPatch(items, patch, stays) {
  if (!patch?.ids?.length || !patch.changes) return items;
  const touched = new Set(patch.ids);
  if (!(items || []).some((item) => touched.has(item.id))) return items;

  const next = [];
  for (const item of items) {
    if (!touched.has(item.id)) {
      next.push(item);
      continue;
    }
    const patched = { ...item, ...patch.changes };
    if (stays && !stays(patched)) continue;
    next.push(patched);
  }
  return next;
}
