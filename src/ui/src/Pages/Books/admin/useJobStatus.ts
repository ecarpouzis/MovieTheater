/**
 * Books' binding of the site's job observer (R9 S6: `src/ui/src/admin/useJobStatus` — SSE with a
 * 2 s poll behind it). All this file adds is WHICH server: the host's `/API/Books/admin/*`.
 */
import useSharedJobStatus, { JOB_POLL_MS, type UseJobStatus } from "../../../admin/useJobStatus";
import { booksJobApi } from "./adminApi";

export { JOB_POLL_MS };
export type { UseJobStatus };

export default function useJobStatus(kind: string, enabled = true): UseJobStatus {
  return useSharedJobStatus(kind, booksJobApi, enabled);
}
