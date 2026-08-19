import { useState, useEffect, useCallback, useRef } from "react";
import { Modal, Input, Select, Checkbox, Button, InputNumber, Slider, Tag, message, Popconfirm } from "antd";
import { MovieAPI } from "../../MovieAPI";
import "./ChannelAdminModal.css";
import "../../Components/SheetModal.css";

const STRATEGY_OPTIONS = [
  { value: "SeededShuffle", label: "Shuffle (seeded)" },
  { value: "WeightedShuffle", label: "Weighted shuffle (favor rewatchable + well-rated)" },
  { value: "ReleaseDate", label: "Release order (oldest → newest)" },
  { value: "NewestFirst", label: "Newest first" },
  { value: "Marathon", label: "Marathon (franchise/watch order)" },
  { value: "EpisodeRoundRobin", label: "Episode round-robin (rotate shows)" },
];

const GENRE_MODE_OPTIONS = [
  { value: "any", label: "Any of these genres" },
  { value: "all", label: "All of these genres" },
];

const KIND_OPTIONS = [
  { value: "Movies", label: "Movies" },
  { value: "Series", label: "TV / Episodes" },
  { value: "Misc", label: "Misc videos" },
];

// The six AI "feels like X" dials (TitleInsight, 0–100). A full 0–100 range means "no constraint".
const SLIDERS = [
  { key: "cultClassic", label: "Cult classic" },
  { key: "surrealism", label: "Surrealism" },
  { key: "intensity", label: "Intensity / darkness" },
  { key: "novelty", label: "Novelty" },
  { key: "rewatchability", label: "Rewatchability" },
  { key: "energy", label: "Energy" },
];

// A fresh channel: empty filter, enabled, shuffled, movies-only (matches the engine's back-compat default).
function blankChannel() {
  return {
    id: null,
    name: "",
    description: "",
    sortOrder: 0,
    enabled: true,
    category: "",
    scheduleStrategy: "SeededShuffle",
    seasonStartMonth: null,
    seasonStartDay: null,
    seasonEndMonth: null,
    seasonEndDay: null,
    filter: {
      genreIds: [],
      genreMode: "any",
      yearMin: null,
      yearMax: null,
      maxMpaRatingId: null,
      excludeAdult: true,
      unwatchedByUserId: null,
      excludeRemoveFromRandom: true,
      kinds: ["Movies"],
      pathContains: [],
      languages: [],
      excludeLanguages: [],
      minViewers: null,
      cultClassic: null,
      surrealism: null,
      intensity: null,
      novelty: null,
      rewatchability: null,
      energy: null,
      tags: [],
      credits: [],
    },
  };
}

// Async person typeahead for the credits picker; seeds its options from a rule's existing people so
// previously-chosen names render before any search.
function PersonPicker({ value, people, onChange }) {
  const [options, setOptions] = useState(() => (people || []).map((p) => ({ value: p.id, label: p.name })));
  const timer = useRef(null);

  const search = (q) => {
    clearTimeout(timer.current);
    const term = (q || "").trim();
    if (term.length < 2) return;
    timer.current = setTimeout(async () => {
      try {
        const r = await MovieAPI.getChannelAdminPeople(term);
        if (!r.ok) return;
        const found = await r.json();
        setOptions((prev) => {
          const seen = new Map(prev.map((o) => [o.value, o]));
          for (const p of found) seen.set(p.id, { value: p.id, label: p.name });
          return [...seen.values()];
        });
      } catch {
        /* ignore */
      }
    }, 250);
  };

  return (
    <Select
      mode="multiple"
      showSearch
      filterOption={false}
      value={value}
      onSearch={search}
      onChange={onChange}
      options={options}
      placeholder="Search people…"
      notFoundContent={null}
      style={{ width: "100%" }}
    />
  );
}

/**
 * Channel management for editors (CanEditMovies). List existing channels, then create/edit/delete.
 * The full filter is editable — content kinds, schedule strategy, seasonal window, AI sliders, tag
 * rules, credits, languages, path. Save preserves catalog-authored facets the form doesn't surface
 * (the server parses the stored filter and overwrites only form fields), and regenerates the
 * not-yet-aired schedule, so changes take effect going forward.
 */
function ChannelAdminModal({ open, onClose, onChanged }) {
  const [meta, setMeta] = useState({ genres: [], ratings: [], strategies: [], kinds: [], creditRoles: [], tagCategories: [], tagVocab: {} });
  const [channels, setChannels] = useState(null); // null = loading
  const [editing, setEditing] = useState(null); // null = list view; object = form view
  const [saving, setSaving] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [shelves, setShelves] = useState(null); // null = not in shelves mode; array of category names = reorder view

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
    setShelves(null);
    setDirty(false);
    setChannels(null);
    loadList();

    MovieAPI.getChannelAdminMeta()
      .then((r) => (r.ok ? r.json() : null))
      .then((m) => m && setMeta((prev) => ({ ...prev, ...m })))
      .catch(() => {});
  }, [open, loadList]);

  const setField = (key, value) => setEditing((c) => ({ ...c, [key]: value }));
  const setFilterField = (key, value) => setEditing((c) => ({ ...c, filter: { ...c.filter, [key]: value } }));

  // ── tag rules ──
  const addTagRule = () =>
    setFilterField("tags", [...editing.filter.tags, { category: meta.tagCategories[0] || "Subgenre", values: [], mode: "any", negate: false }]);
  const updateTagRule = (i, patch) =>
    setFilterField("tags", editing.filter.tags.map((t, j) => (j === i ? { ...t, ...patch } : t)));
  const removeTagRule = (i) => setFilterField("tags", editing.filter.tags.filter((_, j) => j !== i));

  // ── credit rules ──
  const addCreditRule = () =>
    setFilterField("credits", [...editing.filter.credits, { role: null, personIds: [], people: [] }]);
  const updateCreditRule = (i, patch) =>
    setFilterField("credits", editing.filter.credits.map((c, j) => (j === i ? { ...c, ...patch } : c)));
  const removeCreditRule = (i) => setFilterField("credits", editing.filter.credits.filter((_, j) => j !== i));

  // ── shelves (category) order ──
  const openShelves = async () => {
    try {
      const res = await MovieAPI.getChannelShelves();
      setShelves(res.ok ? await res.json() : []);
    } catch {
      setShelves([]);
    }
  };
  const moveShelf = (i, dir) =>
    setShelves((s) => {
      const j = i + dir;
      if (j < 0 || j >= s.length) return s;
      const next = s.slice();
      [next[i], next[j]] = [next[j], next[i]];
      return next;
    });
  const handleSaveShelves = async () => {
    setSaving(true);
    try {
      const res = await MovieAPI.saveChannelShelves(shelves);
      if (!res.ok) throw new Error();
      message.success("Shelf order saved.");
      setDirty(true);
      setShelves(null);
    } catch {
      message.error("Couldn't save shelf order.");
    } finally {
      setSaving(false);
    }
  };

  const handleSave = async () => {
    if (!editing.name?.trim()) {
      message.error("Name is required.");
      return;
    }
    setSaving(true);
    try {
      const f = editing.filter;
      const payload = {
        Id: editing.id,
        Name: editing.name.trim(),
        Description: editing.description?.trim() || null,
        SortOrder: editing.sortOrder ?? 0,
        Enabled: editing.enabled,
        Category: editing.category?.trim() || null,
        ScheduleStrategy: editing.scheduleStrategy,
        SeasonStartMonth: editing.seasonStartMonth,
        SeasonStartDay: editing.seasonStartDay,
        SeasonEndMonth: editing.seasonEndMonth,
        SeasonEndDay: editing.seasonEndDay,
        GenreIds: f.genreIds,
        GenreMode: f.genreMode,
        YearMin: f.yearMin,
        YearMax: f.yearMax,
        MaxMpaRatingId: f.maxMpaRatingId,
        ExcludeAdult: f.excludeAdult,
        ExcludeRemoveFromRandom: f.excludeRemoveFromRandom,
        Kinds: f.kinds,
        PathContains: f.pathContains,
        Languages: f.languages,
        ExcludeLanguages: f.excludeLanguages,
        MinViewers: f.minViewers,
        CultClassic: f.cultClassic,
        Surrealism: f.surrealism,
        Intensity: f.intensity,
        Novelty: f.novelty,
        Rewatchability: f.rewatchability,
        Energy: f.energy,
        Tags: f.tags,
        Credits: f.credits.map((c) => ({ PersonIds: c.personIds, Role: c.role })),
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

  // ── list view ──
  const listView = (
    <div className="cha-list">
      <div className="cha-list-head">
        <span>{channels?.length ?? 0} channel{channels?.length === 1 ? "" : "s"}</span>
        <span className="cha-list-head-actions">
          <Button size="small" onClick={openShelves}>Reorder shelves</Button>
          <Button type="primary" size="small" onClick={() => setEditing(blankChannel())}>+ New channel</Button>
        </span>
      </div>
      {channels === null && <div className="cha-empty">Loading…</div>}
      {channels?.length === 0 && <div className="cha-empty">No channels yet.</div>}
      {channels?.map((c) => (
        <div key={c.id} className="cha-row">
          <div className="cha-row-main">
            <span className="cha-row-name">{c.name}</span>
            {!c.enabled && <Tag color="default">disabled</Tag>}
            {c.category && <Tag color="purple">{c.category}</Tag>}
            {c.catalogKey && <Tag color="geekblue">catalog</Tag>}
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

  const renderSlider = (key, label) => {
    const r = editing.filter[key];
    const val = [r?.min ?? 0, r?.max ?? 100];
    return (
      <label className="cha-field" key={key}>
        <span>{label} <span className="cha-slider-val">{val[0]}–{val[1]}</span></span>
        <Slider
          range
          min={0}
          max={100}
          value={val}
          onChange={([lo, hi]) => setFilterField(key, lo <= 0 && hi >= 100 ? null : { min: lo, max: hi })}
        />
      </label>
    );
  };

  // ── form view ──
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
          <span>Category</span>
          <Input value={editing.category} maxLength={48} onChange={(e) => setField("category", e.target.value)} placeholder="e.g. Genres, Anime" />
        </label>
      </div>
      <label className="cha-field">
        <span>Schedule strategy</span>
        <Select value={editing.scheduleStrategy} options={STRATEGY_OPTIONS} onChange={(v) => setField("scheduleStrategy", v)} style={{ width: "100%" }} />
      </label>

      <div className="cha-section">What it airs</div>
      <label className="cha-field">
        <span>Content kinds</span>
        <Checkbox.Group
          options={KIND_OPTIONS}
          value={editing.filter.kinds}
          onChange={(v) => setFilterField("kinds", v.length ? v : ["Movies"])}
        />
      </label>
      <label className="cha-field">
        <span>Genres</span>
        <Select
          mode="multiple" allowClear
          value={editing.filter.genreIds}
          onChange={(v) => setFilterField("genreIds", v)}
          options={meta.genres.map((g) => ({ value: g.id, label: g.name }))}
          placeholder="All genres" optionFilterProp="label" style={{ width: "100%" }}
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
          allowClear value={editing.filter.maxMpaRatingId}
          onChange={(v) => setFilterField("maxMpaRatingId", v ?? null)}
          options={meta.ratings.map((r) => ({ value: r.id, label: r.name }))}
          placeholder="No ceiling" style={{ width: "100%" }}
        />
      </label>

      <div className="cha-section">AI dials</div>
      {SLIDERS.map((s) => renderSlider(s.key, s.label))}

      <div className="cha-section">Tags</div>
      {editing.filter.tags.map((t, i) => (
        <div className="cha-rule" key={i}>
          <div className="cha-rule-head">
            <Select
              size="small" value={t.category}
              onChange={(v) => updateTagRule(i, { category: v, values: [] })}
              options={meta.tagCategories.map((c) => ({ value: c, label: c }))}
              style={{ width: 130 }}
            />
            <Select size="small" value={t.mode} onChange={(v) => updateTagRule(i, { mode: v })} style={{ width: 78 }}
              options={[{ value: "any", label: "any" }, { value: "all", label: "all" }]} />
            <Checkbox checked={t.negate} onChange={(e) => updateTagRule(i, { negate: e.target.checked })}>not</Checkbox>
            <Button size="small" type="text" danger onClick={() => removeTagRule(i)}>✕</Button>
          </div>
          <Select
            mode="tags" size="small" value={t.values}
            onChange={(v) => updateTagRule(i, { values: v })}
            options={(meta.tagVocab[t.category] || []).map((v) => ({ value: v, label: v }))}
            placeholder="values…" style={{ width: "100%" }}
          />
        </div>
      ))}
      <Button size="small" onClick={addTagRule} className="cha-add">+ Add tag rule</Button>

      <div className="cha-section">Credits</div>
      {editing.filter.credits.map((c, i) => (
        <div className="cha-rule" key={i}>
          <div className="cha-rule-head">
            <Select
              size="small" allowClear value={c.role} placeholder="Any role"
              onChange={(v) => updateCreditRule(i, { role: v ?? null })}
              options={meta.creditRoles.map((r) => ({ value: r, label: r }))}
              style={{ width: 120 }}
            />
            <span className="cha-rule-hint">match any of:</span>
            <Button size="small" type="text" danger onClick={() => removeCreditRule(i)}>✕</Button>
          </div>
          <PersonPicker value={c.personIds} people={c.people} onChange={(ids) => updateCreditRule(i, { personIds: ids })} />
        </div>
      ))}
      <Button size="small" onClick={addCreditRule} className="cha-add">+ Add credit</Button>

      <div className="cha-section">Source &amp; provenance</div>
      <label className="cha-field">
        <span>Languages (original)</span>
        <Select mode="tags" value={editing.filter.languages} onChange={(v) => setFilterField("languages", v)} placeholder="e.g. ja, fr" style={{ width: "100%" }} />
      </label>
      <label className="cha-field">
        <span>Exclude languages</span>
        <Select mode="tags" value={editing.filter.excludeLanguages} onChange={(v) => setFilterField("excludeLanguages", v)} placeholder="e.g. en (World Cinema)" style={{ width: "100%" }} />
      </label>
      <label className="cha-field">
        <span>Path contains (on-disk collections)</span>
        <Select mode="tags" value={editing.filter.pathContains} onChange={(v) => setFilterField("pathContains", v)} placeholder='e.g. "Looney Tunes", "Criterion"' style={{ width: "100%" }} />
      </label>
      <label className="cha-field cha-field--narrow">
        <span>Min community viewers</span>
        <InputNumber min={0} value={editing.filter.minViewers} onChange={(v) => setFilterField("minViewers", v)} placeholder="—" style={{ width: "100%" }} />
      </label>

      <div className="cha-section">Seasonal window (optional)</div>
      <div className="cha-field-row">
        <label className="cha-field cha-field--narrow">
          <span>From month</span>
          <InputNumber min={1} max={12} value={editing.seasonStartMonth} onChange={(v) => setField("seasonStartMonth", v)} placeholder="—" style={{ width: "100%" }} />
        </label>
        <label className="cha-field cha-field--narrow">
          <span>From day</span>
          <InputNumber min={1} max={31} value={editing.seasonStartDay} onChange={(v) => setField("seasonStartDay", v)} placeholder="—" style={{ width: "100%" }} />
        </label>
        <label className="cha-field cha-field--narrow">
          <span>To month</span>
          <InputNumber min={1} max={12} value={editing.seasonEndMonth} onChange={(v) => setField("seasonEndMonth", v)} placeholder="—" style={{ width: "100%" }} />
        </label>
        <label className="cha-field cha-field--narrow">
          <span>To day</span>
          <InputNumber min={1} max={31} value={editing.seasonEndDay} onChange={(v) => setField("seasonEndDay", v)} placeholder="—" style={{ width: "100%" }} />
        </label>
      </div>

      <div className="cha-section">Options</div>
      <label className="cha-checkbox">
        <Checkbox checked={editing.filter.excludeAdult} onChange={(e) => setFilterField("excludeAdult", e.target.checked)}>
          Exclude adult (NC-17 / X) titles
        </Checkbox>
      </label>
      <label className="cha-checkbox">
        <Checkbox checked={editing.filter.excludeRemoveFromRandom} onChange={(e) => setFilterField("excludeRemoveFromRandom", e.target.checked)}>
          Exclude titles flagged "remove from random"
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

  const shelvesView = shelves && (
    <div className="cha-shelves">
      <div className="cha-shelves-hint">The order shelves appear in the guide and the homepage rail.</div>
      {shelves.map((name, i) => (
        <div key={name} className="cha-shelf-row">
          <span className="cha-shelf-num">{i + 1}</span>
          <span className="cha-shelf-name">{name}</span>
          <span className="cha-shelf-actions">
            <Button size="small" disabled={i === 0} onClick={() => moveShelf(i, -1)}>↑</Button>
            <Button size="small" disabled={i === shelves.length - 1} onClick={() => moveShelf(i, 1)}>↓</Button>
          </span>
        </div>
      ))}
      {shelves.length === 0 && <div className="cha-empty">No shelves.</div>}
    </div>
  );

  return (
    <Modal
      open={open}
      onCancel={handleClose}
      title={shelves ? "Reorder shelves" : editing ? (editing.id ? "Edit channel" : "New channel") : "TV channels"}
      width={600}
      centered
      /* The shell bounds the dialog to the viewport and makes the BODY the scroller, so the
         hand-rolled `maxHeight: calc(100vh - 200px)` body cap this used to carry is gone — and on a
         phone it becomes a full-screen sheet like every other dialog. */
      wrapClassName="sheet-modal"
      footer={
        shelves
          ? [
              <Button key="back" onClick={() => setShelves(null)}>Back</Button>,
              <Button key="save" type="primary" loading={saving} onClick={handleSaveShelves}>Save order</Button>,
            ]
          : editing
          ? [
              <Button key="back" onClick={() => setEditing(null)}>Back</Button>,
              <Button key="save" type="primary" loading={saving} onClick={handleSave}>Save</Button>,
            ]
          : [<Button key="done" onClick={handleClose}>Done</Button>]
      }
    >
      {shelves ? shelvesView : editing ? formView : listView}
    </Modal>
  );
}

export default ChannelAdminModal;
