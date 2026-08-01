import React from "react";
import { createRoot } from "react-dom/client";
import "./theme.css";
import "./index.css";
// On-demand antd styles: import only the components the app actually uses instead of the full
// antd/dist/antd.css (~576 KB). Each component's style/css.js pulls antd's shared base PLUS that
// component's CSS and its transitive style dependencies (e.g. popconfirm → button+popover,
// list → empty+grid+pagination+spin), so every used component is fully styled while the ~35 unused
// ones (DatePicker, Tree, Tabs, Form, Upload, …) are dropped.
// This list going stale is a SILENT failure — the component renders, just unstyled. Table and Drawer sat
// in the "unused" list above for as long as SavesVaultManager.js (the cross-game saves vault) had been
// rendering both, so that panel shipped with no antd styling at all. Found 2026-08-01.
// MAINTENANCE: if you start using a new antd component anywhere in the app, add its style import here
// (`antd/es/<kebab-name>/style/css`) or it will render unstyled.
import "antd/es/alert/style/css";
import "antd/es/auto-complete/style/css";
import "antd/es/button/style/css";
import "antd/es/card/style/css";
import "antd/es/checkbox/style/css";
import "antd/es/collapse/style/css";
import "antd/es/drawer/style/css";
import "antd/es/dropdown/style/css";
import "antd/es/empty/style/css";
import "antd/es/input/style/css";
import "antd/es/input-number/style/css";
import "antd/es/layout/style/css";
import "antd/es/list/style/css";
import "antd/es/menu/style/css";
import "antd/es/modal/style/css";
import "antd/es/pagination/style/css";
import "antd/es/popconfirm/style/css";
import "antd/es/progress/style/css";
import "antd/es/radio/style/css";
import "antd/es/result/style/css";
import "antd/es/select/style/css";
import "antd/es/slider/style/css";
import "antd/es/space/style/css";
import "antd/es/spin/style/css";
import "antd/es/table/style/css";
import "antd/es/tag/style/css";
import "antd/es/tooltip/style/css";
import "antd/es/typography/style/css";
import "antd/es/message/style/css";
// antd `notification` is deliberately NOT imported: its only user was the patched-binary guard's
// "not reporting" toast, which was removed as noise (it fired after every deploy — see
// NavBar/PatchedArtifactAlarm.js). Re-add this line if anything starts calling notification.*,
// or it renders UNSTYLED — the maintenance note at the top of this block is real.

// LAST, on purpose: it overrides the z-index antd's own styles above set on the popup classes, so
// dropdowns/tooltips can't render behind this app's hand-raised dialogs. See the file's header.
import "./antdPopupLayer.css";
import App from "./App";

const container = document.getElementById("root");
const root = createRoot(container);
root.render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);

