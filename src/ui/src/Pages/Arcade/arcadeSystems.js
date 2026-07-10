// System codes → friendly labels, and which systems the box-art route can source. Shared by the
// browse page and the navbar filter panel, which used to keep divergent copies of the same map.
const SYSTEM_LABEL = {
  nes: "NES", snes: "SNES", genesis: "Genesis", gb: "Game Boy", gbc: "Game Boy Color",
  gba: "Game Boy Advance", n64: "Nintendo 64", gc: "GameCube", ps1: "PlayStation", arcade: "Arcade",
  psp: "PSP", dc: "Dreamcast", naomi: "Naomi", atomiswave: "Atomiswave",
  sms: "Master System", gg: "Game Gear", sg1000: "SG-1000", segacd: "Sega CD",
  sega32x: "32X", pce: "TurboGrafx-16", ngpc: "Neo Geo Pocket", wsc: "WonderSwan Color",
  a2600: "Atari 2600", a7800: "Atari 7800", lynx: "Atari Lynx", vb: "Virtual Boy",
  fds: "Famicom Disk System", neogeo: "Neo Geo",
  // Heavy lane (Moonlight-streamed, docs/arcade-heavy-lane-plan.md §7.1).
  switch: "Switch", ps3: "PlayStation 3", ps4: "PlayStation 4", wiiu: "Wii U", x360: "Xbox 360",
};

export const systemLabel = (s) => SYSTEM_LABEL[s] || (s ? s.toUpperCase() : "");

// Systems the box-art route can source (libretro-thumbnails) — so a card requests /ArcadeImage even
// before its art is cached (the route lazily fetches on first view). Naomi/atomiswave are skipped
// (arcade-named art won't match → don't 404 those cards). Mirror of ArcadeBoxArt.ThumbRepo keys.
export const ART_SYSTEMS = new Set([
  "nes", "snes", "genesis", "gb", "gbc", "gba", "n64", "gc", "ps1", "ps2",
  "psp", "dc", "sms", "gg", "sg1000", "segacd", "sega32x", "pce", "ngpc", "wsc",
  "a2600", "a7800", "lynx", "vb", "fds",
  // arcade/neogeo now resolve real titles → art via libretro (neogeo) or IGDB cover (arcade).
  "arcade", "neogeo",
]);

/** True when a card should attempt /ArcadeImage rather than going straight to its placeholder. */
export const canHaveArt = (game) => Boolean(game?.hasBoxArt) || ART_SYSTEMS.has(game?.system);
