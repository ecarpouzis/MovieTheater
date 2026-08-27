/**
 * Normalization — the `TagAlias` map (variant → canonical, per category) and the four tag-hygiene
 * passes (dry run first; after Apply, rebuild `resolve` so the folds pick it up).
 */
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Button, Input, Popconfirm, Select, Space, Table, Tag, message } from "antd";
import { useState } from "react";
import { bk } from "../../booksQuery";
import { deleteAlias, fetchAliases, normalizeTags, putAlias, type NormalizeResult, type TagAlias } from "../adminApi";

const CATEGORIES = ["audience", "genre", "theme", "tone", "setting", "era", "character-focus", "award", "publisher-context"];

export default function NormalizationTab() {
  const qc = useQueryClient();
  const aliases = useQuery({ queryKey: bk.admin("aliases"), queryFn: ({ signal }) => fetchAliases(signal) });
  const [draft, setDraft] = useState({ category: "genre", aliasTag: "", canonicalTag: "" });
  const invalidate = () => qc.invalidateQueries({ queryKey: bk.admin("aliases") });
  const add = useMutation({ mutationFn: () => putAlias(draft), onSuccess: () => { setDraft({ ...draft, aliasTag: "", canonicalTag: "" }); void invalidate(); }, onError: (e) => message.error(e instanceof Error ? e.message : "Add refused.") });
  const remove = useMutation({ mutationFn: (a: TagAlias) => deleteAlias(a.category, a.aliasTag), onSuccess: () => void invalidate(), onError: (e) => message.error(e instanceof Error ? e.message : "Delete failed.") });
  const [result, setResult] = useState<NormalizeResult | null>(null);
  const run = useMutation({ mutationFn: (apply: boolean) => normalizeTags(apply), onSuccess: (r) => { setResult(r); if (!r.dryRun) message.success("Tags normalized — rebuild resolve on Overview."); }, onError: (e) => message.error(e instanceof Error ? e.message : "Normalization failed.") });

  return (
    <div className="adm-tab">
      <section className="adm-card">
        <header className="adm-card-head"><div className="adm-card-text"><h3 className="adm-card-title">Tag aliases</h3><p className="adm-card-desc">Variant → canonical mappings the tag folds consume. Adding or removing one changes the folds' input fingerprint; the Tags table then shows stale on Overview.</p></div></header>
        <div className="adm-form-row">
          <Select value={draft.category} onChange={(category) => setDraft({ ...draft, category })} options={CATEGORIES.map((c) => ({ value: c, label: c }))} style={{ width: 170 }} />
          <Input placeholder="alias (variant)" value={draft.aliasTag} onChange={(e) => setDraft({ ...draft, aliasTag: e.target.value })} style={{ maxWidth: 220 }} />
          <span>→</span>
          <Input placeholder="canonical" value={draft.canonicalTag} onChange={(e) => setDraft({ ...draft, canonicalTag: e.target.value })} style={{ maxWidth: 220 }} />
          <Button type="primary" onClick={() => add.mutate()} disabled={!draft.aliasTag.trim() || !draft.canonicalTag.trim() || add.isPending}>Add alias</Button>
        </div>
        <Table<TagAlias>
          size="small" rowKey={(a) => `${a.category}|${a.aliasTag}`} loading={aliases.isLoading} dataSource={aliases.data ?? []} pagination={{ pageSize: 50, showSizeChanger: false }}
          columns={[
            { title: "Category", dataIndex: "category", render: (v: string) => <Tag>{v}</Tag> },
            { title: "Alias", dataIndex: "aliasTag", render: (v: string) => <code>{v}</code> },
            { title: "Canonical", dataIndex: "canonicalTag", render: (v: string | null) => <code>{v ?? "—"}</code> },
            { title: "Source", dataIndex: "source" },
            { title: "", key: "act", align: "right", render: (_v, a) => <Button size="small" type="link" danger onClick={() => remove.mutate(a)}>Remove</Button> },
          ]}
        />
      </section>
      <section className="adm-card">
        <header className="adm-card-head">
          <div className="adm-card-text"><h3 className="adm-card-title">Tag hygiene</h3><p className="adm-card-desc">Applies the aliases, drops era ranges and cross-category strays, migrates the old tone:mature. Dry run first.</p></div>
          <Space>
            <Button onClick={() => run.mutate(false)} loading={run.isPending}>Dry run</Button>
            <Popconfirm title="Rewrite the input tags?" onConfirm={() => run.mutate(true)}><Button type="primary" danger>Apply</Button></Popconfirm>
          </Space>
        </header>
        {result && (
          <Alert type={result.dryRun ? "info" : "success"} showIcon title={`${result.dryRun ? "Would apply" : "Applied"}: ${result.result.aliasesApplied} aliases, ${result.result.eraRangesRemoved} era ranges removed, ${result.result.crossCategoryRemoved} cross-category removed, ${result.result.toneMatureMigrated} tone:mature migrated.`} description={result.dryRun ? undefined : `Next: ${result.next}`} />
        )}
      </section>
    </div>
  );
}
