/**
 * System — the host's own log tail (polled every 3 s, level filter, clear) and the thumbnail cache
 * clear (dry run shows the count; Apply deletes only `^\d+\.webp$` — a collection icon survives).
 */
import { useMutation, useQuery } from "@tanstack/react-query";
import { Alert, Button, Input, Popconfirm, Select, Space, Table, Tag, message } from "antd";
import { useState } from "react";
import { bk } from "../../booksQuery";
import { cacheClear, clearLogs, fetchLogs, type LogEntry } from "../adminApi";

const LEVEL_COLOR: Record<string, string> = { error: "red", critical: "red", warning: "orange", information: "green", info: "green", debug: "default", trace: "default" };

export default function SystemTab() {
  const [level, setLevel] = useState<string | undefined>(undefined);
  const [q, setQ] = useState("");
  const logs = useQuery({ queryKey: bk.admin("logs", level ?? "all"), queryFn: ({ signal }) => fetchLogs({ count: 300, level }, signal), refetchInterval: 3000 });
  const clear = useMutation({ mutationFn: () => clearLogs(), onSuccess: () => void logs.refetch() });
  const [cache, setCache] = useState<{ dryRun?: boolean; wouldDelete?: number; deleted?: number; kept: number } | null>(null);
  const cacheM = useMutation({ mutationFn: (apply: boolean) => cacheClear(apply), onSuccess: (r) => { setCache(r); if (!r.dryRun) message.success(`Deleted ${r.deleted} thumbnails.`); }, onError: (e) => message.error(e instanceof Error ? e.message : "Cache clear failed.") });
  const entries = (logs.data?.entries ?? []).filter((e) => !q || e.message.toLowerCase().includes(q.toLowerCase()) || e.category.toLowerCase().includes(q.toLowerCase()));
  return (
    <div className="bka-tab">
      <section className="bka-card">
        <header className="bka-card-head">
          <div className="bka-card-text"><h3 className="bka-card-title">Thumbnail cache</h3><p className="bka-card-desc">Deletes generated covers only (<code>{"^\\d+\\.webp$"}</code>); the thumbnail pass regenerates them. Hand-made collection icons are never touched.</p></div>
          <Space>
            <Button onClick={() => cacheM.mutate(false)} loading={cacheM.isPending}>Count</Button>
            <Popconfirm title={`Delete ${cache?.wouldDelete ?? "the generated"} thumbnails?`} onConfirm={() => cacheM.mutate(true)}><Button danger>Clear cache</Button></Popconfirm>
          </Space>
        </header>
        {cache && <Alert type={cache.dryRun ? "info" : "success"} showIcon title={cache.dryRun ? `Would delete ${cache.wouldDelete?.toLocaleString()} generated thumbnails; ${cache.kept.toLocaleString()} other files kept.` : `Deleted ${cache.deleted?.toLocaleString()}; ${cache.kept.toLocaleString()} kept.`} />}
      </section>
      <section className="bka-card">
        <header className="bka-card-head">
          <div className="bka-card-text"><h3 className="bka-card-title">Host log</h3><p className="bka-card-desc">The last {logs.data?.capacity?.toLocaleString() ?? "…"} lines the host wrote, newest first.</p></div>
          <Space wrap>
            <Input.Search placeholder="filter" value={q} onChange={(e) => setQ(e.target.value)} allowClear style={{ width: 220 }} />
            <Select allowClear placeholder="Level" value={level} onChange={setLevel} options={["Information", "Warning", "Error", "Critical", "Debug"].map((l) => ({ value: l, label: l }))} style={{ width: 140 }} />
            <Button onClick={() => logs.refetch()}>Refresh</Button>
            <Popconfirm title="Clear the host log buffer?" onConfirm={() => clear.mutate()}><Button danger>Clear</Button></Popconfirm>
          </Space>
        </header>
        <Table<LogEntry>
          size="small" rowKey="seq" loading={logs.isLoading} dataSource={entries} pagination={{ pageSize: 100, showSizeChanger: false }}
          columns={[
            { title: "At", dataIndex: "at", width: 170, render: (v: string) => new Date(v).toLocaleTimeString() },
            { title: "Level", dataIndex: "level", width: 110, render: (v: string) => <Tag color={LEVEL_COLOR[v?.toLowerCase()] ?? "default"}>{v}</Tag> },
            { title: "Category", dataIndex: "category", width: 220, ellipsis: true },
            { title: "Message", dataIndex: "message", render: (v: string, r) => <span>{v}{r.exception && <pre className="bka-exc">{r.exception}</pre>}</span> },
          ]}
        />
      </section>
    </div>
  );
}
