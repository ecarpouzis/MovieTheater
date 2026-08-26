/**
 * Duplicates — the detection job, then the groups to review. Resolving a group hides the losers
 * (`IsExcluded`; they stay in the Directory drill); no file on the share is ever touched.
 */
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, Popconfirm, Radio, Segmented, Space, Tag, message } from "antd";
import { useState } from "react";
import { thumbUrl } from "../../booksMedia";
import { bk } from "../../booksQuery";
import { dedupResolve, dedupStart, fetchDedup, type DedupGroup } from "../adminApi";
import JobCard from "../JobCard";

const RELATIONSHIP: Record<number, string> = { 0: "Identical file", 1: "Same comic, different scan", 2: "Contained in" };
const fmtSize = (b: number) => (b >= 1 << 30 ? `${(b / (1 << 30)).toFixed(2)} GB` : b >= 1 << 20 ? `${(b / (1 << 20)).toFixed(1)} MB` : `${(b / 1024).toFixed(0)} KB`);

function Group({ g, onResolved }: { g: DedupGroup; onResolved: () => void }) {
  const [keeper, setKeeper] = useState<number | null>(g.suggestedKeeperItemId ?? g.members[0]?.itemId ?? null);
  const resolve = useMutation({ mutationFn: () => dedupResolve(g.id, keeper ?? undefined), onSuccess: (r) => { message.success(`Group ${g.id}: hid ${r.hidden}.`); onResolved(); }, onError: (e) => message.error(e instanceof Error ? e.message : "Resolve refused.") });
  const pending = g.reviewState === "Pending";
  return (
    <div className="bka-group">
      <div className="bka-group-head">
        <Tag color={g.confidence === "High" ? "green" : "orange"}>{g.confidence ?? "?"}</Tag>
        <span>{RELATIONSHIP[g.relationship ?? -1] ?? `relationship ${g.relationship}`}</span>
        <span className="bka-muted">{g.evidence}</span>
        <span className="bka-muted">#{g.id}</span>
        {pending && (
          <Popconfirm title="Hide every member except the keeper?" onConfirm={() => resolve.mutate()}><Button size="small" type="primary" disabled={keeper == null} loading={resolve.isPending} style={{ marginLeft: "auto" }}>Hide the rest</Button></Popconfirm>
        )}
        {!pending && <Tag style={{ marginLeft: "auto" }}>{g.reviewState}</Tag>}
      </div>
      <Radio.Group value={keeper} onChange={(e) => setKeeper(e.target.value)} disabled={!pending} className="bka-members">
        {g.members.map((m) => (
          <label key={m.itemId} className="bka-member">
            <Radio value={m.itemId} />
            <img src={thumbUrl(m.itemId) ?? undefined} alt="" loading="lazy" />
            <div className="bka-member-text">
              <div><b>{m.fileName}</b> {m.role && <Tag>{m.role}</Tag>}{m.soleFileInFolder && <Tag color="orange">only file in folder</Tag>}{g.suggestedKeeperItemId === m.itemId && <Tag color="green">suggested keeper</Tag>}</div>
              <div className="bka-muted"><code>{m.path}</code></div>
              <div className="bka-muted">{m.pageCount ?? "?"} pp · {fmtSize(m.fileSize)} · <code>#{m.itemId}</code></div>
            </div>
          </label>
        ))}
      </Radio.Group>
    </div>
  );
}

export default function DuplicatesTab() {
  const qc = useQueryClient();
  const [state, setState] = useState("Pending");
  const [page, setPage] = useState(0);
  const top = 25;
  const groups = useQuery({ queryKey: bk.admin("dedup", state, page), queryFn: ({ signal }) => fetchDedup(state, page * top, top, signal) });
  const refresh = () => { void qc.invalidateQueries({ queryKey: bk.admin("dedup") }); void qc.invalidateQueries({ queryKey: bk.admin("info") }); };
  const total = groups.data?.totalCount ?? 0;
  return (
    <div className="bka-tab">
      <JobCard kind="dedup" title="Duplicate detection" description="Cover similarity + file size + page count. Restarting resets the previous detection and re-scores everything." start={() => dedupStart(true)} onStarted={refresh} />
      <section className="bka-card">
        <header className="bka-card-head">
          <div className="bka-card-text"><h3 className="bka-card-title">Groups</h3><p className="bka-card-desc">Pick the keeper, then hide the rest. A hidden duplicate leaves every browse surface but stays in the Directory, dimmed, because the file really lives there.</p></div>
          <Space wrap>
            <Segmented options={["Pending", "Resolved"]} value={state} onChange={(v) => { setState(String(v)); setPage(0); }} />
            <span className="bka-muted">{total.toLocaleString()} groups</span>
            <Button size="small" disabled={page === 0} onClick={() => setPage((p) => p - 1)}>Prev</Button>
            <Button size="small" disabled={(page + 1) * top >= total} onClick={() => setPage((p) => p + 1)}>Next</Button>
          </Space>
        </header>
        {groups.isLoading && <div className="bka-muted">Loading…</div>}
        {!groups.isLoading && total === 0 && <div className="bka-muted">No {state.toLowerCase()} groups.</div>}
        {(groups.data?.groups ?? []).map((g) => <Group key={g.id} g={g} onResolved={refresh} />)}
      </section>
    </div>
  );
}
