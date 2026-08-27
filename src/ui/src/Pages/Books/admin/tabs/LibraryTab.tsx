/**
 * Library — the roots, the scan (PREVIEW first: what it would add/change/remove; Apply asks), the
 * thumbnail pass, the Calibre import, and the broken files. Every long thing is a JobCard.
 */
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Button, Input, Popconfirm, Select, Space, Switch, Table, Tag, message } from "antd";
import { useState } from "react";
import { bk } from "../../booksQuery";
import {
  addRoot, calibreImport, deleteRoot, fetchBroken, fetchRoots, scanPreview, scanStart, scanStatus, thumbsStart, thumbsStatus, updateRoot,
  type BrokenRow, type LibraryRoot, type RootKind, type ScanPreview,
} from "../adminApi";
import JobCard from "../JobCard";

function RootsCard() {
  const qc = useQueryClient();
  const roots = useQuery({ queryKey: bk.admin("roots"), queryFn: ({ signal }) => fetchRoots(signal) });
  const [draft, setDraft] = useState<{ path: string; kind: RootKind; isCalibre: boolean; enabled: boolean }>({ path: "", kind: "Comic", isCalibre: false, enabled: true });
  const invalidate = () => qc.invalidateQueries({ queryKey: bk.admin("roots") });
  const add = useMutation({ mutationFn: () => addRoot(draft), onSuccess: () => { setDraft({ path: "", kind: "Comic", isCalibre: false, enabled: true }); void invalidate(); message.success("Root added."); }, onError: (e) => message.error(e instanceof Error ? e.message : "Add failed.") });
  const update = useMutation({ mutationFn: (r: LibraryRoot) => updateRoot(r.id, { path: r.path, kind: r.kind, isCalibre: r.isCalibre, enabled: r.enabled }), onSuccess: () => void invalidate(), onError: (e) => message.error(e instanceof Error ? e.message : "Update failed.") });
  const remove = useMutation({ mutationFn: (id: number) => deleteRoot(id), onSuccess: () => { void invalidate(); message.success("Root removed."); }, onError: (e) => message.error(e instanceof Error ? e.message : "Delete refused.") });
  return (
    <section className="adm-card">
      <header className="adm-card-head">
        <div className="adm-card-text">
          <h3 className="adm-card-title">Library roots</h3>
          <p className="adm-card-desc">The folders the scan walks. A root that still holds items cannot be removed — scan it empty first.</p>
        </div>
      </header>
      <Table<LibraryRoot>
        size="small" rowKey="id" pagination={false} loading={roots.isLoading} dataSource={roots.data ?? []}
        columns={[
          { title: "Path", dataIndex: "path", render: (v: string, r) => <span><code>{v}</code> {r.reachable === false && <Tag color="red">unreachable</Tag>}</span> },
          { title: "Kind", dataIndex: "kind" },
          { title: "Calibre", dataIndex: "isCalibre", render: (v: boolean, r) => <Switch size="small" checked={v} onChange={(on) => update.mutate({ ...r, isCalibre: on })} /> },
          { title: "Enabled", dataIndex: "enabled", render: (v: boolean, r) => <Switch size="small" checked={v} onChange={(on) => update.mutate({ ...r, enabled: on })} /> },
          { title: "", key: "act", align: "right", render: (_v, r) => <Popconfirm title="Remove this root?" description="Refused while it holds items." onConfirm={() => remove.mutate(r.id)}><Button size="small" danger>Remove</Button></Popconfirm> },
        ]}
      />
      <div className="adm-form-row">
        <Input placeholder="New root path" value={draft.path} onChange={(e) => setDraft({ ...draft, path: e.target.value })} style={{ maxWidth: 420 }} />
        <Select<RootKind> value={draft.kind} onChange={(kind) => setDraft({ ...draft, kind })} options={[{ value: "Comic", label: "Comics" }, { value: "Book", label: "Books" }]} style={{ width: 120 }} />
        <label className="adm-inline"><Switch size="small" checked={draft.isCalibre} onChange={(on) => setDraft({ ...draft, isCalibre: on })} /> Calibre library</label>
        <Button type="primary" onClick={() => add.mutate()} disabled={!draft.path.trim() || add.isPending}>Add root</Button>
      </div>
    </section>
  );
}

function ScanCard() {
  const roots = useQuery({ queryKey: bk.admin("roots"), queryFn: ({ signal }) => fetchRoots(signal) });
  const [rootId, setRootId] = useState<number>(0); // 0 = every root
  const [preview, setPreview] = useState<ScanPreview | null>(null);
  const phase = useQuery({ queryKey: bk.admin("scan-status"), queryFn: ({ signal }) => scanStatus(signal), refetchInterval: 3000 });
  const doPreview = useMutation({ mutationFn: () => scanPreview(rootId || undefined), onSuccess: (r) => setPreview(r.preview), onError: (e) => message.error(e instanceof Error ? e.message : "Preview failed.") });
  const p = phase.data?.phase;
  return (
    <JobCard
      kind="scan"
      title="Library scan"
      description={<>Walks the roots read-only and reconciles the catalog. <b>Preview first</b> — nothing is written until Apply. A removed file is marked, never deleted. After a scan, rebuild <code>series</code> then <code>resolve</code> (Overview).</>}
      start={() => scanStart(rootId || undefined)}
      startLabel="Apply scan"
      controls={(
        <>
          <Select<number> value={rootId} onChange={setRootId} style={{ width: 260 }} options={[{ value: 0, label: "Every root" }, ...(roots.data ?? []).map((r) => ({ value: r.id, label: r.path }))]} />
          <Button onClick={() => doPreview.mutate()} loading={doPreview.isPending}>Preview</Button>
        </>
      )}
    >
      {preview && (
        <Alert type="info" showIcon className="adm-preview" title={`Preview: would add ${preview.wouldAdd.toLocaleString()}, change ${preview.wouldChange.toLocaleString()}, remove ${preview.wouldRemove.toLocaleString()} — over ${preview.folders.toLocaleString()} folders / ${preview.files.toLocaleString()} files.`} />
      )}
      {p && p.phase !== "done" && (
        <div className="adm-job-nums">phase <b>{p.phase}</b> · +{p.added} / ~{p.changed} / −{p.removed}{p.failed > 0 && <span className="adm-warn"> · {p.failed} failed</span>}</div>
      )}
    </JobCard>
  );
}

function ThumbsCard() {
  const [reset, setReset] = useState(false);
  const status = useQuery({ queryKey: bk.admin("thumbs-status"), queryFn: ({ signal }) => thumbsStatus(signal), refetchInterval: 3000 });
  const s = status.data;
  return (
    <JobCard
      kind="thumbnails"
      title="Thumbnails"
      description="Generates the missing cover thumbnails. Reset starts the walk from the top (already-generated covers are skipped, not redrawn)."
      start={() => thumbsStart(reset)}
      controls={<label className="adm-inline"><Switch size="small" checked={reset} onChange={setReset} /> Reset cursor</label>}
    >
      {s && <div className="adm-job-nums">generated {s.generated.toLocaleString()} · skipped {s.skipped.toLocaleString()} · failed {s.failed.toLocaleString()} · remaining {s.remaining.toLocaleString()}</div>}
    </JobCard>
  );
}

function CalibreCard() {
  const [metadata, setMetadata] = useState("");
  const [link, setLink] = useState("");
  const [apply, setApply] = useState(false);
  return (
    <JobCard
      kind="calibre-import"
      title="Calibre import"
      description={<>Fills the books' Calibre-native identity (series, ISBN, tags) from a <code>metadata.db</code>. Without Apply it is a dry run. Leave the path empty to use the root marked Calibre.</>}
      start={() => calibreImport({ metadata: metadata.trim() || undefined, link: link.trim() || undefined, apply })}
      startLabel={apply ? "Import" : "Dry run"}
      controls={(
        <>
          <Input placeholder="metadata.db path (optional)" value={metadata} onChange={(e) => setMetadata(e.target.value)} style={{ width: 260 }} />
          <Input placeholder="link file (optional)" value={link} onChange={(e) => setLink(e.target.value)} style={{ width: 200 }} />
          <label className="adm-inline"><Switch size="small" checked={apply} onChange={setApply} /> Apply</label>
        </>
      )}
    />
  );
}

function BrokenCard() {
  const [page, setPage] = useState(1);
  const top = 50;
  const broken = useQuery({ queryKey: bk.admin("broken", page), queryFn: ({ signal }) => fetchBroken((page - 1) * top, top, signal) });
  return (
    <section className="adm-card">
      <header className="adm-card-head"><div className="adm-card-text"><h3 className="adm-card-title">Broken files</h3><p className="adm-card-desc">What a scan or the thumbnail pass could not read. A file marked missing returns whole when a later scan finds it.</p></div></header>
      <Table<BrokenRow>
        size="small" rowKey="id" loading={broken.isLoading} dataSource={broken.data?.items ?? []}
        pagination={{ current: page, pageSize: top, total: broken.data?.totalCount ?? 0, onChange: setPage, showSizeChanger: false }}
        columns={[
          { title: "Id", dataIndex: "id", width: 80 },
          { title: "File", dataIndex: "fileName" },
          { title: "Path", dataIndex: "path", ellipsis: true, render: (v: string) => <code>{v}</code> },
          { title: "Why", key: "why", render: (_v, r) => <span>{r.isBroken && <Tag color="red">{r.brokenReason ?? "broken"}</Tag>}{r.thumbnailError && <Tag color="orange">thumb: {r.thumbnailError}</Tag>}</span> },
        ]}
      />
    </section>
  );
}

export default function LibraryTab() {
  return (
    <div className="adm-tab">
      <RootsCard />
      <ScanCard />
      <ThumbsCard />
      <CalibreCard />
      <BrokenCard />
    </div>
  );
}
