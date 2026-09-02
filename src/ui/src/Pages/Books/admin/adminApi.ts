/**
 * The operator's API — the R6 admin surface on the host (`/API/Books/admin/*`), typed against
 * `src/MovieTheater.Books/Controllers/AdminController.cs`. Every long operation there is a JOB: a
 * start answers 202 with the first batch's numbers and a status URL, the loop runs on the host,
 * a second start of the same kind is a 409, and a stop lands at the next batch boundary. Nothing
 * in this file loops; the tabs observe (`useJobStatus`) and, where a WHOLE list is wanted, page it
 * with `driveBatches`.
 *
 * Errors: the host answers `{ error }` bodies on 400/404/409; those become `AdminApiError.message`
 * so a tab can show the host's own sentence ("Root 3 still holds 412 items; scan it empty first.").
 */
import { AdminApiError, isRunning, jobPercent, type JobApi, type JobStart, type JobState, type JobStatus } from "../../../admin/jobs";
import { BOOKS_API, qs } from "../booksApi";

export const ADMIN_API = `${BOOKS_API}/admin`;

// The job vocabulary is the SITE's (R9 S6, `src/ui/src/admin/jobs`); re-exported here so the tabs'
// imports stay local and there is one definition of a job status on the site.
export { AdminApiError, isRunning, jobPercent };
export type { JobApi, JobStart, JobState, JobStatus };

async function request<T>(path: string, init?: RequestInit, signal?: AbortSignal): Promise<T> {
  const url = `${ADMIN_API}${path}`;
  const r = await fetch(url, { credentials: "same-origin", ...init, signal });
  if (r.status === 204) return null as T;
  if (!r.ok) {
    let msg = `${r.status}`;
    try {
      const body = await r.json();
      if (body && typeof body.error === "string") msg = body.error;
    } catch { /* no body */ }
    throw new AdminApiError(r.status, url, msg);
  }
  return (await r.json()) as T;
}

const json = (method: string, body?: unknown): RequestInit => ({
  method,
  headers: body === undefined ? undefined : { "Content-Type": "application/json" },
  body: body === undefined ? undefined : JSON.stringify(body),
});

// ── shapes ──

export interface AdminInfo {
  catalog: { roots: number; folders: number; items: number; comics: number; books: number; excluded: number; broken: number; series: number; publishers: number };
  derived: { readingOrder: number; collectionNodes: number; collectedEditionSpans: number; libraryRatings: number; itemTags: number; seriesTags: number };
  links: { seriesKeyLinks: number; itemProviderLinks: number; pending: number; multiple: number };
  dedupGroups: number;
  openDedupGroups: number;
  lastScan: { id: number; rootId: number | null; kind: string | null; startedAt: string | null; finishedAt: string | null; itemsSeen: number; added: number; changed: number; removed: number; error: string | null } | null;
  host: { cacheDir: boolean; mediaPlane: boolean; settingsOverlay: string | null; comicVineConfigured: boolean };
  jobs: JobStatus[];
}
export interface DerivedEntry { name: string; rebuildJob: string; lastRebuiltAt: string | null; rowCount: number; storedFingerprint: string | null; currentFingerprint: string | null; stale: boolean }

export interface ScanPreview { wouldAdd: number; wouldChange: number; wouldRemove: number; folders: number; files: number }
export interface ScanPhase { phase: string; processed: number; remaining: number; nextCursor: string | null; added: number; changed: number; removed: number; failed: number }
export interface ThumbsStatus { job: JobStatus | null; cursor: unknown; processed: number; generated: number; skipped: number; failed: number; remaining: number }
export interface BrokenRow { id: number; path: string; fileName: string; isBroken: boolean; brokenReason: string | null; thumbnailError: string | null; brokenCheckedAt: string | null; thumbnailCheckedAt: string | null }
export interface Paged<T> { totalCount: number; skip: number; top: number; items: T[] }

export type RootKind = "Comic" | "Book";
export interface LibraryRoot { id: number; path: string; kind: RootKind; isCalibre: boolean; enabled: boolean; reachable?: boolean }
export interface RootBody { path: string; kind: RootKind; isCalibre: boolean; enabled: boolean }

export interface ConfigKey { name: string; kind: "Secret" | "Int" | string; min: number | null; max: number | null; description: string | null }
export interface AdminConfig { path: string | null; writable: boolean; keys: ConfigKey[]; values: Record<string, unknown> }

export interface LogEntry { seq: number; at: string; level: string; category: string; message: string; exception: string | null }
export interface KidTag { category: string; tag: string; appliesTo: string | null; updatedAt: string | null }
export interface TagAlias { category: string; aliasTag: string; canonicalTag: string | null; source: string | null }
export interface NormalizeResult { dryRun: boolean; result: { aliasesApplied: number; eraRangesRemoved: number; crossCategoryRemoved: number; toneMatureMigrated: number }; next: string }

export interface DedupMember { duplicateGroupId: number; itemId: number; role: string | null; soleFileInFolder: boolean; path: string; fileName: string; fileSize: number; pageCount: number | null }
export interface DedupGroup { id: number; relationship: number | null; confidence: string | null; evidence: string | null; suggestedKeeperItemId: number | null; reviewState: string | null; detectedAt: string | null; members: DedupMember[] }
export interface DedupPage { totalCount: number; skip: number; top: number; groups: DedupGroup[] }

export interface MismatchSummary { series: number; linkedSeries: number; unlinkedSeries: number; pendingLinks: number; multipleLinks: number; openReviews: number; singleIssueSeries: number }
export interface EditResult { action: string; target: string; rowsChanged: number; rebuildRequired: boolean }
export interface SeriesAliasRow { parsedKey: string; items: number }
export interface LinkCandidate { id: number; name: string | null; publisher?: string | null; startYear?: number | null; issues?: number | null; score?: number | null }
export interface LinkCandidates { parsedKey: string; provider: string; status: string; providerKey: number | null; score: number | null; storedTopScore: number | null; candidatesInLegs: boolean; candidates?: LinkCandidate[] | null; attemptCount: number; attemptedAt: string | null; error: string | null }
export interface Decision { id: number; seriesKey: string | null; class: string | null; action: string | null; target: string | null; confidence: string | null; evidenceJson: string | null; state: string | null; undoJson: string | null; decidedBy: string | null; decidedAt: string | null }
export interface NameFix { seriesId: number; current: string; proposed: string; issueCount: number }
export interface Overmatch { seriesId: number; name: string | null; held: number; claimed: number; cvVolumeId: number }
export interface ComicVineStatus { configured: boolean; series: JobStatus | null; issues: JobStatus | null }

export const RECOMPUTE_KINDS = ["series", "resolve", "tags", "reading-order", "containment", "collected-editions", "ratings"] as const;
export type RecomputeKind = (typeof RECOMPUTE_KINDS)[number];

// ── info, registry, jobs ──

export const fetchInfo = (signal?: AbortSignal) => request<AdminInfo>("/info", undefined, signal);
export const fetchDerived = (signal?: AbortSignal) => request<DerivedEntry[]>("/derived", undefined, signal);
export const fetchJobs = (signal?: AbortSignal) => request<JobStatus[]>("/jobs/status", undefined, signal);
export const fetchJob = (kind: string, signal?: AbortSignal) => request<JobStatus>(`/jobs/status${qs({ kind })}`, undefined, signal);
export const stopJob = (kind: string) => request<JobStatus>(`/jobs/${encodeURIComponent(kind)}/stop`, json("POST"));
/** The SSE feed's URL for one job kind (the browser's EventSource carries the session cookie). */
export const jobEventsUrl = (kind: string) => `${ADMIN_API}/${encodeURIComponent(kind)}/events`;
export const recompute = (what: RecomputeKind, seriesId?: number) => request<JobStart>(`/recompute/${what}${qs({ seriesId })}`, json("POST"));

// ── library ──

export const scanPreview = (rootId?: number | null) => request<{ dryRun: true; preview: ScanPreview }>(`/scan/start${qs({ rootId })}`, json("POST"));
export const scanStart = (rootId?: number | null) => request<JobStart>(`/scan/start${qs({ rootId, apply: true })}`, json("POST"));
export const scanStatus = (signal?: AbortSignal) => request<{ job: JobStatus | null; phase: ScanPhase }>("/scan/status", undefined, signal);
export const thumbsStart = (reset = false) => request<JobStart>(`/thumbnails/start${qs({ reset })}`, json("POST"));
export const thumbsStatus = (signal?: AbortSignal) => request<ThumbsStatus>("/thumbnails/status", undefined, signal);
export const fetchBroken = (skip = 0, top = 100, signal?: AbortSignal) => request<Paged<BrokenRow>>(`/broken${qs({ skip, top })}`, undefined, signal);
export const fetchRoots = (signal?: AbortSignal) => request<LibraryRoot[]>("/roots", undefined, signal);
export const addRoot = (body: RootBody) => request<LibraryRoot>("/roots", json("POST", body));
export const updateRoot = (id: number, body: RootBody) => request<LibraryRoot>(`/roots/${id}`, json("PUT", body));
export const deleteRoot = (id: number) => request<null>(`/roots/${id}`, json("DELETE"));
export const calibreImport = (p: { metadata?: string; link?: string; libraryRoot?: string; apply: boolean }) => request<JobStart>(`/calibre/import${qs(p)}`, json("POST"));

// ── cache, icons, config, logs ──

export const cacheClear = (apply: boolean) => request<{ dryRun?: boolean; wouldDelete?: number; deleted?: number; kept: number }>(`/cache/clear${qs({ apply })}`, json("POST"));
export async function uploadFolderIcon(folderId: number, file: File): Promise<{ folderId: number; hasIcon: boolean }> {
  const form = new FormData();
  form.append("file", file);
  return request(`/folders/${folderId}/icon`, { method: "POST", body: form });
}
export const deleteFolderIcon = (folderId: number) => request<null>(`/folders/${folderId}/icon`, json("DELETE"));
export const fetchConfig = (signal?: AbortSignal) => request<AdminConfig>("/config", undefined, signal);
export const putConfig = (values: Record<string, unknown>) => request<Record<string, unknown>>("/config", json("PUT", values));
export const fetchLogs = (p: { count?: number; level?: string; afterSeq?: number } = {}, signal?: AbortSignal) => request<{ capacity: number; entries: LogEntry[] }>(`/logs${qs(p)}`, undefined, signal);
export const clearLogs = () => request<null>("/logs", json("DELETE"));

// ── kids, normalization ──

export const fetchKidTags = (signal?: AbortSignal) => request<KidTag[]>("/kids-tags", undefined, signal);
export const putKidTag = (body: { category: string; tag: string; appliesTo: string | null }) => request<KidTag>("/kids-tags", json("PUT", body));
export const deleteKidTag = (category: string, tag: string) => request<null>(`/kids-tags/${encodeURIComponent(category)}/${encodeURIComponent(tag)}`, json("DELETE"));
export const fetchAliases = (signal?: AbortSignal) => request<TagAlias[]>("/normalization/aliases", undefined, signal);
export const putAlias = (body: { category: string; aliasTag: string; canonicalTag: string }) => request<TagAlias>("/normalization/aliases", json("PUT", body));
export const deleteAlias = (category: string, aliasTag: string) => request<null>(`/normalization/aliases/${encodeURIComponent(category)}/${encodeURIComponent(aliasTag)}`, json("DELETE"));
export const normalizeTags = (apply: boolean) => request<NormalizeResult>(`/normalization/apply${qs({ apply })}`, json("POST"));

// ── dedup ──

export const dedupStart = (reset = true) => request<JobStart>(`/dedup/start${qs({ reset })}`, json("POST"));
export const fetchDedup = (state = "Pending", skip = 0, top = 50, signal?: AbortSignal) => request<DedupPage>(`/dedup${qs({ state, skip, top })}`, undefined, signal);
export const dedupResolve = (id: number, keeperItemId?: number) => request<{ groupId: number; hidden: number }>(`/dedup/${id}/resolve${qs({ keeperItemId })}`, json("POST"));

// ── series reconciliation ──

export const fetchMismatchSummary = (signal?: AbortSignal) => request<MismatchSummary>("/series/summary", undefined, signal);
export const fetchSeriesAliases = (id: number, signal?: AbortSignal) => request<SeriesAliasRow[]>(`/series/${id}/aliases`, undefined, signal);
export const fetchLinkCandidates = (parsedKey: string, provider = "Cv", signal?: AbortSignal) => request<LinkCandidates>(`/series/link-candidates${qs({ parsedKey, provider })}`, undefined, signal);
export const clearLink = (parsedKey: string, provider = "Cv") => request<EditResult>("/series/clear-link", json("POST", { parsedKey, provider, providerKey: null }));
export const setLink = (parsedKey: string, providerKey: number, provider = "Cv") => request<EditResult>("/series/set-link", json("POST", { parsedKey, provider, providerKey }));
export const foldKey = (fromKey: string, toKey: string) => request<EditResult>("/series/fold", json("POST", { fromKey, toKey }));
export const unifyFolder = (folderId: number, parsedKey: string) => request<EditResult>("/series/unify-folder", json("POST", { folderId, parsedKey }));
export const markReviewed = (body: { scope: string; key: string; state: string; note?: string | null }) => request<EditResult>("/series/review", json("POST", body));
export const fetchDecisions = (state: string | undefined, skip: number, top: number, signal?: AbortSignal) => request<Decision[]>(`/series/decisions${qs({ state, skip, top })}`, undefined, signal);
export const revertDecision = (id: number) => request<EditResult>(`/series/decisions/${id}/revert`, json("POST"));
export const setOverride = (seriesId: number, displayName: string | null) => request<EditResult>(`/series/${seriesId}/override`, json("PUT", { displayName }));
export const setFranchise = (seriesId: number, franchise: string | null) => request<EditResult>(`/series/${seriesId}/franchise`, json("PUT", { franchise }));
export const nameFix = (apply: boolean, signal?: AbortSignal) => request<{ dryRun: boolean; fixes: NameFix[] }>(`/series/namefix${qs({ apply })}`, undefined, signal);
export const prune = (apply: boolean) => request<{ dryRun: boolean; candidates: number; deleted: number }>(`/series/prune${qs({ apply })}`, json("POST"));
export const fetchOvermatch = (ratio = 2, minIssues = 20, signal?: AbortSignal) => request<Overmatch[]>(`/series/split-overmatch${qs({ ratio, minIssues })}`, undefined, signal);

// ── providers ──

export const comicVineStart = (mode: "series" | "issues") => request<JobStart>(`/comicvine/start${qs({ mode })}`, json("POST"));
export const comicVineStatus = (signal?: AbortSignal) => request<ComicVineStatus>("/comicvine/status", undefined, signal);
export const externalStart = () => request<JobStart>("/external/start", json("POST"));
export const externalStatus = (signal?: AbortSignal) => request<JobStatus | null>("/external/status", undefined, signal);

/** What the shared shell's cards call to observe and stop a Books job. */
export const booksJobApi: JobApi = {
  fetchJob: (kind, signal) => fetchJob(kind, signal),
  stopJob: (kind) => stopJob(kind),
  eventsUrl: (kind) => jobEventsUrl(kind),
};
