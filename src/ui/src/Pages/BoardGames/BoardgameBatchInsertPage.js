import { useState } from "react";
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

function BoardgameBatchInsertPage() {
  const [batchInput, setBatchInput] = useState("");
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(false);

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

    setLoading(true);
    try {
      const response = await MovieAPI.boardgameLookupFromInputs(inputs);
      const normalized = (Array.isArray(response) ? response : []).map((x, i) => normalizeResult(x, i));
      setItems(normalized);
    } catch (err) {
      message.error(err?.message || "Failed to generate batch.");
      setItems([]);
    } finally {
      setLoading(false);
    }
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

      {items.map((item) => (
        <BoardgameInsertCard key={`${item.index}-${item.input}`} item={item} onInserted={updateItem} />
      ))}
    </div>
  );
}

export default BoardgameBatchInsertPage;
