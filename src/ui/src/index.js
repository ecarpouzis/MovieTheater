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

// Engine tier for CSS (the Long Box's views-perf law #6e): the family's Firefox runs on SOFTWARE
// WebRender (about:support → Compositing), where every overlapping shadow/texture over scrolling
// content is CPU raster per frame at full resolution. `html.eng-gecko` scopes a paint diet in the
// catalog stylesheets (cheap book shadow, no static overlays over the scrolled opening, cheap hover
// lift) to Firefox only — Chrome keeps the rich look. Feature-detected (MozAppearance), never
// UA-sniffed. The durable cure on those machines is HW WebRender, not CSS.
if ("MozAppearance" in document.documentElement.style) {
  document.documentElement.classList.add("eng-gecko");
}

const container = document.getElementById("root");
const root = createRoot(container);
root.render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <App />
    </QueryClientProvider>
  </React.StrictMode>
);

