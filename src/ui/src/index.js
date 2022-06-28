import React from "react";
import ReactDOM from "react-dom";
import "./index.css";
import "antd/dist/antd.css";
import App from "./App";

if (window.location.href === "http://localhost:3000/") {
  window.location.href = "http://localhost:3001";
} else {
  ReactDOM.render(
    <React.StrictMode>
      <App />
    </React.StrictMode>,
    document.getElementById("root")
  );
}
