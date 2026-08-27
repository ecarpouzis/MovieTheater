/**
 * The result count on a rail's head line: one 1-row page per facet state, held five minutes, shared
 * by every surface that shows it (the sider rail and the phone sheet ask the same query key, so the
 * two agree and only one request goes out). Every section wrote this same query by hand before —
 * only the request and the envelope's count field differ.
 *
 * `useCountQuery` is the bare form for a section whose API client already returns a number;
 * `useResultCount` is the common one: hand it a request, it reads `totalCount` or `total` off the
 * JSON envelope (-1 when the endpoint does not count).
 */
import { useQuery, type QueryKey } from "@tanstack/react-query";

export const COUNT_STALE_MS = 5 * 60 * 1000;

export interface CountContext { signal: AbortSignal }

export function useCountQuery(queryKey: QueryKey, count: (ctx: CountContext) => Promise<number>, enabled = true) {
  return useQuery({
    queryKey,
    queryFn: ({ signal }) => count({ signal }),
    enabled,
    staleTime: COUNT_STALE_MS,
  });
}

/** The count off a paged browse envelope: `totalCount` (the movie/arcade shape) or `total` (the rest). */
export async function readCount(response: Response): Promise<number> {
  if (!response.ok) throw new Error(`count → ${response.status}`);
  const data = (await response.json()) as { totalCount?: number; total?: number };
  if (typeof data.totalCount === "number") return data.totalCount;
  if (typeof data.total === "number") return data.total;
  return -1;
}

export default function useResultCount(queryKey: QueryKey, request: (ctx: CountContext) => Promise<Response>, enabled = true) {
  return useCountQuery(queryKey, async (ctx) => readCount(await request(ctx)), enabled);
}
