import React from "react";
import ReactDOM from "react-dom";
import "./index.css";
import "antd/dist/antd.css";
import App from "./App";

import client from "./Apollo";
import { ApolloProvider } from "@apollo/client";

if (window.location.href === "http://localhost:3000/") {
  window.location.href = "http://localhost:3001";
} else {
  ReactDOM.render(
    <React.StrictMode>
      <ApolloProvider client={client}>
        <App />
      </ApolloProvider>
    </React.StrictMode>,
    document.getElementById("root")
  );
}
