/**
 * Config — the settings overlay: an ALLOW-LIST of four keys, never a config editor. A secret reads
 * back as "(set)"; an out-of-range number or an unknown key is refused by the host with a sentence.
 */
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Button, Input, InputNumber, Space, Tag, message } from "antd";
import { useEffect, useState } from "react";
import { bk } from "../../booksQuery";
import { fetchConfig, putConfig } from "../adminApi";

export default function ConfigTab() {
  const qc = useQueryClient();
  const config = useQuery({ queryKey: bk.admin("config"), queryFn: ({ signal }) => fetchConfig(signal) });
  const [draft, setDraft] = useState<Record<string, unknown>>({});
  useEffect(() => { setDraft({}); }, [config.data]);
  const save = useMutation({
    mutationFn: () => putConfig(draft),
    onSuccess: () => { message.success("Settings written."); setDraft({}); void qc.invalidateQueries({ queryKey: bk.admin("config") }); void qc.invalidateQueries({ queryKey: bk.admin("info") }); },
    onError: (e) => message.error(e instanceof Error ? e.message : "The host refused the settings."),
  });
  const c = config.data;
  const dirty = Object.keys(draft).length > 0;
  return (
    <div className="bka-tab">
      <section className="bka-card">
        <header className="bka-card-head">
          <div className="bka-card-text">
            <h3 className="bka-card-title">Settings overlay</h3>
            <p className="bka-card-desc">Written atomically to <code>{c?.path ?? "…"}</code>. Paths and the other secrets are deliberately not settable here. {c && !c.writable && <Tag color="red">not writable on this host</Tag>}</p>
          </div>
          <Space>
            <Button onClick={() => setDraft({})} disabled={!dirty}>Discard</Button>
            <Button type="primary" onClick={() => save.mutate()} disabled={!dirty || !c?.writable} loading={save.isPending}>Save</Button>
          </Space>
        </header>
        {config.isError && <Alert type="error" showIcon title="The settings could not be read." />}
        <div className="bka-config">
          {(c?.keys ?? []).map((k) => {
            const current = c?.values[k.name];
            const value = k.name in draft ? draft[k.name] : current;
            return (
              <div key={k.name} className="bka-config-row">
                <div className="bka-config-key"><code>{k.name}</code><div className="bka-muted">{k.description}</div></div>
                <div className="bka-config-val">
                  {k.kind === "Secret" ? (
                    <Space>
                      <Input.Password placeholder={current === "(set)" ? "(set) — type to replace" : "not set"} value={typeof value === "string" && value !== "(set)" ? value : ""} onChange={(e) => setDraft({ ...draft, [k.name]: e.target.value })} style={{ width: 300 }} />
                      {current === "(set)" && <Button size="small" onClick={() => setDraft({ ...draft, [k.name]: null })}>Clear</Button>}
                      {current === "(set)" && !(k.name in draft) && <Tag color="green">set</Tag>}
                    </Space>
                  ) : (
                    <Space>
                      <InputNumber min={k.min ?? undefined} max={k.max ?? undefined} value={typeof value === "number" ? value : value == null ? null : Number(value)} onChange={(v) => setDraft({ ...draft, [k.name]: v })} style={{ width: 160 }} />
                      <span className="bka-muted">{k.min != null && k.max != null ? `${k.min}–${k.max}` : ""}{current == null ? " · host default" : ""}</span>
                    </Space>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      </section>
    </div>
  );
}
