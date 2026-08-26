/**
 * The counts the Books rail shows beside its index (catalog size, novels, what you are part-way
 * through). Per-user (the gate decides what counts), so React Query — never `useCachedResource`.
 * Asked only for a member on a password session, only while on /books, refreshed every few minutes.
 */
import { useQuery } from "@tanstack/react-query";
import { fetchCatalog, fetchContinue, fetchNovels } from "../Pages/Books/booksApi";
import type { BooksIndexCounts, BooksMe } from "../Pages/Books/booksNav";
import { bk } from "../Pages/Books/booksQuery";

export default function useBooksIndex(me: BooksMe | null | undefined, enabled = true): BooksIndexCounts {
  const ok = !!me?.booksAccess && !!me?.hasPassword && enabled;
  const query = useQuery({
    queryKey: [...bk.index(), me?.username ?? ""],
    enabled: ok,
    staleTime: 3 * 60 * 1000,
    queryFn: async (): Promise<BooksIndexCounts> => {
      const [catalog, novels, cont] = await Promise.all([
        fetchCatalog({ kind: "comic", top: 1, count: true }).then((r) => r.total).catch(() => null),
        fetchNovels({ top: 1 }).then((r) => r.total).catch(() => null),
        fetchContinue(0, 1).then((r) => r.totalCount).catch(() => null),
      ]);
      return { catalog: catalog != null && catalog >= 0 ? catalog : null, novels, continueReading: cont };
    },
  });
  return query.data ?? {};
}
