import { useState, useRef } from "react";
import { Button, Card, Input, message } from "antd";
import { MovieAPI } from "../../MovieAPI";

function normalizeResult(item, index) {
  return {
    index,
    input: item?.input,
    found: !!item?.found,
    exists: !!item?.exists,
    id: item?.id ?? null,
    bggThingId: item?.bggThingId ?? null,
    name: item?.name ?? null,
    yearPublished: item?.yearPublished ?? null,
    minPlayers: item?.minPlayers ?? null,
    maxPlayers: item?.maxPlayers ?? null,
    playingTime: item?.playingTime ?? null,
    minAge: item?.minAge ?? null,
    description: item?.description ?? null,
    imageUrl: item?.imageUrl ?? item?.thumbnailUrl ?? null,
    message: item?.message ?? null,
    inserted: false,
    inserting: false,
  };
}

function BoardgameInsertCard({ item, onInserted }) {
  async function insert() {
    if (!item.bggThingId || item.inserting || item.inserted || item.exists) return;

    onInserted(item.index, { inserting: true });
    try {
      const response = await MovieAPI.insertBoardgameFromBgg(item.bggThingId);
      const body = await response.json().catch(() => ({}));
      if (!response.ok || body?.success === false) {
        message.error(body?.message || `Failed to insert ${item.name || item.input}`);
        onInserted(item.index, { inserting: false });
        return;
      }

      message.success(`Inserted: ${item.name || item.input}`);
      onInserted(item.index, { inserted: true, exists: true, inserting: false, id: body?.data?.id ?? item.id });
    } catch (err) {
      message.error(err?.message || "Insert failed");
      onInserted(item.index, { inserting: false });
    }
  }

  const players = item.minPlayers && item.maxPlayers
    ? (item.minPlayers === item.maxPlayers ? `${item.minPlayers}` : `${item.minPlayers}-${item.maxPlayers}`)
    : item.minPlayers ?? item.maxPlayers ?? "?";

  return (
    <Card style={{ marginTop: 12 }}>
      <div style={{ display: "flex", gap: 12 }}>
        {item.imageUrl ? (
          <img
            src={item.imageUrl}
            alt={item.name || item.input || "Boardgame"}
            style={{ width: 80, height: 110, objectFit: "cover", borderRadius: 4 }}
          />
        ) : null}
        <div style={{ flex: 1 }}>
          <div><b>{item.name || item.input || "Unknown"}</b>{item.yearPublished ? ` (${item.yearPublished})` : ""}</div>
          <div>BGG ID: {item.bggThingId ?? "N/A"}</div>
          <div>Players: {players} • Time: {item.playingTime ?? "?"} min • Age: {item.minAge ?? "?"}+</div>
          {!item.found ? <div style={{ color: "#ff7875" }}>{item.message || "Not found"}</div> : null}
          {item.exists && !item.inserted ? <div style={{ color: "#faad14" }}>Already exists in database</div> : null}
          {item.inserted ? <div style={{ color: "#52c41a" }}>Inserted</div> : null}
          {item.description ? (
            <div style={{ marginTop: 6, opacity: 0.85 }}>{String(item.description).replace(/<[^>]*>/g, "").slice(0, 260)}</div>
          ) : null}
        </div>
        <div>
          <Button
            type="primary"
            disabled={!item.found || item.exists || item.inserted}
            loading={item.inserting}
            onClick={insert}
          >
            Insert
          </Button>
        </div>
      </div>
    </Card>
  );
}

function ts() {
  return new Date().toISOString().replace("T", " ").substring(0, 23);
}

function BoardgameBatchInsertPage() {
  const [batchInput, setBatchInput] = useState("");
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(false);
  const [logLines, setLogLines] = useState([]);
  const logRef = useRef(null);

  function appendLog(line) {
    setLogLines((prev) => {
      const next = [...prev, `[${ts()}] ${line}`];
      // scroll log to bottom after render
      setTimeout(() => {
        if (logRef.current) logRef.current.scrollTop = logRef.current.scrollHeight;
      }, 0);
      return next;
    });
  }

  function updateItem(index, patch) {
    setItems((prev) => prev.map((x) => (x.index === index ? { ...x, ...patch } : x)));
  }

  async function generateBatch() {
    const inputs = (batchInput || "")
      .split("\n")
      .map((x) => x.trim())
      .filter((x) => x.length > 0);

    if (inputs.length === 0) {
      message.warning("Enter boardgame names or BGG IDs first.");
      return;
    }

    setItems([]);
    setLogLines([]);
    setLoading(true);
    appendLog(`Starting lookup for ${inputs.length} game(s)…`);

    const allItems = [];
    for (let i = 0; i < inputs.length; i++) {
      const input = inputs[i];
      appendLog(`[${i + 1}/${inputs.length}] Querying: "${input}"`);
      const t0 = Date.now();
      try {
        const response = await MovieAPI.boardgameLookupFromInputs([input]);
        const elapsed = Date.now() - t0;
        const raw = Array.isArray(response) && response.length > 0 ? response[0] : null;
        const item = normalizeResult(raw, allItems.length);
        allItems.push(item);
        setItems([...allItems]);
        if (item.found) {
          appendLog(`  → Found: "${item.name}" (BGG #${item.bggThingId}) — ${elapsed}ms`);
        } else {
          appendLog(`  → Not found: ${item.message || "no result"} — ${elapsed}ms`);
        }
      } catch (err) {
        const elapsed = Date.now() - t0;
        appendLog(`  → ERROR: ${err?.message || "unknown error"} — ${elapsed}ms`);
        allItems.push(normalizeResult({ input, found: false, message: err?.message }, allItems.length));
        setItems([...allItems]);
      }
    }

    const found = allItems.filter((x) => x.found).length;
    appendLog(`Done. ${found}/${allItems.length} found.`);
    setLoading(false);
  }

  async function insertAll() {
    for (const item of items) {
      if (!item.found || item.exists || item.inserted) continue;
      // sequential by design
      // eslint-disable-next-line no-await-in-loop
      await (async () => {
        updateItem(item.index, { inserting: true });
        try {
          const response = await MovieAPI.insertBoardgameFromBgg(item.bggThingId);
          const body = await response.json().catch(() => ({}));
          if (!response.ok || body?.success === false) {
            updateItem(item.index, { inserting: false });
            return;
          }
          updateItem(item.index, { inserted: true, exists: true, inserting: false, id: body?.data?.id ?? item.id });
        } catch {
          updateItem(item.index, { inserting: false });
        }
      })();
    }

    message.success("Batch insert complete.");
  }

  return (
    <div>
      <h2>Boardgame Batch Insert</h2>
      <Input.TextArea
        rows={8}
        value={batchInput}
        onChange={(e) => setBatchInput(e.target.value)}
        placeholder="One per line: boardgame name OR numeric BGG ID"
      />
      <div style={{ marginTop: 8, display: "flex", gap: 8 }}>
        <Button type="primary" onClick={generateBatch} loading={loading}>Generate Batch</Button>
        <Button onClick={insertAll} disabled={items.length === 0}>Insert All</Button>
      </div>

      {logLines.length > 0 && (
        <div
          ref={logRef}
          style={{
            marginTop: 12,
            padding: "8px 10px",
            background: "#1a1a1a",
            color: "#d4d4d4",
            fontFamily: "monospace",
            fontSize: 12,
            lineHeight: 1.6,
            borderRadius: 4,
            maxHeight: 200,
            overflowY: "auto",
            whiteSpace: "pre-wrap",
            wordBreak: "break-all",
            userSelect: "all",
          }}
        >
          {logLines.join("\n")}
        </div>
      )}

      {items.map((item) => (
        <BoardgameInsertCard key={`${item.index}-${item.input}`} item={item} onInserted={updateItem} />
      ))}
    </div>
  );
}

export default BoardgameBatchInsertPage;
