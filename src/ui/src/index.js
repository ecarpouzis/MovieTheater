import React from "react";
import { createRoot } from "react-dom/client";
import "./index.css";
import "antd/dist/antd.css";
import App from "./App";

// Temporary deploy-verification marker (string survives minification → new bundle hash).
console.log("deploy-check DC20260613");

const container = document.getElementById("root");
const root = createRoot(container);
root.render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);

