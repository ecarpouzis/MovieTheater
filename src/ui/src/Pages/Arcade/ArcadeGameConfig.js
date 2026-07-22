import { useEffect, useMemo, useState } from "react";
import { Button, Collapse, Input, Modal, Select, Spin, Tooltip, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import { systemLabel } from "./arcadeSystems";
import "./ArcadeGameConfig.css";

/**
 * The per-game emulator/quality config tool (docs/arcade-per-game-config.md), reached from the game
 * modal's ⚙ Configure button. Editor-only. It edits ONE persistent per-game profile (keyed server-side
 * by game identity, not ROM), so what you set here is how the game plays when anyone presses Start —
 * delivered per-room, effective on the next room with no restart.
 *
 * The control set is generated from the server's catalog of what each core actually supports (the
 * "quality modifiers" that used to hide in the Cheats dropdown live here now). Every value is the core's
 * EXACT token — libretro silently ignores an unknown one — so the picker only ever offers real tokens,
 * and the Advanced section (raw key/value) is the editor's own-risk escape hatch.
 *
 * We always submit the FULL current value set; the server stores only what differs from the game's
 * default, so "leave it alone" never pins a redundant value and Reset clears the profile entirely.
 */
// The config Modal sits at zIndex 1600 (above the game modal). antd Select popups default lower, so
// without this they open BEHIND the modal and look empty. Keep it above the modal.
const DROPDOWN_STYLE = { zIndex: 1700 };

const CATEGORY_ORDER = ["video", "hack", "performance", "system", "audio"];
const CATEGORY_LABEL = {
  video: "Video & display",
  hack: "Enhancements — may glitch on some games",
  performance: "Performance",
  system: "System",
  audio: "Audio",
};

export default function ArcadeGameConfig({ game, onClose }) {
  const gameId = game.id;
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [switching, setSwitching] = useState(false);
  const [cfg, setCfg] = useState(null); // server payload
  const [values, setValues] = useState({}); // key -> token (selected core's catalog options)
  const [renderProfile, setRenderProfile] = useState(null); // graphics profile id
  const [notes, setNotes] = useState("");
  const [advanced, setAdvanced] = useState([]); // [{ key, value }] raw escape-hatch rows

  // Apply a GET /Config payload. keepUserFields (a profile switch) preserves the current notes + advanced
  // rows (they're cross-core) and only swaps the option list/values for the newly-selected core.
  const applyConfig = (d, keepUserFields) => {
    setCfg(d);
    setValues((d.options || []).reduce((m, o) => { m[o.key] = o.value; return m; }, {}));
    setRenderProfile(d.renderProfile || null);
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

  // Changing the Graphics profile re-fetches THAT profile's core options — PS1 Beetle vs pcsx_rearmed expose
  // different options. Unsaved edits to the previous profile's options are discarded (notes/advanced are kept).
  const switchProfile = (id) => {
    setRenderProfile(id);
    setSwitching(true);
    MovieAPI.getArcadeGameConfig(gameId, id)
      .then(async (r) => { if (r.ok) applyConfig(await r.json(), true); })
      .catch(() => {})
      .finally(() => setSwitching(false));
  };

  const grouped = useMemo(() => {
    const byCat = {};
    (cfg?.options || []).forEach((o) => { (byCat[o.category] ||= []).push(o); });
    return CATEGORY_ORDER.filter((c) => byCat[c]?.length).map((c) => ({ category: c, options: byCat[c] }));
  }, [cfg]);

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
  const reset = () => doSave({ coreOptions: {}, renderProfile: "", notes: "" }, "Reset to defaults.");

  const setAdvRow = (i, patch) =>
    setAdvanced((rows) => rows.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  const addAdvRow = () => setAdvanced((rows) => [...rows, { key: "", value: "" }]);
  const removeAdvRow = (i) => setAdvanced((rows) => rows.filter((_, j) => j !== i));

  return (
    <Modal
      open
      onCancel={onClose}
      width={640}
      zIndex={1600}
      wrapClassName="arcade-config-modal"
      title={<span className="agc-title">⚙ Configure — {game.title}</span>}
      footer={[
        <Button key="reset" danger onClick={reset} disabled={loading || saving}>Reset to defaults</Button>,
        <Button key="cancel" onClick={onClose} disabled={saving}>Cancel</Button>,
        <Button key="save" type="primary" onClick={save} loading={saving} disabled={loading}>Save</Button>,
      ]}
    >
      {loading ? (
        <div className="agc-loading"><Spin /></div>
      ) : (
        <div className="agc-body">
          <p className="agc-lede">
            How <b>{game.title}</b> ({systemLabel(cfg?.system)}) plays for everyone when a room starts.
            Changes apply to the next room — they don't affect rooms already running.
          </p>

          {cfg?.profiles?.length > 0 && (
            <div className="agc-group">
              <div className="agc-group__title">Graphics</div>
              <label className="agc-field">
                <span className="agc-field__label">
                  Renderer / core
                  <Tooltip title="What Start Room launches for this game — the core + renderer combination. Options below follow this choice. Vulkan is the default for 3D systems; pick an OpenGL profile to use the GL core/renderer.">
                    <span className="agc-info">ⓘ</span>
                  </Tooltip>
                </span>
                <Select
                  className="agc-select"
                  value={renderProfile}
                  onChange={switchProfile}
                  loading={switching}
                  options={cfg.profiles.map((p) => ({ value: p.id, label: p.label }))}
                  popupClassName="arcade-version-dropdown"
                  dropdownStyle={DROPDOWN_STYLE}
                />
              </label>
            </div>
          )}

          {grouped.map(({ category, options }) => (
            <div className="agc-group" key={category}>
              <div className="agc-group__title">{CATEGORY_LABEL[category] || category}</div>
              {options.map((o) => (
                <label className="agc-field" key={o.key}>
                  <span className="agc-field__label">
                    {o.label}
                    {o.note && (
                      <Tooltip title={o.note}><span className="agc-info">ⓘ</span></Tooltip>
                    )}
                  </span>
                  {o.isRange ? (
                    <Input
                      className="agc-select" type="number" min={o.rangeMin} max={o.rangeMax}
                      value={values[o.key]}
                      onChange={(e) => setValues((v) => ({ ...v, [o.key]: e.target.value }))}
                    />
                  ) : (
                    <Select
                      className="agc-select" value={values[o.key]}
                      onChange={(val) => setValues((v) => ({ ...v, [o.key]: val }))}
                      options={(o.values || []).map((vv) => ({ value: vv.token, label: vv.label }))}
                      popupClassName="arcade-version-dropdown"
                      dropdownStyle={DROPDOWN_STYLE}
                    />
                  )}
                </label>
              ))}
            </div>
          ))}

          <Collapse ghost className="agc-advanced">
            <Collapse.Panel
              key="adv"
              header={`Advanced — raw core options${advanced.length ? ` (${advanced.length})` : ""}`}
            >
              <div className="agc-adv-body">
                <p className="agc-adv-note">
                  Any libretro core option, by exact key and value. No validation — a wrong key or value
                  is silently ignored by the emulator. For keys not in the list above.
                </p>
                {advanced.map((row, i) => (
                  <div className="agc-adv-row" key={i}>
                    <Input placeholder="option_key" value={row.key}
                      onChange={(e) => setAdvRow(i, { key: e.target.value })} />
                    <Input placeholder="value" value={row.value}
                      onChange={(e) => setAdvRow(i, { value: e.target.value })} />
                    <Button onClick={() => removeAdvRow(i)}>✕</Button>
                  </div>
                ))}
                <Button size="small" onClick={addAdvRow}>+ Add option</Button>
              </div>
            </Collapse.Panel>
          </Collapse>

          <div className="agc-group">
            <div className="agc-group__title">Notes</div>
            <Input.TextArea rows={2} value={notes} maxLength={500}
              placeholder="Why this config (optional) — e.g. 'widescreen patch off, breaks the HUD'."
              onChange={(e) => setNotes(e.target.value)} />
          </div>
        </div>
      )}
    </Modal>
  );
}
