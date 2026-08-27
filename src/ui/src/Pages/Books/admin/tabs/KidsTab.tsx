/**
 * Kids — the `KidSafeTag` allow-list: which tags clear a series (comics) or a book for the Kids
 * view, on top of the ceiling-0 floor. The rows are the host's; the tag options come from the
 * browse facets so a typo cannot clear a tag that exists nowhere.
 */
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AutoComplete, Button, Input, Segmented, Table, Tag, message } from "antd";
import { useState } from "react";
import { fetchFacets } from "../../booksApi";
import { bk } from "../../booksQuery";
import { deleteKidTag, fetchKidTags, putKidTag, type KidTag } from "../adminApi";

const SCOPES = [{ value: "comic", label: "Comics" }, { value: "book", label: "Books" }, { value: "both", label: "Both" }];

export default function KidsTab() {
  const qc = useQueryClient();
  const tags = useQuery({ queryKey: bk.admin("kids-tags"), queryFn: ({ signal }) => fetchKidTags(signal) });
  const facets = useQuery({ queryKey: bk.facets("comic"), queryFn: ({ signal }) => fetchFacets("comic", signal), staleTime: 5 * 60 * 1000 });
  const [draft, setDraft] = useState({ category: "audience", tag: "", appliesTo: "both" });
  const invalidate = () => { void qc.invalidateQueries({ queryKey: bk.admin("kids-tags") }); void qc.invalidateQueries({ queryKey: ["books", "explore-kids"] }); void qc.invalidateQueries({ queryKey: ["books", "kids-browse"] }); };
  const upsert = useMutation({ mutationFn: (b: { category: string; tag: string; appliesTo: string }) => putKidTag(b), onSuccess: () => { invalidate(); setDraft({ ...draft, tag: "" }); }, onError: (e) => message.error(e instanceof Error ? e.message : "Refused.") });
  const remove = useMutation({ mutationFn: (t: KidTag) => deleteKidTag(t.category, t.tag), onSuccess: () => invalidate(), onError: (e) => message.error(e instanceof Error ? e.message : "Delete failed.") });
  const options = (facets.data?.tags ?? []).map((t) => ({ value: t.value.includes(":") ? t.value.slice(t.value.indexOf(":") + 1) : t.value }));

  return (
    <div className="adm-tab">
      <section className="adm-card">
        <header className="adm-card-head"><div className="adm-card-text"><h3 className="adm-card-title">Kid-clear tags</h3><p className="adm-card-desc">A series appears in Kids only when it carries one of these tags (and nothing above the ceiling). Comics carry <code>audience:all-ages</code>; books carry <code>audience:children</code>. Changes reach the Kids view on its next open.</p></div></header>
        <div className="adm-form-row">
          <Input value={draft.category} onChange={(e) => setDraft({ ...draft, category: e.target.value })} placeholder="category" style={{ width: 140 }} />
          <AutoComplete value={draft.tag} onChange={(v) => setDraft({ ...draft, tag: String(v) })} options={options} placeholder="tag" style={{ width: 220 }} filterOption={(input, o) => String(o?.value ?? "").toLowerCase().includes(input.toLowerCase())} />
          <Segmented options={SCOPES} value={draft.appliesTo} onChange={(v) => setDraft({ ...draft, appliesTo: String(v) })} />
          <Button type="primary" disabled={!draft.category.trim() || !draft.tag.trim() || upsert.isPending} onClick={() => upsert.mutate(draft)}>Clear for kids</Button>
        </div>
        <Table<KidTag>
          size="small" rowKey={(t) => `${t.category}|${t.tag}`} loading={tags.isLoading} dataSource={tags.data ?? []} pagination={false}
          columns={[
            { title: "Category", dataIndex: "category", render: (v: string) => <Tag>{v}</Tag> },
            { title: "Tag", dataIndex: "tag", render: (v: string) => <code>{v}</code> },
            { title: "Applies to", dataIndex: "appliesTo", render: (v: string | null, t) => <Segmented size="small" options={SCOPES} value={v ?? "both"} onChange={(next) => upsert.mutate({ category: t.category, tag: t.tag, appliesTo: String(next) })} /> },
            { title: "Updated", dataIndex: "updatedAt", render: (v: string | null) => (v ? new Date(v).toLocaleString() : "—") },
            { title: "", key: "act", align: "right", render: (_v, t) => <Button size="small" type="link" danger onClick={() => remove.mutate(t)}>Remove</Button> },
          ]}
        />
      </section>
    </div>
  );
}
