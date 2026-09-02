/**
 * Series — reconciliation. Every write here edits an INPUT (a key link, a parsed key, a display
 * name) and answers `rebuildRequired`; the Rebuild hint points at Overview → series. Three sub-views:
 *   Mismatches — the counters, a parsed key's stored link + candidates (clear / set), fold a spelling,
 *                unify a folder, and the over-matched volumes report;
 *   Review     — the decision log with revert, paged by the client-driven loop;
 *   Names      — display-name overrides, the name-fix dry run → apply, and prune.
 */
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Button, Input, InputNumber, Popconfirm, Segmented, Select, Space, Statistic, Table, Tag, message } from "antd";
import { useState } from "react";
import { bk } from "../../booksQuery";
import {
  clearLink, fetchDecisions, fetchLinkCandidates, fetchMismatchSummary, fetchOvermatch, fetchSeriesAliases, foldKey, nameFix, prune, revertDecision, setFranchise, setLink, setOverride, unifyFolder,
  type Decision, type EditResult, type NameFix, type Overmatch, type SeriesAliasRow,
} from "../adminApi";
import { driveBatches } from "../driveBatches";

function useEditToast() {
  const qc = useQueryClient();
  return {
    ok: (r: EditResult) => {
      message.success(`${r.action}: ${r.rowsChanged} row(s) changed${r.rebuildRequired ? " — rebuild series on Overview to see it" : ""}.`);
      void qc.invalidateQueries({ queryKey: bk.admin("series") });
    },
    err: (e: unknown) => message.error(e instanceof Error ? e.message : "The edit was refused."),
  };
}

function Mismatches() {
  const toast = useEditToast();
  const summary = useQuery({ queryKey: bk.admin("series", "summary"), queryFn: ({ signal }) => fetchMismatchSummary(signal) });
  const [key, setKey] = useState("");
  const [asked, setAsked] = useState("");
  const link = useQuery({ queryKey: bk.admin("series", "link", asked), queryFn: ({ signal }) => fetchLinkCandidates(asked, "Cv", signal), enabled: asked.length > 0, retry: false });
  const [providerKey, setProviderKey] = useState<number | null>(null);
  const doClear = useMutation({ mutationFn: () => clearLink(asked), onSuccess: toast.ok, onError: toast.err });
  const doSet = useMutation({ mutationFn: () => setLink(asked, providerKey!), onSuccess: toast.ok, onError: toast.err });
  const [fold, setFold] = useState({ from: "", to: "" });
  const doFold = useMutation({ mutationFn: () => foldKey(fold.from.trim(), fold.to.trim()), onSuccess: toast.ok, onError: toast.err });
  const [unify, setUnify] = useState<{ folderId: number | null; key: string }>({ folderId: null, key: "" });
  const doUnify = useMutation({ mutationFn: () => unifyFolder(unify.folderId!, unify.key.trim()), onSuccess: toast.ok, onError: toast.err });
  const [seriesId, setSeriesId] = useState<number | null>(null);
  const aliases = useQuery({ queryKey: bk.admin("series", "aliases", seriesId ?? 0), queryFn: ({ signal }) => fetchSeriesAliases(seriesId!, signal), enabled: seriesId != null });
  const over = useQuery({ queryKey: bk.admin("series", "overmatch"), queryFn: ({ signal }) => fetchOvermatch(2, 20, signal) });
  const s = summary.data;

  return (
    <div className="adm-tab">
      <div className="adm-stats">
        <Statistic title="Series" value={s?.series ?? 0} />
        <Statistic title="Linked" value={s?.linkedSeries ?? 0} />
        <Statistic title="Unlinked" value={s?.unlinkedSeries ?? 0} />
        <Statistic title="Pending links" value={s?.pendingLinks ?? 0} />
        <Statistic title="Multiple" value={s?.multipleLinks ?? 0} />
        <Statistic title="Open reviews" value={s?.openReviews ?? 0} />
        <Statistic title="Single-issue" value={s?.singleIssueSeries ?? 0} />
      </div>

      <section className="adm-card">
        <header className="adm-card-head"><div className="adm-card-text"><h3 className="adm-card-title">A parsed key's provider link</h3><p className="adm-card-desc">What the scraper decided for one spelling. A cleared link stays <i>Cleared</i> so the next scrape cannot re-make the same wrong match.</p></div></header>
        <div className="adm-form-row">
          <Input placeholder="parsed series key" value={key} onChange={(e) => setKey(e.target.value)} onPressEnter={() => setAsked(key.trim())} style={{ maxWidth: 360 }} />
          <Button onClick={() => setAsked(key.trim())} disabled={!key.trim()}>Look up</Button>
        </div>
        {link.isError && asked && <Alert type="info" showIcon title={`No stored link for "${asked}".`} />}
        {link.data && (
          <div className="adm-facts">
            <span>status <Tag>{link.data.status}</Tag></span>
            <span>provider key <code>{link.data.providerKey ?? "—"}</code></span>
            <span>score {link.data.score ?? "—"} (stored top {link.data.storedTopScore ?? "—"})</span>
            <span>attempts {link.data.attemptCount}</span>
            {link.data.error && <span className="adm-warn">{link.data.error}</span>}
            {link.data.candidates?.length ? (
              <ul className="adm-candidates">
                {link.data.candidates.map((c) => (
                  <li key={c.id}>
                    <Button size="small" type="link" onClick={() => setProviderKey(c.id)}>#{c.id}</Button>
                    {c.name ?? "—"}{c.publisher ? ` · ${c.publisher}` : ""}{c.startYear ? ` · ${c.startYear}` : ""}{c.issues != null ? ` · ${c.issues} issues` : ""}
                    {c.score != null && <Tag>{c.score}</Tag>}
                  </li>
                ))}
              </ul>
            ) : link.data.candidatesInLegs ? <span>candidates are in the legs file</span> : null}
            <Space wrap>
              <Popconfirm title="Clear this link?" onConfirm={() => doClear.mutate()}><Button danger size="small">Clear link</Button></Popconfirm>
              <InputNumber placeholder="CV volume id" value={providerKey} onChange={(v) => setProviderKey(v == null ? null : Number(v))} style={{ width: 150 }} />
              <Button size="small" type="primary" disabled={providerKey == null} onClick={() => doSet.mutate()}>Set link</Button>
            </Space>
          </div>
        )}
      </section>

      <section className="adm-card">
        <header className="adm-card-head"><div className="adm-card-text"><h3 className="adm-card-title">Fold a spelling</h3><p className="adm-card-desc">Re-points every comic parsed as <i>from</i> onto <i>to</i>. The emptied spelling keeps its own series row until pruned (Names).</p></div></header>
        <div className="adm-form-row">
          <Input placeholder="from key" value={fold.from} onChange={(e) => setFold({ ...fold, from: e.target.value })} style={{ maxWidth: 280 }} />
          <span>→</span>
          <Input placeholder="to key" value={fold.to} onChange={(e) => setFold({ ...fold, to: e.target.value })} style={{ maxWidth: 280 }} />
          <Popconfirm title={`Fold "${fold.from}" into "${fold.to}"?`} onConfirm={() => doFold.mutate()} disabled={!fold.from.trim() || !fold.to.trim()}><Button type="primary" disabled={!fold.from.trim() || !fold.to.trim()}>Fold</Button></Popconfirm>
        </div>
      </section>

      <section className="adm-card">
        <header className="adm-card-head"><div className="adm-card-text"><h3 className="adm-card-title">Unify a folder</h3><p className="adm-card-desc">Gives every comic in one folder the same parsed key — the fix for a shattered folder.</p></div></header>
        <div className="adm-form-row">
          <InputNumber placeholder="folder id" value={unify.folderId} onChange={(v) => setUnify({ ...unify, folderId: v == null ? null : Number(v) })} style={{ width: 140 }} />
          <Input placeholder="parsed key" value={unify.key} onChange={(e) => setUnify({ ...unify, key: e.target.value })} style={{ maxWidth: 280 }} />
          <Popconfirm title="Unify this folder?" onConfirm={() => doUnify.mutate()} disabled={unify.folderId == null || !unify.key.trim()}><Button type="primary" disabled={unify.folderId == null || !unify.key.trim()}>Unify</Button></Popconfirm>
        </div>
      </section>

      <section className="adm-card">
        <header className="adm-card-head"><div className="adm-card-text"><h3 className="adm-card-title">A series' spellings</h3><p className="adm-card-desc">The parsed keys that resolve into one series, with how many items each carries.</p></div></header>
        <div className="adm-form-row">
          <InputNumber placeholder="series id" value={seriesId} onChange={(v) => setSeriesId(v == null ? null : Number(v))} style={{ width: 140 }} />
        </div>
        {aliases.data && <Table<SeriesAliasRow> size="small" rowKey="parsedKey" pagination={false} dataSource={aliases.data} columns={[{ title: "Parsed key", dataIndex: "parsedKey", render: (v: string) => <code>{v}</code> }, { title: "Items", dataIndex: "items", align: "right" }]} />}
      </section>

      <section className="adm-card">
        <header className="adm-card-head"><div className="adm-card-text"><h3 className="adm-card-title">Over-matched volumes</h3><p className="adm-card-desc">Series holding more than twice the issues their ComicVine volume claims — a volume that swallowed a sibling series. Clearing the link lets the next scrape pick again.</p></div></header>
        <Table<Overmatch>
          size="small" rowKey="seriesId" loading={over.isLoading} dataSource={over.data ?? []} pagination={{ pageSize: 25, showSizeChanger: false }}
          columns={[
            { title: "Series", dataIndex: "name", render: (v: string | null, r) => <span>{v ?? "—"} <code>#{r.seriesId}</code></span> },
            { title: "Held", dataIndex: "held", align: "right" },
            { title: "Claimed", dataIndex: "claimed", align: "right" },
            { title: "CV volume", dataIndex: "cvVolumeId", render: (v: number) => <code>{v}</code> },
            { title: "", key: "act", align: "right", render: (_v, r) => <Button size="small" onClick={() => setSeriesId(r.seriesId)}>Spellings</Button> },
          ]}
        />
      </section>
    </div>
  );
}

const DECISION_STATES = ["All", "Queued", "AutoApplied", "Confirmed", "Reverted"];

function Review() {
  const toast = useEditToast();
  const [state, setState] = useState("AutoApplied");
  const [rows, setRows] = useState<Decision[]>([]);
  const [loading, setLoading] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const load = async (s: string) => {
    setLoading(true); setNote(null);
    try {
      const all = await driveBatches<Decision>(async (cursor, signal) => {
        const page = await fetchDecisions(s === "All" ? undefined : s, cursor, 100, signal);
        return { items: page, nextCursor: page.length === 100 ? cursor + 100 : null };
      }, { maxSteps: 50 });
      setRows(all);
      if (all.length >= 5000) setNote("Showing the first 5,000 decisions.");
    } catch (e) { message.error(e instanceof Error ? e.message : "Could not load the decisions."); }
    finally { setLoading(false); }
  };
  const revert = useMutation({ mutationFn: (id: number) => revertDecision(id), onSuccess: (r) => { toast.ok(r); void load(state); }, onError: toast.err });
  return (
    <div className="adm-tab">
      <section className="adm-card">
        <header className="adm-card-head">
          <div className="adm-card-text"><h3 className="adm-card-title">Decision log</h3><p className="adm-card-desc">Every reconciliation edit, with its undo payload. Fold and unify revert from here; a cleared link is reversed by setting it again.</p></div>
          <Space wrap>
            <Segmented options={DECISION_STATES} value={state} onChange={(v) => { setState(String(v)); void load(String(v)); }} />
            <Button onClick={() => load(state)} loading={loading}>Load</Button>
          </Space>
        </header>
        {note && <Alert type="info" showIcon title={note} />}
        <Table<Decision>
          size="small" rowKey="id" loading={loading} dataSource={rows} pagination={{ pageSize: 50, showSizeChanger: false }}
          columns={[
            { title: "Id", dataIndex: "id", width: 70 },
            { title: "Class", dataIndex: "class", render: (v: string | null) => v && <Tag>{v}</Tag> },
            { title: "Key", dataIndex: "seriesKey", render: (v: string | null) => <code>{v}</code> },
            { title: "Action", dataIndex: "action" },
            { title: "Target", dataIndex: "target", render: (v: string | null) => v && <code>{v}</code> },
            { title: "Confidence", dataIndex: "confidence", render: (v: string | null) => v && <Tag color={v === "High" ? "green" : v === "Low" ? "red" : "orange"}>{v}</Tag> },
            { title: "State", dataIndex: "state" },
            { title: "By", dataIndex: "decidedBy" },
            { title: "When", dataIndex: "decidedAt", render: (v: string | null) => (v ? new Date(v).toLocaleString() : "—") },
            { title: "", key: "act", align: "right", render: (_v, r) => r.state !== "Reverted" && r.undoJson ? <Popconfirm title="Revert this decision?" onConfirm={() => revert.mutate(r.id)}><Button size="small" danger>Revert</Button></Popconfirm> : null },
          ]}
        />
      </section>
    </div>
  );
}

function Names() {
  const toast = useEditToast();
  const [seriesId, setSeriesId] = useState<number | null>(null);
  const [name, setName] = useState("");
  const doOverride = useMutation({ mutationFn: (clear: boolean) => setOverride(seriesId!, clear ? null : name.trim()), onSuccess: toast.ok, onError: toast.err });
  const [franchiseId, setFranchiseId] = useState<number | null>(null);
  const [franchise, setFranchiseName] = useState("");
  const doFranchise = useMutation({ mutationFn: (clear: boolean) => setFranchise(franchiseId!, clear ? null : franchise.trim()), onSuccess: toast.ok, onError: toast.err });
  const fixes = useQuery({ queryKey: bk.admin("series", "namefix"), queryFn: ({ signal }) => nameFix(false, signal) });
  const applyFixes = useMutation({ mutationFn: () => nameFix(true), onSuccess: (r) => { message.success(`Applied ${r.fixes.length} name fixes.`); void fixes.refetch(); }, onError: toast.err });
  const pruneDry = useQuery({ queryKey: bk.admin("series", "prune"), queryFn: () => prune(false) });
  const applyPrune = useMutation({ mutationFn: () => prune(true), onSuccess: (r) => { message.success(`Pruned ${r.deleted} empty series.`); void pruneDry.refetch(); }, onError: toast.err });
  return (
    <div className="adm-tab">
      <section className="adm-card">
        <header className="adm-card-head"><div className="adm-card-text"><h3 className="adm-card-title">Display name override</h3><p className="adm-card-desc">Pins the name a series shows. An override always wins over the resolved name; clearing it restores the pipeline's pick.</p></div></header>
        <div className="adm-form-row">
          <InputNumber placeholder="series id" value={seriesId} onChange={(v) => setSeriesId(v == null ? null : Number(v))} style={{ width: 140 }} />
          <Input placeholder="display name" value={name} onChange={(e) => setName(e.target.value)} style={{ maxWidth: 360 }} />
          <Button type="primary" disabled={seriesId == null || !name.trim()} onClick={() => doOverride.mutate(false)}>Set</Button>
          <Button disabled={seriesId == null} onClick={() => doOverride.mutate(true)}>Clear</Button>
        </div>
      </section>
      <section className="adm-card">
        <header className="adm-card-head"><div className="adm-card-text"><h3 className="adm-card-title">Franchise</h3><p className="adm-card-desc">The curated Franchise facet value for a series (Batman, X-Men, Star Wars…). Nothing derives it: this and <code>books-curation-import</code> are its only producers.</p></div></header>
        <div className="adm-form-row">
          <InputNumber placeholder="series id" value={franchiseId} onChange={(v) => setFranchiseId(v == null ? null : Number(v))} style={{ width: 140 }} />
          <Input placeholder="franchise" value={franchise} onChange={(e) => setFranchiseName(e.target.value)} style={{ maxWidth: 360 }} />
          <Button type="primary" disabled={franchiseId == null || !franchise.trim()} onClick={() => doFranchise.mutate(false)}>Set</Button>
          <Button disabled={franchiseId == null} onClick={() => doFranchise.mutate(true)}>Clear</Button>
        </div>
      </section>
      <section className="adm-card">
        <header className="adm-card-head">
          <div className="adm-card-text"><h3 className="adm-card-title">Name fix</h3><p className="adm-card-desc">Series whose resolved name still carries a scene-release artifact; the proposal is the cleaned title. Dry run below — Apply pins every proposal as an override.</p></div>
          <Popconfirm title={`Apply ${fixes.data?.fixes.length ?? 0} name fixes?`} onConfirm={() => applyFixes.mutate()} disabled={!fixes.data?.fixes.length}><Button type="primary" disabled={!fixes.data?.fixes.length} loading={applyFixes.isPending}>Apply all</Button></Popconfirm>
        </header>
        <Table<NameFix> size="small" rowKey="seriesId" loading={fixes.isLoading} dataSource={fixes.data?.fixes ?? []} pagination={{ pageSize: 25, showSizeChanger: false }}
          columns={[{ title: "Series", dataIndex: "seriesId", width: 90, render: (v: number) => <code>#{v}</code> }, { title: "Current", dataIndex: "current" }, { title: "Proposed", dataIndex: "proposed" }, { title: "Issues", dataIndex: "issueCount", align: "right" }]} />
      </section>
      <section className="adm-card">
        <header className="adm-card-head">
          <div className="adm-card-text"><h3 className="adm-card-title">Prune empty series</h3><p className="adm-card-desc">Series with no items and no marks — the residue of past re-points. A series a reader has marked is never pruned.</p></div>
          <Space>
            <span className="adm-muted">{pruneDry.data ? `${pruneDry.data.candidates.toLocaleString()} candidates` : "…"}</span>
            <Popconfirm title={`Delete ${pruneDry.data?.candidates ?? 0} empty series?`} onConfirm={() => applyPrune.mutate()} disabled={!pruneDry.data?.candidates}><Button danger disabled={!pruneDry.data?.candidates} loading={applyPrune.isPending}>Prune</Button></Popconfirm>
          </Space>
        </header>
      </section>
    </div>
  );
}

const VIEWS = [{ value: "mismatches", label: "Mismatches" }, { value: "review", label: "Review" }, { value: "names", label: "Names" }];

export default function SeriesTab() {
  const [view, setView] = useState("mismatches");
  return (
    <div className="adm-tab">
      <Select value={view} onChange={setView} options={VIEWS} style={{ width: 200 }} className="adm-subtabs" />
      {view === "mismatches" && <Mismatches />}
      {view === "review" && <Review />}
      {view === "names" && <Names />}
    </div>
  );
}
