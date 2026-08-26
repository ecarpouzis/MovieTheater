/**
 * Collections — the hand-made icon on each top-level collection folder (`f_{id}.jpg`, survives a
 * cache clear). Tiles come from the browse facets; the picture from the media plane.
 */
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, message } from "antd";
import { useRef, useState } from "react";
import { fetchFacets } from "../../booksApi";
import { folderIconUrl, useMediaToken } from "../../booksMedia";
import { bk } from "../../booksQuery";
import { deleteFolderIcon, uploadFolderIcon } from "../adminApi";

function Tile({ id, name, count, epoch }: { id: number; name: string; count: number; epoch: number }) {
  const qc = useQueryClient();
  const input = useRef<HTMLInputElement>(null);
  const [nonce, setNonce] = useState(0);
  const [missing, setMissing] = useState(false);
  const refresh = () => { setMissing(false); setNonce((n) => n + 1); void qc.invalidateQueries({ queryKey: ["catalog", "facets"] }); void qc.invalidateQueries({ queryKey: bk.facets("comic") }); };
  const upload = useMutation({ mutationFn: (f: File) => uploadFolderIcon(id, f), onSuccess: () => { message.success(`Icon set on ${name}.`); refresh(); }, onError: (e) => message.error(e instanceof Error ? e.message : "Upload failed.") });
  const remove = useMutation({ mutationFn: () => deleteFolderIcon(id), onSuccess: () => { message.success(`Icon removed from ${name}.`); refresh(); }, onError: (e) => message.error(e instanceof Error ? e.message : "Delete failed.") });
  const src = folderIconUrl(id);
  return (
    <div className="bka-tile">
      <button type="button" className="bka-tile-art" onClick={() => input.current?.click()} disabled={upload.isPending} title="Upload a new icon">
        {src && !missing ? <img key={`${nonce}:${epoch}`} src={`${src}${src.includes("?") ? "&" : "?"}v=${nonce}`} alt="" onError={() => setMissing(true)} /> : <span className="bka-tile-empty">no icon</span>}
        <span className="bka-tile-hover">{upload.isPending ? "Uploading…" : "Upload"}</span>
      </button>
      <input ref={input} type="file" accept="image/*" style={{ display: "none" }} onChange={(e) => { const f = e.target.files?.[0]; if (f) upload.mutate(f); e.target.value = ""; }} />
      <div className="bka-tile-name" title={name}>{name}</div>
      <div className="bka-tile-sub">{count.toLocaleString()} comics · <code>#{id}</code></div>
      <Button size="small" type="link" danger onClick={() => remove.mutate()} disabled={remove.isPending}>Remove icon</Button>
    </div>
  );
}

export default function CollectionsTab() {
  const { epoch } = useMediaToken();
  const facets = useQuery({ queryKey: bk.facets("comic"), queryFn: ({ signal }) => fetchFacets("comic", signal), staleTime: 5 * 60 * 1000 });
  const collections = [...(facets.data?.collections ?? [])].sort((a, b) => a.name.localeCompare(b.name));
  return (
    <div className="bka-tab">
      <section className="bka-card">
        <header className="bka-card-head"><div className="bka-card-text"><h3 className="bka-card-title">Collection icons</h3><p className="bka-card-desc">Click a tile to upload its icon (JPG/PNG/WebP, up to 4 MB). Icons are hand-made art: a cache clear never touches them.</p></div></header>
        {facets.isLoading ? <div className="bka-muted">Loading…</div> : collections.length === 0 ? <div className="bka-muted">No collections yet.</div> : (
          <div className="bka-tiles">{collections.map((c) => <Tile key={c.id} id={c.id} name={c.name} count={c.count} epoch={epoch} />)}</div>
        )}
      </section>
    </div>
  );
}
