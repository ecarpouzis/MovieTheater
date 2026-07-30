import React from "react";
import { createRoot } from "react-dom/client";
import "./theme.css";
import "./index.css";
// On-demand antd styles: import only the components the app actually uses instead of the full
// antd/dist/antd.css (~576 KB). Each component's style/css.js pulls antd's shared base PLUS that
// component's CSS and its transitive style dependencies (e.g. popconfirm → button+popover,
// list → empty+grid+pagination+spin), so every used component is fully styled while the ~35 unused
// ones (Table, DatePicker, Tree, Tabs, Drawer, Form, Upload, …) are dropped.
// MAINTENANCE: if you start using a new antd component anywhere in the app, add its style import here
// (`antd/es/<kebab-name>/style/css`) or it will render unstyled.
import "antd/es/alert/style/css";
import "antd/es/auto-complete/style/css";
import "antd/es/button/style/css";
import "antd/es/card/style/css";
import "antd/es/checkbox/style/css";
import "antd/es/collapse/style/css";
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
import "antd/es/tag/style/css";
import "antd/es/tooltip/style/css";
import "antd/es/typography/style/css";
import "antd/es/message/style/css";
// notification (not message): the sticky patched-binary alarm in NavBar/PatchedArtifactAlarm.js.
// Without this the popup renders UNSTYLED — the maintenance note at the top of this block is real.
import "antd/es/notification/style/css";
import App from "./App";

const container = document.getElementById("root");
const root = createRoot(container);
root.render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);

