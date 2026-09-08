/**
 * Containment review — the 718 questions the model pass could not answer alone.
 *
 * The pass read every series folder and judged 20,498 collected editions. Where the shelf itself is
 * ambiguous it refused and said why: three relaunch ladders each numbered from #1, a 24-page single
 * issue wearing a `tpb` format, two rips of one volume, a provider row naming a different book. Each
 * of those is a row here, with the file, the span it currently carries and the rest of its shelf, so
 * the answer is visible without opening anything.
 *
 * Two verbs: decide the flag (Accepted = it was right, the item stays untrusted; Dismissed = false
 * alarm, the de-duplication may use it again), or type the range yourself — which writes a Curated
 * span at confidence 1.0 in your name and outranks everything the pass or a provider inferred.
 */
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, Input, InputNumber, Popconfirm, Segmented, Select, Space, Tag, Tooltip, message } from "antd";
import { useState } from "react";
import { thumbUrl } from "../../booksMedia";
import { bk } from "../../booksQuery";
import {
  clearContainmentSpan, containedDedupStart, decideContainmentFlag, fetchContainmentFlags,
  fetchContainmentSummary, fetchOverlaps, recompute, setContainmentSpan,
  type ContainmentFlag, type OverlapGroup,
} from "../adminApi";

const SPAN_SOURCE: Record<number, string> = { 0: "none", 1: "inferred", 2: "ComicVine", 3: "GCD", 4: "LOCG", 5: "curated" };
const EDITION_SOURCE: Record<number, string> = { 0: "LOCG", 1: "GCD", 2: "ComicVine", 3: "curated" };

/** What each flag actually means, in one line, so the queue does not need a legend elsewhere. */
const FLAG_HELP: Record<string, string> = {
  "overlap-in-series": "Several runs share this Series and each numbers from #1, so an issue number is ambiguous here.",
  "conflated-series": "One provider range is repeated across the whole shelf — the leg is guessing, not reading.",
  "label-ambiguous": "The file is labelled like a collection but does not read like one — usually a single issue with a tpb format, or a second copy of a volume already held.",
  "provider-disagrees": "The legs contradict each other, or one of them names a different book entirely.",
  "arithmetic-odd": "The page count cannot hold the number of issues the range claims.",
  "duplicate-edition": "Two items in this series claim the same block of issues — two editions, or two rips of one.",
  "span-retracted": "The pass withdrew a range it had written; the audit found it unsafe.",
};

function fmt(n: number | null | undefined) {
  return n === null || n === undefined ? "?" : Number.isInteger(n) ? String(n) : n.toFixed(2).replace(/\.?0+$/, "");
}

function Flag({ f, onDone }: { f: ContainmentFlag; onDone: () => void }) {
  const curated = f.spans.find((s) => s.source === 3);
  const [start, setStart] = useState<number | null>(curated?.issueStart ?? null);
  const [end, setEnd] = useState<number | null>(curated?.issueEnd ?? null);
  const [note, setNote] = useState("");

  const decide = useMutation({
    mutationFn: (state: string) => decideContainmentFlag(f.id, state, note || undefined),
    onSuccess: (r) => { message.success(`Flag ${f.id}: ${r.reviewState}.`); onDone(); },
    onError: (e) => message.error(e instanceof Error ? e.message : "Refused."),
  });
  const save = useMutation({
    mutationFn: () => setContainmentSpan(f.itemId, start as number, end as number, undefined, note || undefined),
    onSuccess: (r) => { message.success(`Item ${r.itemId} now collects #${fmt(r.start)}-${fmt(r.end)}. Rebuild containment to see it.`); onDone(); },
    onError: (e) => message.error(e instanceof Error ? e.message : "Refused."),
  });
  const clear = useMutation({
    mutationFn: () => clearContainmentSpan(f.itemId),
    onSuccess: () => { message.success(`Item ${f.itemId} contains nothing known.`); onDone(); },
    onError: (e) => message.error(e instanceof Error ? e.message : "Refused."),
  });

  const pending = f.reviewState === "Pending";
  const rangeOk = start !== null && end !== null && end >= start;

  return (
    <div className="adm-group">
      <div className="adm-group-head">
        <Tooltip title={FLAG_HELP[f.flag ?? ""] ?? undefined}><Tag color={pending ? "orange" : "default"}>{f.flag}</Tag></Tooltip>
        <span className="adm-muted">{f.series ?? `series ${f.seriesId ?? "?"}`}</span>
        <span className="adm-muted">#{f.id}</span>
        {!pending && <Tag style={{ marginLeft: "auto" }}>{f.reviewState}{f.decidedBy ? ` · ${f.decidedBy}` : ""}</Tag>}
      </div>

      <div className="adm-member">
        <img src={thumbUrl(f.itemId) ?? undefined} alt="" loading="lazy" />
        <div className="adm-member-text">
          <div><b>{f.fileName ?? `item ${f.itemId}`}</b> {f.isExcluded && <Tag>hidden</Tag>}</div>
          <div>{f.detail}</div>
          <div className="adm-muted">{f.pageCount ?? "?"} pp · <code>#{f.itemId}</code></div>
          <div className="adm-muted">
            {f.spans.length === 0
              ? "no span from any source"
              : f.spans.map((s) => (
                  <span key={s.source} style={{ marginRight: 12 }}>
                    {EDITION_SOURCE[s.source] ?? s.source} #{fmt(s.issueStart)}-{fmt(s.issueEnd)}
                    {s.confidence != null && ` (${s.confidence})`}
                    {s.providerRef && !s.providerRef.startsWith("model:") && <Tag color="green" style={{ marginLeft: 4 }}>gold</Tag>}
                  </span>
                ))}
          </div>
          {curated?.note && <div className="adm-muted"><i>{curated.note}</i></div>}
        </div>
      </div>

      {f.shelf.length > 1 && (
        <details className="adm-muted" style={{ margin: "4px 0 8px 8px" }}>
          <summary>The rest of the shelf ({f.shelf.length})</summary>
          <ul style={{ margin: "4px 0 0 16px", padding: 0 }}>
            {f.shelf.map((s) => (
              <li key={s.itemId}>
                {s.fileName} — {s.spanLabel ?? "no span"}
                {s.spanSource != null && ` · ${SPAN_SOURCE[s.spanSource] ?? s.spanSource}`}
                {s.containsCount ? ` · holds ${s.containsCount}` : ""}
              </li>
            ))}
          </ul>
        </details>
      )}

      <Space wrap style={{ marginLeft: 8 }}>
        <span className="adm-muted">Collects issues</span>
        <InputNumber size="small" placeholder="from" value={start} onChange={setStart} style={{ width: 84 }} />
        <InputNumber size="small" placeholder="to" value={end} onChange={setEnd} style={{ width: 84 }} />
        <Button size="small" type="primary" disabled={!rangeOk} loading={save.isPending} onClick={() => save.mutate()}>Save span</Button>
        {curated && (
          <Popconfirm title="Withdraw the span? The book will contain nothing known." onConfirm={() => clear.mutate()}>
            <Button size="small" danger loading={clear.isPending}>Clear span</Button>
          </Popconfirm>
        )}
        <Input size="small" placeholder="note (optional)" value={note} onChange={(e) => setNote(e.target.value)} style={{ width: 260 }} />
        <Button size="small" loading={decide.isPending} onClick={() => decide.mutate("Accepted")}>Flag stands</Button>
        <Button size="small" loading={decide.isPending} onClick={() => decide.mutate("Dismissed")}>False alarm</Button>
      </Space>
    </div>
  );
}

/**
 * One overlap: a collected edition and the single issues of it you also hold as separate files.
 * There is no keeper to pick — owning both is legitimate — so this only ever shows you the overlap.
 */
function Overlap({ g }: { g: OverlapGroup }) {
  const collection = g.members.find((m) => m.role === "Collection");
  const contained = g.members.filter((m) => m.role !== "Collection");
  return (
    <div className="adm-group">
      <div className="adm-group-head">
        <Tag color={g.confidence === "High" ? "green" : "orange"}>{g.confidence ?? "?"}</Tag>
        <span className="adm-muted">{g.evidence}</span>
        <span className="adm-muted">#{g.id}</span>
      </div>
      <div className="adm-member">
        <img src={collection ? thumbUrl(collection.itemId) ?? undefined : undefined} alt="" loading="lazy" />
        <div className="adm-member-text">
          <div><b>{collection?.fileName ?? "(the collected edition)"}</b></div>
          <div className="adm-muted">{collection?.pageCount ?? "?"} pp · <code>#{collection?.itemId}</code></div>
          <details className="adm-muted">
            <summary>{contained.length} single issues it already holds</summary>
            <ul style={{ margin: "4px 0 0 16px", padding: 0 }}>
              {contained.map((m) => <li key={m.itemId}>{m.fileName} <code>#{m.itemId}</code></li>)}
            </ul>
          </details>
        </div>
      </div>
    </div>
  );
}

export default function ContainmentTab() {
  const qc = useQueryClient();
  const [state, setState] = useState("Pending");
  const [flag, setFlag] = useState<string | undefined>(undefined);
  const [page, setPage] = useState(0);
  const top = 25;

  const [overlapPage, setOverlapPage] = useState(0);
  const summary = useQuery({ queryKey: bk.admin("containment", "summary"), queryFn: ({ signal }) => fetchContainmentSummary(signal) });
  const overlaps = useQuery({
    queryKey: bk.admin("containment", "overlaps", overlapPage),
    queryFn: ({ signal }) => fetchOverlaps(overlapPage * 25, 25, signal),
  });
  const flags = useQuery({
    queryKey: bk.admin("containment", state, flag ?? "all", page),
    queryFn: ({ signal }) => fetchContainmentFlags(state, flag, page * top, top, signal),
  });
  const refresh = () => { void qc.invalidateQueries({ queryKey: bk.admin("containment") }); };
  const total = flags.data?.totalCount ?? 0;
  const s = summary.data;
  // The site and the Books host deploy separately, so this tab can reach a host that predates it.
  // Say so, rather than showing an empty queue that looks like there is nothing to review.
  const notDeployed = (summary.error as { status?: number } | null)?.status === 404;

  // A span you typed changes what the editions collect; CollectionNode is derived from it and has to be
  // rebuilt before the shelf, the reading order or the overlap groups agree with you.
  const rebuild = useMutation({
    mutationFn: () => recompute("containment"),
    onSuccess: () => message.success("Containment rebuild started — watch it on the Overview tab."),
    onError: (e) => message.error(e instanceof Error ? e.message : "Could not start the rebuild."),
  });
  // …and then re-derive the overlap groups, so a corrected span cannot leave a stale one behind.
  const regroup = useMutation({
    mutationFn: () => containedDedupStart(true),
    onSuccess: () => message.success("Overlap groups re-deriving — they land in the Duplicates tab."),
    onError: (e) => message.error(e instanceof Error ? e.message : "Could not start the re-derivation."),
  });

  if (notDeployed) {
    return (
      <div className="adm-tab">
        <section className="adm-card">
          <div className="adm-card-text">
            <h3 className="adm-card-title">The host has not been deployed yet</h3>
            <p className="adm-card-desc">
              This screen ships in the Books host binary, which updates only through an elevated
              <code> .\scripts\deploy-books-host.ps1</code>. The containment data itself is already live.
            </p>
          </div>
        </section>
      </div>
    );
  }

  return (
    <div className="adm-tab">
      <section className="adm-card">
        <header className="adm-card-head">
          <div className="adm-card-text">
            <h3 className="adm-card-title">What the editions collect</h3>
            <p className="adm-card-desc">
              A collected edition's range decides which single issues it makes redundant, so the file
              de-duplication reads it. Judged ranges are trusted; a flagged one never is.
            </p>
          </div>
          <Space wrap>
            <Button size="small" loading={rebuild.isPending} onClick={() => rebuild.mutate()}>Rebuild containment</Button>
            <Button size="small" loading={regroup.isPending} onClick={() => regroup.mutate()}>Re-derive overlaps</Button>
          </Space>
        </header>
        <div className="adm-tiles">
          <div><b>{(s?.curatedSpans.model ?? 0).toLocaleString()}</b><span className="adm-muted">judged spans</span></div>
          <div><b>{(s?.curatedSpans.gold ?? 0).toLocaleString()}</b><span className="adm-muted">from indicia or by hand</span></div>
          <div><b>{(s?.containedGroups ?? 0).toLocaleString()}</b><span className="adm-muted">overlap groups</span></div>
          <div><b>{(s?.flags.reduce((n, f) => n + f.pending, 0) ?? 0).toLocaleString()}</b><span className="adm-muted">flags to review</span></div>
        </div>
      </section>

      <section className="adm-card">
        <header className="adm-card-head">
          <div className="adm-card-text">
            <h3 className="adm-card-title">Review queue</h3>
            <p className="adm-card-desc">
              Type the right range and it becomes yours — Curated, confidence 1.0, and the pass will not
              overwrite it. Or rule on the flag: <b>Flag stands</b> keeps the item out of the de-duplication,
              <b> False alarm</b> lets it back in.
            </p>
          </div>
          <Space wrap>
            <Segmented options={["Pending", "Accepted", "Dismissed", "All"]} value={state} onChange={(v) => { setState(String(v)); setPage(0); }} />
            <Select
              size="small" allowClear placeholder="every flag" style={{ minWidth: 190 }} value={flag}
              onChange={(v) => { setFlag(v); setPage(0); }}
              options={(s?.flags ?? []).map((f) => ({ value: f.flag, label: `${f.flag} (${f.pending})` }))}
            />
            <span className="adm-muted">{total.toLocaleString()} rows</span>
            <Button size="small" disabled={page === 0} onClick={() => setPage((p) => p - 1)}>Prev</Button>
            <Button size="small" disabled={(page + 1) * top >= total} onClick={() => setPage((p) => p + 1)}>Next</Button>
          </Space>
        </header>
        {flags.isLoading && <div className="adm-muted">Loading…</div>}
        {!flags.isLoading && total === 0 && <div className="adm-muted">Nothing {state.toLowerCase()}.</div>}
        {(flags.data?.items ?? []).map((f) => <Flag key={f.id} f={f} onDone={refresh} />)}
      </section>

      <section className="adm-card">
        <header className="adm-card-head">
          <div className="adm-card-text">
            <h3 className="adm-card-title">Overlaps</h3>
            <p className="adm-card-desc">
              Where a trusted range means you hold the same reading twice — the collected edition and the
              floppies inside it. No signature can see this, so nothing else finds it. Nothing here is a
              mistake: keeping both is normal, and these groups are never resolvable.
            </p>
          </div>
          <Space wrap>
            <span className="adm-muted">{(overlaps.data?.totalCount ?? 0).toLocaleString()} groups</span>
            <Button size="small" disabled={overlapPage === 0} onClick={() => setOverlapPage((p) => p - 1)}>Prev</Button>
            <Button size="small" disabled={(overlapPage + 1) * 25 >= (overlaps.data?.totalCount ?? 0)} onClick={() => setOverlapPage((p) => p + 1)}>Next</Button>
          </Space>
        </header>
        {overlaps.isLoading && <div className="adm-muted">Loading…</div>}
        {!overlaps.isLoading && (overlaps.data?.totalCount ?? 0) === 0 && (
          <div className="adm-muted">None yet — run <b>Re-derive overlaps</b> above.</div>
        )}
        {(overlaps.data?.groups ?? []).map((g) => <Overlap key={g.id} g={g} />)}
      </section>
    </div>
  );
}
