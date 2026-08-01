# Ant Design 4 → 5: upgrade assessment

Measured against the tree on 2026-08-01 (antd `^4.24.16`, React `^18.3.1`, Vite 8).
Written after two v4-only bugs surfaced from a Steam Deck session on the same day.

**Verdict: worth doing, and the API surface is nearly free — but budget the time for a visual
regression pass, not for code changes. Not urgent: both bugs that motivated it are already fixed.**

## Why consider it at all — two whole bug CLASSES disappear

Both of these are v4 design gaps, not mistakes in our code, and both have now bitten:

1. **Popups render behind dialogs.** antd 4 has no automatic z-index management (it landed in antd
   **5.13**). Popups portal to `document.body` with a fixed class z-index — `.ant-select-dropdown`
   and `.ant-dropdown` 1050, `.ant-tooltip` 1070 — while our dialogs are hand-raised to 1400–1600 to
   clear the fixed nav bar. Every popup inside a dialog was therefore behind it. Patched three times:
   twice per-popup (`GameModal.css`, `ArcadeGameConfig.js`), then finally as a layer
   (`src/ui/src/antdPopupLayer.css`). On v5 that file becomes unnecessary.
2. **A component used without its style import renders unstyled, silently.** v4's on-demand
   `antd/es/<name>/style/css` imports are a hand-maintained list in `index.js`; forget one and the
   component renders bare with no error. `Table` and `Drawer` were missing for as long as
   `SavesVaultManager.js` had been rendering both. v5 is CSS-in-JS — the list, and the failure mode,
   cease to exist.

## The migration surface is unusually small here

Measured, not estimated:

| v4 → v5 breaking change | In this codebase |
|---|---|
| `visible` → `open` (Modal/Drawer/Tooltip/Popover/Dropdown) | **0** — already on `open` |
| `dropdownClassName` → `popupClassName` | **0** — already migrated |
| `Dropdown overlay=` → `menu=` | **0** |
| `Table filterDropdownVisible` → `…Open` | **0** |
| Removed components (`PageHeader`, `Comment`) | **0** |
| `moment` → `dayjs` | **0** — no date components, neither library is a dependency |
| Less vars / `@primary-color` / `modifyVars` | **0** — theming is CSS custom properties in `theme.css`, not Less |
| React ≥ 16.9 | ✓ React 18.3 |
| `@ant-design/icons` major bump | not a dependency (the UI uses emoji) |

Mandatory mechanical work:

- **Delete the 28 `antd/es/*/style/css` imports** from `index.js` (no such files in v5), plus the
  maintenance comment they exist for.
- Optionally delete `antdPopupLayer.css` once on ≥5.13 — harmless to keep, and keeping it through the
  upgrade is the safer order.

Soft/deferrable:

- **204 static `message.*` calls + 5 `Modal.confirm`.** These still work in v5; they just don't read
  `ConfigProvider` context and v5 logs a warning. Only matters if we later adopt token theming, and
  the fix is wrapping the tree in `<App>`. Not upgrade-blocking.
- 3 `dropdownStyle` and 4 `bordered` props — deprecated in later v5, still functional.

## The real cost: 445 hand-written `.ant-*` overrides across 18 CSS files

This is the whole risk, and it is not an API problem:

```
33 .ant-modal-wrap    24 .ant-btn      21 .ant-select-item-option-disabled
20 .ant-input         18 .ant-select-selector    15 .ant-modal-content …
```

v5 keeps the `.ant-*` class names, so these selectors mostly keep matching. What changes underneath
them is **specificity and defaults** — CSS-in-JS emits its own rules with different weight, and v5's
design language moves border radii, spacing, control heights, colours and motion. So the failure mode
is not "it breaks", it's "it shifts", quietly, in 18 files' worth of places — including load-bearing
ones like `ArcadeModal.css`'s sheet/card shell, which exists specifically to keep a dialog's footer
reachable on a TV browser.

Budget: a day, most of it walking the app in both themes on a wide and a narrow viewport. There is no
test coverage for visual layout (the 212 Vitest tests are logic), so this has to be eyes.

## Recommendation

Do it, as its own deliberate branch, when there's appetite for a visual pass — not folded into
another change. Suggested order:

1. Bump antd, delete the 28 style imports, get it compiling. (Small.)
2. Walk every dialog-bearing surface: arcade lobby + game modal + room Controllers panel + ⚙ Configure
   + RetroAchievements panel + saves vault Drawer, then the movie/TV/browse pages. Both themes.
3. Fix override drift in the CSS, file by file.
4. Only then consider `<App>` + token theming, and dropping `antdPopupLayer.css`.

Deliberately **not** urgent: the popup layer and the missing style imports are both fixed on v4
today, so this buys prevention and maintenance relief, not a live fix.
