# MovieTheater UI

React 18 + Vite + antd 6 single-page app, served by the API's Yarp proxy (dev: `localhost:3000`, proxied to the API on `3001`).

## Scripts

| Command | What it does |
|---|---|
| `npm start` | Vite dev server on port 3000 with the `/API`, `/odata` and image routes proxied to `localhost:3001` (see `vite.config.js` — a new image route needs a proxy line or it falls through to the SPA catch-all) |
| `npm run build` | `tsc --noEmit` (type-check) then `vite build` into `build/` — the same command the Docker UI image runs |
| `npm run typecheck` | Type-check only |
| `npm test` / `npm run test:watch` | Vitest (happy-dom; setup in `src/setupTests.js`) |
| `npm run copy-libass`, `npm run build-butterchurn-presets` | Asset generators; both run automatically before `start`/`build` |

## Languages

- New and ported code is **TypeScript** (`.ts`/`.tsx`), `strict`, under `tsconfig.json`; `@/…` resolves to `src/…`.
- Existing code is JavaScript (`.js` files containing JSX, handled by the `treat-js-files-as-jsx` plugin in `vite.config.js`). It is not type-checked (`checkJs: false`) and is converted only when a file is rewritten for other reasons.
- ESLint: flat config in `eslint.config.mjs` — the React rules for everything, `typescript-eslint` recommended on `.ts`/`.tsx`.

## Where things live

- `src/Components`, `src/hooks`, `src/utils` — the shared primitives (sheet modal shell, load-failure surface, fallback image, cached resources, polling, paged catalog, storage helpers). Use them before writing a spinner, a fetch cache, a poll loop, a modal shell or a storage read.
- `src/catalog` — the section-independent card/group/source contracts and the generic browse views (TypeScript).
- `src/Pages/<Section>` — one folder per vertical; `src/NavBar` — the table-driven navbar (`SECTIONS`) and per-section rails.
- `src/theme.css` — design tokens keyed by `data-theme` (light/dark) and `data-feature` (section) on `<html>`.

The `mt-build` meta tag in `index.html` is the deploy marker: it is served uncached, so fetching `/` tells you which build is live.
