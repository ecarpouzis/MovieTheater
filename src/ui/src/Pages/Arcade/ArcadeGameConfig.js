import { useEffect, useMemo, useState } from "react";
import { Button, Input, Modal, Select, Spin, Tooltip, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import { systemLabel } from "./arcadeSystems";
import "../../Components/SheetModal.css";
import "./ArcadeGameConfig.css";

/**
 * The per-game emulator config tool (docs/arcade-per-game-config.md), reached from the game modal's ⚙
 * Configure button. Editor-only. Edits ONE persistent per-game profile (keyed server-side by game
 * identity, not ROM), so what you set here is how the game plays when anyone presses Start — delivered
 * per-room, effective on the next room with no restart.
 *
 * Laid out like a real emulator's settings dialog: a category rail on the left, the selected category's
 * settings on the right (each with an inline description), and a search that filters across all of them.
 * The control set + exact value tokens come from the server's per-core catalog (libretro silently ignores
 * an unknown token, so the picker only offers real ones); Advanced is the raw own-risk escape hatch.
 *
 * We submit the FULL current value set; the server stores only what differs from the game default, so
 * "leave it alone" never pins a redundant value and Reset clears the profile.
 */
// The config Modal sits at zIndex 1600 (above the game modal); antd Select popups default lower and would
// open BEHIND it. Keep dropdowns above the modal.
const DROPDOWN_STYLE = { zIndex: 1700 };

// Fallback tier list (the server sends the authoritative one in the /Config payload).
const QUALITY_TIERS = [
  { id: "max", label: "Max" },
  { id: "ultra", label: "Ultra" },
  { id: "high", label: "High" },
  { id: "medium", label: "Medium" },
  { id: "low", label: "Low" },
];

// "input" = how the pad drives the game (ScummVM's cursor feel). It leads the rail for the systems that
// have it: on a point-and-click there is no video lever at all, so Controls IS the settings dialog.
const CATEGORY_ORDER = ["video", "input", "hack", "performance", "system", "audio", "other"];
const CATEGORY_LABEL = {
  video: "Video",
  input: "Controls",
  hack: "Enhancements",
  performance: "Performance",
  system: "System",
  audio: "Audio",
  other: "Other",
};

export default function ArcadeGameConfig({ game, onClose }) {
  const gameId = game.id;
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [switching, setSwitching] = useState(false);
  const [cfg, setCfg] = useState(null); // server payload
  const [values, setValues] = useState({}); // key -> token (selected core's catalog options)
  // Graphics selection: a profile id = this game is PINNED to that core/renderer; "" = follow the
  // system default (stored as null, so the game keeps tracking that default if it ever changes).
  // The two are deliberately different states — pinning a game to today's default silently freezes it.
  const [renderProfile, setRenderProfile] = useState("");
  const [notes, setNotes] = useState("");
  const [advanced, setAdvanced] = useState([]); // [{ key, value }] raw escape-hatch rows
  const [tab, setTab] = useState("video"); // active rail item: a category, "advanced", or "notes"
  const [search, setSearch] = useState("");
  // The Reset target: which tier's defaults "Reset to defaults" applies for the selected
  // renderer/core. Not persisted server-side — always opens on Ultra (the live system tuning).
  const [qualityTier, setQualityTier] = useState("ultra");

  // Apply a GET /Config payload. keepUserFields (a profile switch) preserves the current notes + advanced
  // rows (cross-core) and only swaps the option list/values for the newly-selected core.
  const applyConfig = (d, keepUserFields) => {
    setCfg(d);
    setValues((d.options || []).reduce((m, o) => { m[o.key] = o.value; return m; }, {}));
    // Only the initial load sets the selection from the server; a profile switch is already showing the
    // user's pick and a GET made with ?profile= can't tell us what's stored (it echoes the preview).
    if (!keepUserFields) setRenderProfile(d.savedRenderProfile || "");
    const cats = CATEGORY_ORDER.filter((c) => (d.options || []).some((o) => o.category === c));
    const firstCat = cats[0] || "advanced";
    // Keep the current tab across a profile switch when it's still valid; otherwise land on the first category.
    setTab((prev) => (keepUserFields && (prev === "advanced" || prev === "notes" || cats.includes(prev)) ? prev : firstCat));
    if (!keepUserFields) {
      setNotes(d.notes || "");
      setAdvanced(Object.entries(d.advanced || {}).map(([key, value]) => ({ key, value })));
    }
  };

  useEffect(() => {
    let alive = true;
    setLoading(true);
    MovieAPI.getArcadeGameConfig(gameId)
      .then(async (r) => {
        if (!alive) return;
        if (r.status === 403) { message.error("Configuring games is editor-only."); onClose(); return; }
        if (!r.ok) { message.error("Couldn't load this game's config."); onClose(); return; }
        applyConfig(await r.json(), false);
      })
      .catch(() => { if (alive) { message.error("Couldn't load this game's config."); onClose(); } })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
  }, [gameId]); // eslint-disable-line react-hooks/exhaustive-deps

  // Changing the Graphics profile re-fetches THAT profile's core options (PS1 Beetle vs pcsx_rearmed expose
  // different options). Unsaved edits to the previous profile's options are discarded (notes/advanced kept).
  // "" (System default) previews the SYSTEM default profile's options — that is what such a game boots.
  const switchProfile = (id) => {
    setRenderProfile(id);
    setSwitching(true);
    MovieAPI.getArcadeGameConfig(gameId, id || cfg?.defaultProfile || "")
      .then(async (r) => { if (r.ok) applyConfig(await r.json(), true); })
      .catch(() => {})
      .finally(() => setSwitching(false));
  };

  const grouped = useMemo(() => {
    const byCat = {};
    (cfg?.options || []).forEach((o) => { (byCat[o.category] ||= []).push(o); });
    return CATEGORY_ORDER.filter((c) => byCat[c]?.length).map((c) => ({ category: c, options: byCat[c] }));
  }, [cfg]);

  const navItems = useMemo(() => {
    const items = grouped.map((g) => ({ key: g.category, label: CATEGORY_LABEL[g.category] || g.category, count: g.options.length }));
    items.push({ key: "advanced", label: "Advanced", count: advanced.filter((a) => (a.key || "").trim()).length });
    items.push({ key: "notes", label: "Notes", count: notes ? 1 : 0 });
    return items;
  }, [grouped, advanced, notes]);

  const searchResults = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return [];
    return (cfg?.options || []).filter(
      (o) => o.label?.toLowerCase().includes(q) || o.key?.toLowerCase().includes(q) || o.note?.toLowerCase().includes(q),
    );
  }, [search, cfg]);

  const buildBody = () => {
    const coreOptions = { ...values };
    advanced.forEach(({ key, value }) => {
      const k = (key || "").trim();
      if (k) coreOptions[k] = value ?? "";
    });
    return { coreOptions, renderProfile, notes };
  };

  const doSave = (body, okMsg) => {
    setSaving(true);
    MovieAPI.saveArcadeGameConfig(gameId, body)
      .then(async (r) => {
        if (!r.ok) {
          const err = await r.json().catch(() => null);
          message.error(err?.message || "Couldn't save the config.");
          return;
        }
        message.success(okMsg);
        onClose();
      })
      .catch(() => message.error("Couldn't save the config."))
      .finally(() => setSaving(false));
  };

  const save = () => doSave(buildBody(), "Saved — applies the next time this game starts.");

  // Labels for the graphics selection's two states (pinned to a core/renderer vs following the system
  // default). activeProfileLabel is null while renderProfile is "" — that IS the "not pinned" state.
  const defaultProfileLabel = cfg?.profiles?.find((p) => p.id === cfg?.defaultProfile)?.label || null;
  const activeProfileLabel = cfg?.profiles?.find((p) => p.id === renderProfile)?.label || null;

  // Reset applies the picked tier's defaults for the CURRENTLY selected renderer/core (the tier
  // presets are per core/renderer combination, so the renderer choice is kept, not cleared). The
  // server resolves + stores the preset itself; Ultra stores nothing = the live system defaults.
  const tiers = cfg?.qualityTiers?.length ? cfg.qualityTiers : QUALITY_TIERS;
  const tierLabel = tiers.find((t) => t.id === qualityTier)?.label || qualityTier;
  const reset = () => doSave({ qualityTier, renderProfile, notes: "" }, `Reset to ${tierLabel} defaults.`);

  const setAdvRow = (i, patch) => setAdvanced((rows) => rows.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  const addAdvRow = () => setAdvanced((rows) => [...rows, { key: "", value: "" }]);
  const removeAdvRow = (i) => setAdvanced((rows) => rows.filter((_, j) => j !== i));

  const goTab = (key) => { setSearch(""); setTab(key); };

  const renderOption = (o) => (
    <div className="agc-opt" key={o.key}>
      <div className="agc-opt__row">
        <div className="agc-opt__label">{o.label}</div>
        <div className="agc-opt__control">
          {o.isRange ? (
            <Input
              type="number" min={o.rangeMin} max={o.rangeMax}
              value={values[o.key]}
              onChange={(e) => setValues((v) => ({ ...v, [o.key]: e.target.value }))}
            />
          ) : (
            <Select
              value={values[o.key]}
              onChange={(val) => setValues((v) => ({ ...v, [o.key]: val }))}
              options={(o.values || []).map((vv) => ({ value: vv.token, label: vv.label }))}
              classNames={{ popup: { root: "arcade-version-dropdown" } }}
              styles={{ popup: { root: DROPDOWN_STYLE } }}
              popupMatchSelectWidth={false}
              showSearch
              optionFilterProp="label"
            />
          )}
        </div>
      </div>
      {o.note && <div className="agc-opt__desc">{o.note}</div>}
    </div>
  );

  const activeGroup = grouped.find((g) => g.category === tab);

  return (
    <Modal
      open
      onCancel={onClose}
      width={800}
      zIndex={1600}
      // `sheet-modal` = the shared shell (bounded, pinned header/footer); `--themed` gives it the
      // arcade surface its own CSS already writes text for — without it the category rail is
      // white-on-white in dark theme. See Components/SheetModal.css.
      wrapClassName="sheet-modal sheet-modal--themed arcade-config-modal"
      title={<span className="agc-title">⚙ {game.title} <span className="agc-title__sys">— {systemLabel(cfg?.system)}</span></span>}
      footer={[
        <span key="hint" className="agc-foot-hint">Applies the next time the game starts</span>,
        <Tooltip
          key="tier"
          title="What Reset resets to. Ultra = the live system defaults. Max may be experimental; High/Medium/Low step quality down for games that run slow."
        >
          <Select
            className="agc-tier"
            value={qualityTier}
            onChange={setQualityTier}
            disabled={loading || saving}
            options={tiers.map((t) => ({ value: t.id, label: t.label }))}
            classNames={{ popup: { root: "arcade-version-dropdown" } }}
            styles={{ popup: { root: DROPDOWN_STYLE } }}
            popupMatchSelectWidth={false}
          />
        </Tooltip>,
        <Button key="reset" danger onClick={reset} disabled={loading || saving}>Reset to defaults</Button>,
        <Button key="cancel" onClick={onClose} disabled={saving}>Cancel</Button>,
        <Button key="save" type="primary" onClick={save} loading={saving} disabled={loading}>Save</Button>,
      ]}
    >
      {loading ? (
        <div className="agc-loading"><Spin /></div>
      ) : (
        <div className="agc">
          <div className="agc-head">
            {cfg?.profiles?.length > 0 && (
              <div className="agc-renderer">
                <span className="agc-renderer__label">
                  Renderer / core
                  <Tooltip title="What Start room launches for THIS game — the core + renderer. The settings below follow this choice. “System default” means the game follows whatever this system's default is (and keeps following it if that changes); anything else pins this game to that core/renderer for every room.">
                    <span className="agc-info">ⓘ</span>
                  </Tooltip>
                </span>
                <Select
                  value={renderProfile}
                  onChange={switchProfile}
                  loading={switching}
                  options={[
                    // The explicit "no per-game choice" state. It exists because saving used to store the
                    // resolved id, pinning every game to whatever the default was that day with nothing in
                    // the UI saying so.
                    { value: "", label: `System default${defaultProfileLabel ? ` — ${defaultProfileLabel}` : ""}` },
                    ...cfg.profiles.map((p) => ({
                      value: p.id,
                      label: p.id === cfg.defaultProfile ? `${p.label} (the system default)` : p.label,
                    })),
                  ]}
                  classNames={{ popup: { root: "arcade-version-dropdown" } }}
                  styles={{ popup: { root: DROPDOWN_STYLE } }}
                  popupMatchSelectWidth={false}
                />
                <span className="agc-renderer__hint">
                  {renderProfile
                    ? `Pinned for this game — every room boots ${activeProfileLabel || renderProfile}.`
                    : `Not set for this game — it follows the ${systemLabel(cfg?.system)} default${
                        defaultProfileLabel ? ` (${defaultProfileLabel})` : ""
                      }.`}
                </span>
              </div>
            )}
            <Input
              className="agc-search"
              allowClear
              placeholder="Search settings…"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>

          <div className="agc-panes">
            <nav className="agc-rail" aria-label="Setting categories">
              {navItems.map((it) => (
                <button
                  key={it.key}
                  type="button"
                  className={`agc-rail__item${!search && tab === it.key ? " is-active" : ""}`}
                  onClick={() => goTab(it.key)}
                >
                  <span className="agc-rail__label">{it.label}</span>
                  {it.count > 0 && <span className="agc-rail__count">{it.count}</span>}
                </button>
              ))}
            </nav>

            <div className="agc-content">
              {search ? (
                searchResults.length ? (
                  searchResults.map(renderOption)
                ) : (
                  <div className="agc-empty">No settings match “{search}”.</div>
                )
              ) : tab === "advanced" ? (
                <div className="agc-adv">
                  <p className="agc-adv__note">
                    Any libretro core option, by exact key and value. Not validated — a wrong key or value is
                    silently ignored by the emulator. Use this for keys not listed in the categories.
                  </p>
                  {advanced.map((row, i) => (
                    <div className="agc-adv__row" key={i}>
                      <Input placeholder="option_key" value={row.key} onChange={(e) => setAdvRow(i, { key: e.target.value })} />
                      <Input placeholder="value" value={row.value} onChange={(e) => setAdvRow(i, { value: e.target.value })} />
                      <Button onClick={() => removeAdvRow(i)}>✕</Button>
                    </div>
                  ))}
                  <Button size="small" onClick={addAdvRow}>+ Add option</Button>
                </div>
              ) : tab === "notes" ? (
                <div className="agc-notes">
                  <p className="agc-adv__note">A private note on why this game is configured this way. Editors only; never shown to players.</p>
                  <Input.TextArea
                    rows={5}
                    value={notes}
                    maxLength={500}
                    placeholder="e.g. 'Widescreen off — it stretches the HUD.'"
                    onChange={(e) => setNotes(e.target.value)}
                  />
                </div>
              ) : activeGroup ? (
                activeGroup.options.map(renderOption)
              ) : (
                <div className="agc-empty">This core exposes no settings in this category.</div>
              )}
            </div>
          </div>
        </div>
      )}
    </Modal>
  );
}
