import { useState, useEffect, useCallback } from "react";
import { Modal, Input, Select, Checkbox, Button, InputNumber, Tag, message, Popconfirm } from "antd";
import { MovieAPI } from "../../MovieAPI";
import "./ChannelAdminModal.css";

const SHUFFLE_OPTIONS = [
  { value: "SeededShuffle", label: "Shuffle (seeded)" },
  { value: "ReleaseDate", label: "Release order" },
];

const GENRE_MODE_OPTIONS = [
  { value: "any", label: "Any of these genres" },
  { value: "all", label: "All of these genres" },
];

// A fresh channel: empty filter, enabled, shuffled. Mirrors SeedChannelsCommand's
// defaults so a hand-made channel behaves like a seeded one.
function blankChannel() {
  return {
    id: null,
    name: "",
    description: "",
    sortOrder: 0,
    enabled: true,
    shuffleMode: "SeededShuffle",
    filter: {
      genreIds: [],
      genreMode: "any",
      yearMin: null,
      yearMax: null,
      maxMpaRatingId: null,
      unwatchedByUserId: null,
      excludeRemoveFromRandom: true,
    },
  };
}

/**
 * Channel management for editors (CanEditMovies). List existing channels, then
 * create/edit/delete. Saving regenerates the not-yet-aired schedule server-side,
 * so filter changes take effect going forward. onChanged() lets TvPage refresh
 * its channel list after edits.
 */
function ChannelAdminModal({ open, onClose, onChanged }) {
  const [meta, setMeta] = useState({ genres: [], ratings: [] });
  const [channels, setChannels] = useState(null); // null = loading
  const [editing, setEditing] = useState(null); // null = list view; object = form view
  const [saving, setSaving] = useState(false);
  const [dirty, setDirty] = useState(false); // whether any save/delete happened this session

  const loadList = useCallback(async () => {
    try {
      const res = await MovieAPI.getChannelAdminList();
      if (!res.ok) throw new Error();
      setChannels(await res.json());
    } catch {
      setChannels([]);
      message.error("Couldn't load channels.");
    }
  }, []);

  useEffect(() => {
    if (!open) return;
    setEditing(null);
    setDirty(false);
    setChannels(null);
    loadList();

    MovieAPI.getChannelAdminMeta()
      .then((r) => (r.ok ? r.json() : { genres: [], ratings: [] }))
      .then(setMeta)
      .catch(() => setMeta({ genres: [], ratings: [] }));
  }, [open, loadList]);

  const setField = (key, value) => setEditing((c) => ({ ...c, [key]: value }));
  const setFilterField = (key, value) =>
    setEditing((c) => ({ ...c, filter: { ...c.filter, [key]: value } }));

  const handleSave = async () => {
    if (!editing.name?.trim()) {
      message.error("Name is required.");
      return;
    }
    setSaving(true);
    try {
      const payload = {
        Id: editing.id,
        Name: editing.name.trim(),
        Description: editing.description?.trim() || null,
        SortOrder: editing.sortOrder ?? 0,
        Enabled: editing.enabled,
        ShuffleMode: editing.shuffleMode,
        GenreIds: editing.filter.genreIds,
        GenreMode: editing.filter.genreMode,
        YearMin: editing.filter.yearMin,
        YearMax: editing.filter.yearMax,
        MaxMpaRatingId: editing.filter.maxMpaRatingId,
        UnwatchedByUserId: editing.filter.unwatchedByUserId, // preserved on round-trip
        ExcludeRemoveFromRandom: editing.filter.excludeRemoveFromRandom,
      };
      const res = await MovieAPI.saveChannel(payload);
      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        throw new Error(body.message || "Save failed.");
      }
      message.success(editing.id ? "Channel updated." : "Channel created.");
      setDirty(true);
      setEditing(null);
      await loadList();
    } catch (err) {
      message.error(err.message || "Save failed.");
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async (id) => {
    try {
      const res = await MovieAPI.deleteChannel(id);
      if (!res.ok) throw new Error();
      message.success("Channel deleted.");
      setDirty(true);
      await loadList();
    } catch {
      message.error("Delete failed.");
    }
  };

  const handleClose = () => {
    if (dirty) onChanged?.();
    onClose();
  };

  const ratingName = (id) => meta.ratings.find((r) => r.id === id)?.name ?? `#${id}`;

  // ── list view ───────────────────────────────────────────────────────────────
  const listView = (
    <div className="cha-list">
      <div className="cha-list-head">
        <span>{channels?.length ?? 0} channel{channels?.length === 1 ? "" : "s"}</span>
        <Button type="primary" size="small" onClick={() => setEditing(blankChannel())}>
          + New channel
        </Button>
      </div>
      {channels === null && <div className="cha-empty">Loading…</div>}
      {channels?.length === 0 && <div className="cha-empty">No channels yet.</div>}
      {channels?.map((c) => (
        <div key={c.id} className="cha-row">
          <div className="cha-row-main">
            <span className="cha-row-name">{c.name}</span>
            {!c.enabled && <Tag color="default">disabled</Tag>}
            {c.filter.maxMpaRatingId != null && <Tag color="gold">≤ {ratingName(c.filter.maxMpaRatingId)}</Tag>}
            {c.filter.genreIds.length > 0 && <Tag color="blue">{c.filter.genreIds.length} genre{c.filter.genreIds.length === 1 ? "" : "s"}</Tag>}
            {c.description && <span className="cha-row-desc">{c.description}</span>}
          </div>
          <div className="cha-row-actions">
            <Button size="small" onClick={() => setEditing(structuredClone(c))}>Edit</Button>
            <Popconfirm title="Delete this channel?" onConfirm={() => handleDelete(c.id)} okText="Delete" okType="danger">
              <Button size="small" danger>Delete</Button>
            </Popconfirm>
          </div>
        </div>
      ))}
    </div>
  );

  // ── form view ─────────────────────────────────────────────────────────────────
  const formView = editing && (
    <div className="cha-form">
      <label className="cha-field">
        <span>Name</span>
        <Input value={editing.name} maxLength={64} onChange={(e) => setField("name", e.target.value)} placeholder="e.g. Westerns" />
      </label>
      <label className="cha-field">
        <span>Description</span>
        <Input value={editing.description} maxLength={256} onChange={(e) => setField("description", e.target.value)} placeholder="Optional tagline" />
      </label>
      <div className="cha-field-row">
        <label className="cha-field cha-field--narrow">
          <span>Sort order</span>
          <InputNumber value={editing.sortOrder} onChange={(v) => setField("sortOrder", v ?? 0)} style={{ width: "100%" }} />
        </label>
        <label className="cha-field">
          <span>Order</span>
          <Select value={editing.shuffleMode} options={SHUFFLE_OPTIONS} onChange={(v) => setField("shuffleMode", v)} style={{ width: "100%" }} />
        </label>
      </div>

      <div className="cha-section">Filter</div>

      <label className="cha-field">
        <span>Genres</span>
        <Select
          mode="multiple"
          allowClear
          value={editing.filter.genreIds}
          onChange={(v) => setFilterField("genreIds", v)}
          options={meta.genres.map((g) => ({ value: g.id, label: g.name }))}
          placeholder="All genres"
          optionFilterProp="label"
          style={{ width: "100%" }}
        />
      </label>
      {editing.filter.genreIds.length > 1 && (
        <label className="cha-field">
          <span>Genre match</span>
          <Select value={editing.filter.genreMode} options={GENRE_MODE_OPTIONS} onChange={(v) => setFilterField("genreMode", v)} style={{ width: "100%" }} />
        </label>
      )}
      <div className="cha-field-row">
        <label className="cha-field cha-field--narrow">
          <span>Year from</span>
          <InputNumber value={editing.filter.yearMin} onChange={(v) => setFilterField("yearMin", v)} placeholder="—" style={{ width: "100%" }} />
        </label>
        <label className="cha-field cha-field--narrow">
          <span>Year to</span>
          <InputNumber value={editing.filter.yearMax} onChange={(v) => setFilterField("yearMax", v)} placeholder="—" style={{ width: "100%" }} />
        </label>
      </div>
      <label className="cha-field">
        <span>Rating ceiling</span>
        <Select
          allowClear
          value={editing.filter.maxMpaRatingId}
          onChange={(v) => setFilterField("maxMpaRatingId", v ?? null)}
          options={meta.ratings.map((r) => ({ value: r.id, label: r.name }))}
          placeholder="No ceiling"
          style={{ width: "100%" }}
        />
      </label>
      <label className="cha-checkbox">
        <Checkbox checked={editing.filter.excludeRemoveFromRandom} onChange={(e) => setFilterField("excludeRemoveFromRandom", e.target.checked)}>
          Exclude movies flagged "remove from random"
        </Checkbox>
      </label>
      <label className="cha-checkbox">
        <Checkbox checked={editing.enabled} onChange={(e) => setField("enabled", e.target.checked)}>
          Enabled (visible to viewers)
        </Checkbox>
      </label>
      {editing.filter.unwatchedByUserId != null && (
        <div className="cha-note">This channel is personalized (unseen-by filter); that setting is preserved on save.</div>
      )}
    </div>
  );

  return (
    <Modal
      open={open}
      onCancel={handleClose}
      title={editing ? (editing.id ? "Edit channel" : "New channel") : "TV channels"}
      width={560}
      footer={
        editing
          ? [
              <Button key="back" onClick={() => setEditing(null)}>Back</Button>,
              <Button key="save" type="primary" loading={saving} onClick={handleSave}>Save</Button>,
            ]
          : [<Button key="done" onClick={handleClose}>Done</Button>]
      }
    >
      {editing ? formView : listView}
    </Modal>
  );
}

export default ChannelAdminModal;
