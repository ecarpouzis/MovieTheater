import { useCallback, useEffect, useMemo, useState, type CSSProperties } from "react";
import Card from "../cards/Card";
import CardImage, { hueSvg } from "../cards/CardImage";
import type { CardItem, DirectoryNode, DirectorySource } from "../types";
import { StreamEmpty, StreamFailed, StreamLoading } from "./StreamStates";
import type { ViewProps } from "./ViewProps";

/**
 * Directory — the section's own hierarchy as a file explorer: a breadcrumb, the current node's
 * children as tiles, its loose items as cards. Folders for Books/Photos, franchise → titles for
 * Movies, artist → albums for Music, system → games for Arcade. The drill stack is view state
 * (it resets when the section's filters change); nodes with nothing under them hide unless the
 * "show empty" tweak is on.
 */
export const DIRECTORY_ITEM_TOP = 500;
const DIR_BASE_CELL = 200;

function hueOf(id: string): number {
  let h = 0;
  for (let i = 0; i < id.length; i += 1) h = (h * 31 + id.charCodeAt(i)) % 360;
  return h;
}

/** A level's first tiles are above the fold: their art loads eagerly (the same dozen the Grid marks). */
const DIR_EAGER = 12;

function NodeTile({ node, cellH, hoverClass, noun, eager, onOpen }: { node: DirectoryNode; cellH: number; hoverClass: string; noun: string; eager: boolean; onOpen: (n: DirectoryNode) => void }) {
  const w = Math.round(cellH * 0.66);
  const hue = node.hue ?? hueOf(node.id);
  return (
    <div className={`bx-card bx-dir-node${hoverClass ? ` ${hoverClass}` : ""}`} style={{ "--aspect": 0.66 } as CSSProperties} role="button" tabIndex={0}
      onClick={() => onOpen(node)} onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onOpen(node); } }} aria-label={node.label}>
      <div className="bx-cover" style={{ height: cellH, width: w }}>
        {node.imageUrl ? <CardImage src={node.imageUrl} hue={hue} eager={eager} /> : <img src={hueSvg(hue)} alt="" style={{ position: "absolute", inset: 0, width: "100%", height: "100%", objectFit: "cover" }} />}
        <span className="bx-dir-badge" aria-hidden="true">▸</span>
      </div>
      <div className="bx-meta" style={{ width: w, minWidth: 100 }}>
        <div className="bx-meta-row"><span className="bx-meta-a">{node.count != null ? `${node.count.toLocaleString()} ${node.count === 1 ? noun : `${noun}s`}` : ""}</span></div>
        <div className="bx-meta-title">{node.label}</div>
      </div>
    </div>
  );
}

export interface DirectoryViewProps extends ViewProps {
  showEmpty: boolean;
  /** Start drilled into these nodes (a "Browse this folder" link); a new identity re-seeds the stack. */
  initialStack?: DirectoryNode[];
}

export default function DirectoryView({ source, state, coverScale, metadata, hoverClass, showEmpty, initialStack }: DirectoryViewProps) {
  const dir = source.directory as DirectorySource | undefined;
  const cellH = Math.round(DIR_BASE_CELL * coverScale);
  const [stack, setStack] = useState<DirectoryNode[]>(() => initialStack ?? []);
  const [nodes, setNodes] = useState<DirectoryNode[] | null>(null);
  const [items, setItems] = useState<CardItem[] | null>(null);
  const [error, setError] = useState(false);
  const [nonce, setNonce] = useState(0);
  useEffect(() => { setStack(initialStack ?? []); }, [source.queryKey, initialStack]);
  const current = stack[stack.length - 1] ?? null;

  useEffect(() => {
    if (!dir) return undefined;
    const controller = new AbortController();
    setNodes(null); setItems(null); setError(false);
    const load = current
      ? Promise.all([dir.children(current.id, controller.signal), dir.items(current.id, 0, DIRECTORY_ITEM_TOP, controller.signal)])
      : Promise.all([dir.roots(controller.signal), Promise.resolve({ items: [], total: 0 })]);
    load.then(([ns, page]) => { if (controller.signal.aborted) return; setNodes(ns); setItems(page.items); })
      .catch((err: unknown) => { if (controller.signal.aborted || (err as { name?: string })?.name === "AbortError") return; setError(true); setNodes([]); setItems([]); });
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dir, current?.id, source.queryKey, nonce, state.sort]);

  const push = useCallback((n: DirectoryNode) => setStack((s) => [...s, n]), []);
  const popTo = useCallback((idx: number) => setStack((s) => s.slice(0, idx + 1)), []);
  const visibleNodes = useMemo(() => {
    const list = (nodes ?? []).filter((n) => showEmpty || n.count == null || n.count > 0);
    const sort = source.sorts.find((s) => s.value === state.sort);
    return sort && !sort.alpha ? [...list].sort((a, b) => (b.count ?? 0) - (a.count ?? 0)) : [...list].sort((a, b) => a.label.localeCompare(b.label));
  }, [nodes, showEmpty, source.sorts, state.sort]);

  if (!dir) return <StreamEmpty source={source} />;
  const loading = nodes == null || items == null;
  return (
    <div className="bx-drill">
      <nav className="bx-crumb" aria-label="Breadcrumb">
        <button type="button" className="bx-crumb-btn" onClick={() => popTo(-1)}>{source.title ?? "All"}</button>
        {stack.map((n, i) => (
          <span key={`${n.id}-${i}`} className="bx-crumb-seg">
            <span className="bx-crumb-sep">›</span>
            {i < stack.length - 1 ? <button type="button" className="bx-crumb-btn" onClick={() => popTo(i)}>{n.label}</button> : <span className="bx-crumb-current">{n.label}</span>}
          </span>
        ))}
      </nav>
      {error ? <StreamFailed onRetry={() => setNonce((x) => x + 1)} /> : loading ? <StreamLoading /> : (
        <div className="bx-drill-body">
          {visibleNodes.length > 0 && (
            <section className="bx-drill-section">
              {items.length > 0 && <div className="bx-drill-label">Folders</div>}
              <div className="bx-grid" style={{ "--cell": `${cellH}px` } as CSSProperties}>
                {visibleNodes.map((n, i) => <NodeTile key={n.id} node={n} cellH={cellH} hoverClass={hoverClass} noun={source.itemNoun ?? "item"} eager={i < DIR_EAGER} onOpen={push} />)}
              </div>
            </section>
          )}
          {items.length > 0 && (
            <section className="bx-drill-section">
              {visibleNodes.length > 0 && <div className="bx-drill-label">{source.itemNoun ? `${source.itemNoun}s` : "Items"}</div>}
              <div className="bx-grid" style={{ "--cell": `${cellH}px` } as CSSProperties}>
                {items.map((item, i) => <Card key={item.key} item={item} cellH={cellH} metadata={metadata} hoverClass={hoverClass} eager={visibleNodes.length === 0 && i < DIR_EAGER} onOpen={source.onOpen} />)}
              </div>
            </section>
          )}
          {visibleNodes.length === 0 && items.length === 0 && <StreamEmpty source={source} />}
        </div>
      )}
    </div>
  );
}
