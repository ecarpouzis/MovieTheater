/**
 * One item's per-user state for the modal and the readers' pills: the reading position (the ONE
 * progress API — `/positions/{id}`) and the item marks (`/marks/items/{id}`), with the two toggles
 * and the star rating as mutations that name what they made stale. "Mark read" is `lastPage: -1`
 * — the only Finished signal — and "unmark" resets the position; nothing here ever finishes a book
 * by reaching its last page.
 */
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { fetchItemMark, getPosition, markFinished, putItemMark, resetPosition } from "./booksApi";
import { bk, invalidateAfter } from "./booksQuery";

export default function useItemState(itemId: number | null) {
  const qc = useQueryClient();
  const enabled = itemId != null && itemId > 0;
  const position = useQuery({ queryKey: bk.position(itemId ?? 0), queryFn: () => getPosition(itemId!), enabled });
  const mark = useQuery({ queryKey: bk.itemMark(itemId ?? 0), queryFn: () => fetchItemMark(itemId!), enabled });

  const isRead = position.data?.status === "finished";
  const wantToRead = mark.data?.wantToRead ?? position.data?.wantToRead ?? false;
  const rating = mark.data?.rating ?? null;

  const toggleRead = useMutation({
    mutationFn: async () => (isRead ? resetPosition(itemId!) : markFinished(itemId!)),
    onSettled: () => invalidateAfter(qc, { kind: "position", itemId: itemId! }),
  });
  const toggleWant = useMutation({
    mutationFn: async () => putItemMark(itemId!, { wantToRead: !wantToRead }),
    onSettled: () => invalidateAfter(qc, { kind: "itemMark", itemId: itemId! }),
  });
  const setRating = useMutation({
    mutationFn: async (value: number | null) => putItemMark(itemId!, { rating: value }),
    onSettled: () => invalidateAfter(qc, { kind: "itemMark", itemId: itemId! }),
  });

  return {
    position: position.data ?? null,
    isRead,
    wantToRead,
    rating,
    loading: position.isLoading || mark.isLoading,
    toggleRead: () => { if (enabled && !toggleRead.isPending) toggleRead.mutate(); },
    toggleWant: () => { if (enabled && !toggleWant.isPending) toggleWant.mutate(); },
    setRating: (value: number | null) => { if (enabled) setRating.mutate(value); },
  };
}
