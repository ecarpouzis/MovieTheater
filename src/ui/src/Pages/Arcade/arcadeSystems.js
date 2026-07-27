// System codes → friendly labels, and which systems the box-art route can source. Shared by the
// browse page and the navbar filter panel, which used to keep divergent copies of the same map.
export const SYSTEM_LABEL = {
  nes: "NES", snes: "SNES", genesis: "Genesis", gb: "Game Boy", gbc: "Game Boy Color",
  gba: "Game Boy Advance", n64: "Nintendo 64", gc: "GameCube", wii: "Wii", ps1: "PlayStation", ps2: "PlayStation 2", arcade: "Arcade",
  psp: "PSP", dc: "Dreamcast", naomi: "Naomi", atomiswave: "Atomiswave", saturn: "Saturn",
  sms: "Master System", gg: "Game Gear", sg1000: "SG-1000", segacd: "Sega CD",
  sega32x: "32X", pce: "TurboGrafx-16", ngpc: "Neo Geo Pocket", wsc: "WonderSwan Color",
  a2600: "Atari 2600", a7800: "Atari 7800", lynx: "Atari Lynx", vb: "Virtual Boy",
  fds: "Famicom Disk System", neogeo: "Neo Geo", "3do": "3DO",
  cdi: "CD-i", coleco: "ColecoVision", intv: "Intellivision", vectrex: "Vectrex",
  o2em: "Odyssey²", channelf: "Channel F", arcadia: "Arcadia 2001",
  pokemini: "Pokémon Mini", supervision: "Supervision", scummvm: "ScummVM",
  nds: "Nintendo DS", "3ds": "Nintendo 3DS",
  // Heavy lane (Moonlight-streamed, docs/arcade-heavy-lane-plan.md §7.1).
  switch: "Switch", ps3: "PlayStation 3", ps4: "PlayStation 4", wiiu: "Wii U", x360: "Xbox 360",
  // Capture lane (H5): a browser room for a heavy title shows system "capture" in its descriptor.
  capture: "Live",
};

export const systemLabel = (s) => SYSTEM_LABEL[s] || (s ? s.toUpperCase() : "");

// Systems the box-art route can source (libretro-thumbnails) — so a card requests /ArcadeImage even
// before its art is cached (the route lazily fetches on first view). Naomi/atomiswave are skipped
// (arcade-named art won't match → don't 404 those cards). Mirror of ArcadeBoxArt.ThumbRepo keys.
export const ART_SYSTEMS = new Set([
  "nes", "snes", "genesis", "gb", "gbc", "gba", "n64", "gc", "wii", "ps1", "ps2",
  "psp", "dc", "sms", "gg", "sg1000", "segacd", "sega32x", "pce", "ngpc", "wsc",
  "a2600", "a7800", "lynx", "vb", "fds", "nds", "3ds",
  // arcade/neogeo now resolve real titles → art via libretro (neogeo) or IGDB cover (arcade).
  "arcade", "neogeo",
]);

/** True when a card should attempt /ArcadeImage rather than going straight to its placeholder. */
export const canHaveArt = (game) => Boolean(game?.hasBoxArt) || ART_SYSTEMS.has(game?.system);

// Heavy/capture lane: a native app streamed by Moonlight, with no libretro core and no CloudRetro
// save path at all (docs/arcade-heavy-lane-plan.md §7.1).
export const HEAVY_LANE_SYSTEMS = new Set(["switch", "ps3", "ps4", "wiiu", "x360", "capture"]);

// Systems with NO emulator save-state: their progress is their own save data, not a serialized
// machine state. psp + ps2 are noSaveStates cores (config.worker-gl.yaml — a t=106 there returns
// ErrNoSaveStates). Keep in step with that file.
//
// scummvm is here for a DIFFERENT reason and is not marked in that config: the core simply cannot
// serialize. Observed live 2026-07-27 —
//   Libretro save on quit failed error="retro_serialize_size returned 0 (core cannot save-state this game)"
// so Save/Load/Continue silently do nothing there. It degraded correctly by accident (no save rows are
// ever written, so the launch modal already fell through to Clean Start), but the in-room Save button
// was still offered and would fail silently. ScummVM's progress is entirely its own in-game save,
// written through ScummVM's savepath.
//
// These have no state to continue from or quickload, so the launch modal collapses to Clean Start
// alone for them — and, happily, they can never be save-scummed either: their own save system is
// legitimate exactly the way a real memory card is.
export const NO_SAVE_STATE_SYSTEMS = new Set(["psp", "ps2", "scummvm", ...HEAVY_LANE_SYSTEMS]);

/** True when a system can offer Continue / Quickload at all (i.e. it has emulator save-states). */
export const hasSaveStates = (system) => !NO_SAVE_STATE_SYSTEMS.has(String(system || "").toLowerCase());

// The fixed quicksave slot (SaveStore.QuickSlot). Deliberately NOT slot 0 — that belongs to the
// autosave / save-on-quit "Continue" state, so pressing Save can never overwrite it.
export const QUICK_SLOT = 99;
