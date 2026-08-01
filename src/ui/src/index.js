import React from "react";
import { createRoot } from "react-dom/client";
import "./theme.css";
import "./index.css";
// antd 5+/6 injects component styles via CSS-in-JS at render time — there are no CSS files to
// import per component (the v4-era `antd/es/<name>/style/css` list that used to live here, and its
// went-stale-silently failure mode, are gone for good). Only used components pay any style cost.

// LAST, on purpose: it overrides the z-index antd sets on the popup classes, so
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

