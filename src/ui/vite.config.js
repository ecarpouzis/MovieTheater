import { execFileSync } from "node:child_process";
import { defineConfig, transformWithEsbuild } from "vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath } from "node:url";

/**
 * What build is this? CI passes MT_BUILD (the commit SHA); a local build asks git; a build with
 * neither (the Docker image builds from a copy of src/ui with no .git) says so rather than lying.
 * Appended with the build's UTC minute so re-running the SAME commit still produces a new marker —
 * "did the rollout happen" must be answerable even when nothing in the tree changed.
 */
function buildId() {
  const stamp = new Date().toISOString().slice(0, 16).replace(/[-:]/g, "").replace("T", "-");
  const sha = process.env.MT_BUILD?.trim()
    || (() => {
      try { return `${execFileSync("git", ["rev-parse", "HEAD"], { encoding: "utf8" }).trim()}-local`; }
      catch { return "unknown"; }
    })();
  return `${sha} ${stamp}`;
}

export default defineConfig({
  plugins: [
    {
      // Runs `pre` so the placeholder is gone before Vite's own %VITE_*% html-env pass sees it.
      name: "mt-build-marker",
      transformIndexHtml: { order: "pre", handler: (html) => html.replace("%MT_BUILD%", buildId()) },
    },
    {
      name: "treat-js-files-as-jsx",
      enforce: "pre",
      async transform(code, id) {
        if (!id.match(/src\/.*\.js$/) || id.includes("node_modules")) return null;
        return transformWithEsbuild(code, id, { loader: "jsx", jsx: "automatic" });
      },
    },
    react(),
  ],

  // `@/...` = src/... for the TypeScript code (mirrors tsconfig `paths`); existing JS keeps relative imports.
  resolve: {
    alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) },
  },

  optimizeDeps: {
    esbuildOptions: {
      loader: {
        ".js": "jsx",
      },
    },
  },

  server: {
    port: 3000,
    proxy: {
      "/API": "http://localhost:3001",
      "/odata": "http://localhost:3001",
      "/Image": "http://localhost:3001",
      "/ImageThumb": "http://localhost:3001",
      "/SeriesImage": "http://localhost:3001",
      "/SeriesImageThumb": "http://localhost:3001",
      "/MiscImage": "http://localhost:3001",
      "/MiscImageThumb": "http://localhost:3001",
      "/BoardgameImage": "http://localhost:3001",
      "/BoardgameImageThumb": "http://localhost:3001",
      // Arcade box art. Without this the /arcade grid shows placeholders in dev, since the covers
      // fall through to vite's SPA catch-all instead of the lazily-caching image route.
      "/ArcadeImage": "http://localhost:3001",
      // Album art (music-plan.md §2.5) — same bite as /ArcadeImage above: without these entries the
      // /music grid's covers fall through to vite's SPA catch-all and 404 in dev.
      "/MusicImage": "http://localhost:3001",
      "/MusicImageThumb": "http://localhost:3001",
    },
  },

  build: {
    outDir: "build",
  },

  test: {
    globals: true,
    environment: "happy-dom",
    setupFiles: ["./src/setupTests.js"],
  },
});
