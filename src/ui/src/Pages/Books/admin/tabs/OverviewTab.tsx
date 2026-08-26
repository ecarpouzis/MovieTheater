/**
 * Overview — the counts an operator checks first, the derived-table registry (what is stale and
 * which job rebuilds it, with a Rebuild button per row), and every job this host has run.
 */
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Button, Space, Statistic, Table, Tag, message } from "antd";
import { bk } from "../../booksQuery";
import { AdminApiError, fetchDerived, fetchInfo, recompute, RECOMPUTE_KINDS, type DerivedEntry, type JobStatus, type RecomputeKind } from "../adminApi";
import { JobStateTag } from "../JobCard";

/** The registry's `rebuildJob` is a verb ("books-resolve --series"); map it onto the recompute kind. */
export function recomputeKindFor(rebuildJob: string, name: string): RecomputeKind | null {
  const s = `${rebuildJob} ${name}`.toLowerCase();
  if (s.includes("reading-order")) return "reading-order";
  if (s.includes("collected")) return "collected-editions";
  if (s.includes("containment") || s.includes("collectionnode")) return "containment";
  if (s.includes("rating")) return "ratings";
  if (s.includes("--tags") || s.includes("fold")) return "tags";
  if (s.includes("--series") || /\bseries(alias)?\b/.test(s)) return "series";
  if (s.includes("resolve") || s.includes("fts") || s.includes("insight")) return "resolve";
  return (RECOMPUTE_KINDS as readonly string[]).includes(s.trim()) ? (s.trim() as RecomputeKind) : null;
}

const fmtDate = (s: string | null | undefined) => (s ? new Date(s).toLocaleString() : "—");

export default function OverviewTab() {
  const qc = useQueryClient();
  const info = useQuery({ queryKey: bk.admin("info"), queryFn: ({ signal }) => fetchInfo(signal), refetchInterval: 15000 });
  const derived = useQuery({ queryKey: bk.admin("derived"), queryFn: ({ signal }) => fetchDerived(signal), refetchInterval: 15000 });
  const rebuild = useMutation({
    mutationFn: (what: RecomputeKind) => recompute(what),
    onSuccess: (r, what) => { message.success(`Rebuild ${what}: started.`); void qc.invalidateQueries({ queryKey: bk.admin("info") }); },
    onError: (e, what) => {
      if (e instanceof AdminApiError && e.status === 409) message.warning(`Rebuild ${what} is already running.`);
      else message.error(e instanceof Error ? e.message : `Rebuild ${what} failed to start.`);
    },
  });

  const i = info.data;
  const running = new Set((i?.jobs ?? []).filter((j) => j.state === "running" || j.state === "stopping").map((j) => j.kind));

  return (
    <div className="bka-tab">
      {info.isError && <Alert type="error" showIcon title="The host's admin surface did not answer — is the Books host up, and is this account an admin there?" />}
      <div className="bka-stats">
        <Statistic title="Items" value={i?.catalog.items ?? 0} />
        <Statistic title="Comics" value={i?.catalog.comics ?? 0} />
        <Statistic title="Books" value={i?.catalog.books ?? 0} />
        <Statistic title="Series" value={i?.catalog.series ?? 0} />
        <Statistic title="Publishers" value={i?.catalog.publishers ?? 0} />
        <Statistic title="Folders" value={i?.catalog.folders ?? 0} />
        <Statistic title="Shadowed" value={i?.catalog.excluded ?? 0} />
        <Statistic title="Broken" value={i?.catalog.broken ?? 0} valueStyle={i && i.catalog.broken > 0 ? { color: "var(--rating-bad, #c0392b)" } : undefined} />
        <Statistic title="Open dedup groups" value={i?.openDedupGroups ?? 0} />
        <Statistic title="Pending links" value={i?.links.pending ?? 0} />
        <Statistic title="Multiple links" value={i?.links.multiple ?? 0} />
      </div>

      <section className="bka-card">
        <header className="bka-card-head"><div className="bka-card-text"><h3 className="bka-card-title">This host</h3></div></header>
        {i && (
          <div className="bka-facts">
            <span>Cache dir <Tag color={i.host.cacheDir ? "green" : "red"}>{i.host.cacheDir ? "configured" : "missing"}</Tag></span>
            <span>Media plane <Tag color={i.host.mediaPlane ? "green" : "red"}>{i.host.mediaPlane ? "configured" : "missing"}</Tag></span>
            <span>ComicVine key <Tag color={i.host.comicVineConfigured ? "green" : "default"}>{i.host.comicVineConfigured ? "set" : "not set"}</Tag></span>
            <span>Settings overlay <code>{i.host.settingsOverlay ?? "—"}</code></span>
            {i.lastScan && <span>Last scan {fmtDate(i.lastScan.startedAt)} — seen {i.lastScan.itemsSeen.toLocaleString()}, +{i.lastScan.added} / ~{i.lastScan.changed} / −{i.lastScan.removed}{i.lastScan.error ? ` · ${i.lastScan.error}` : ""}</span>}
          </div>
        )}
      </section>

      <section className="bka-card">
        <header className="bka-card-head">
          <div className="bka-card-text">
            <h3 className="bka-card-title">Derived tables</h3>
            <p className="bka-card-desc">What is computed from what. <b>Stale</b> means the inputs changed since the table was last rebuilt — a scan landed but the resolver has not run.</p>
          </div>
          <Space wrap>
            {RECOMPUTE_KINDS.map((k) => (
              <Button key={k} size="small" onClick={() => rebuild.mutate(k)} disabled={running.has(`recompute:${k}`) || rebuild.isPending}>{k}</Button>
            ))}
          </Space>
        </header>
        <Table<DerivedEntry>
          size="small"
          rowKey="name"
          pagination={false}
          loading={derived.isLoading}
          dataSource={derived.data ?? []}
          columns={[
            { title: "Table", dataIndex: "name", render: (v: string, r) => <span>{v} {r.stale && <Tag color="orange">stale</Tag>}</span> },
            { title: "Rows", dataIndex: "rowCount", align: "right", render: (v: number) => v.toLocaleString() },
            { title: "Rebuilt", dataIndex: "lastRebuiltAt", render: (v: string | null) => fmtDate(v) },
            { title: "Rebuild job", dataIndex: "rebuildJob", render: (v: string) => <code>{v}</code> },
            {
              title: "", key: "act", align: "right", render: (_v, r) => {
                const k = recomputeKindFor(r.rebuildJob, r.name);
                return k ? <Button size="small" type={r.stale ? "primary" : "default"} onClick={() => rebuild.mutate(k)} disabled={running.has(`recompute:${k}`)}>Rebuild</Button> : null;
              },
            },
          ]}
        />
      </section>

      <section className="bka-card">
        <header className="bka-card-head"><div className="bka-card-text"><h3 className="bka-card-title">Jobs this host has run</h3></div></header>
        <Table<JobStatus>
          size="small"
          rowKey="kind"
          pagination={false}
          dataSource={i?.jobs ?? []}
          locale={{ emptyText: "No job has run since the host started." }}
          columns={[
            { title: "Kind", dataIndex: "kind", render: (v: string) => <code>{v}</code> },
            { title: "State", key: "state", render: (_v, r) => <JobStateTag status={r} /> },
            { title: "Processed", dataIndex: "processed", align: "right", render: (v: number) => v.toLocaleString() },
            { title: "Remaining", dataIndex: "remaining", align: "right", render: (v: number) => v.toLocaleString() },
            { title: "Failed", dataIndex: "failed", align: "right" },
            { title: "Started", dataIndex: "startedAt", render: (v: string | null) => fmtDate(v) },
            { title: "Finished", dataIndex: "finishedAt", render: (v: string | null) => fmtDate(v) },
            { title: "Last line", dataIndex: "lastLine", ellipsis: true, render: (v: string | null, r) => r.error ? <span className="bka-warn">{r.error}</span> : v },
          ]}
        />
      </section>
    </div>
  );
}
