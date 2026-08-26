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
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import App from "./App";

// ONE React Query client for the whole app (plan D c12): the Books section is its first consumer
// (modals, shelf, Explore, kids, admin); the catalog package stays query-free and every other page
// keeps its hooks. Defaults mirror the standalone site's: minutes of staleness, one retry, and no
// refetch on window focus (a tab switch must not refetch a reader's position).
const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 5 * 60 * 1000, retry: 1, refetchOnWindowFocus: false } },
});

const container = document.getElementById("root");
const root = createRoot(container);
root.render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <App />
    </QueryClientProvider>
  </React.StrictMode>
);

