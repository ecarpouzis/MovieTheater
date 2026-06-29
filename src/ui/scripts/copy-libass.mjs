// Copies the libass-wasm worker + wasm + legacy worker + fallback font out of node_modules into
// public/libass/, so the SubtitlesOctopus worker can fetch its 2.3 MB wasm sibling at /libass/ (it
// resolves the wasm relative to its own URL — there's no wasmUrl option). Runs from the prestart/prebuild
// npm hooks (and is safe to run anytime). public/libass/ is gitignored — it's regenerated from
// node_modules, never committed. Plain Node fs (no Vite plugin) to stay clear of rolldown-vite plugin
// compatibility. If the package isn't installed it no-ops, so ASS just falls back instead of breaking.
import { mkdirSync, copyFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url)); // src/ui/scripts
const srcDir = join(here, "..", "node_modules", "@jellyfin", "libass-wasm", "dist", "js");
const destDir = join(here, "..", "public", "libass");
const files = [
  "subtitles-octopus-worker.js",
  "subtitles-octopus-worker.wasm",
  "subtitles-octopus-worker-legacy.js",
  "default.woff2",
];

if (!existsSync(join(srcDir, files[0]))) {
  console.warn("[copy-libass] @jellyfin/libass-wasm not installed — skipping (ASS subtitles will fall back).");
  process.exit(0);
}
mkdirSync(destDir, { recursive: true });
for (const f of files) copyFileSync(join(srcDir, f), join(destDir, f));
console.log(`[copy-libass] copied ${files.length} libass asset(s) to public/libass/`);
