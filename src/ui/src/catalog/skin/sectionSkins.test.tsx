import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import CatalogHost from "../CatalogHost";
import { createClientSource } from "../sources/clientSource";
import { storageKeyFor } from "../tweaks/useTweaks";
import { SITE_SECTION_SKINS } from "./sectionSkins";
import { getSectionSkin } from "./skin";
import type { CardItem } from "../types";

/**
 * S5's promise, section by section: every section's ⚙ panel carries the SAME nine-swatch backdrop
 * grid and the SAME type Seg, and a pick lands on the section root as tokens. The "site" swatch is
 * each section's own surface and writes nothing — which is why a default install looks exactly as
 * it did — so the pick under test is always a designed one.
 */
afterEach(cleanup);
beforeEach(() => {
  window.localStorage.clear();
  document.documentElement.removeAttribute("data-theme");
});

const ITEM: CardItem = { kind: "movie", id: 1, key: "movie:1", title: "One", aspect: 0.66, imageUrl: "", raw: {} };

function mount(section: string) {
  const source = createClientSource({
    queryKey: `${section}-skin-test`,
    items: [ITEM],
    sorts: [{ value: "alpha", label: "A–Z" }],
    onOpen: () => {},
  });
  return render(
    <MemoryRouter initialEntries={[`/?view=grid&sort=alpha`]}>
      <CatalogHost section={section} source={source} />
    </MemoryRouter>,
  );
}

const openPanel = () => fireEvent.click(screen.getByRole("button", { name: "Browse tweaks" }));

describe("catalog/skin — every section's ⚙ panel", () => {
  it.each(Object.keys(SITE_SECTION_SKINS))("%s lists nine backdrops, and picking one writes the section root token", (section) => {
    const skin = getSectionSkin(section)!;
    const keys = Object.keys(skin.backdrops);
    expect(keys).toHaveLength(9);
    // Four light + four dark beside the section's own surface — a set for either theme.
    expect(keys.filter((k) => skin.backdrops[k].family === "light")).toHaveLength(4);
    expect(keys.filter((k) => skin.backdrops[k].family === "dark")).toHaveLength(4);

    const { container } = mount(section);
    openPanel();
    const grid = container.querySelector(".twk-swatches")!;
    expect(grid).toBeTruthy();
    const swatches = within(grid as HTMLElement).getAllByRole("radio");
    expect(swatches).toHaveLength(9);
    // The section's own surface is the live one on a fresh device, and it writes no tokens.
    const host = container.querySelector(".bx-host") as HTMLElement;
    expect(host.dataset.catalogSkin).toBe("site");
    expect(host.style.getPropertyValue("--skin-bg")).toBe("");

    // Pick the first LIGHT backdrop (the site is light here, so no theme switch is involved).
    const lightKey = keys.find((k) => skin.backdrops[k].family === "light")!;
    const def = skin.backdrops[lightKey];
    fireEvent.click(within(grid as HTMLElement).getByRole("radio", { name: def.label }));
    expect(host.dataset.catalogSkin).toBe(lightKey);
    expect(host.dataset.skinPaint).toBe("1");
    expect(host.style.getPropertyValue("--skin-bg")).toBe(def.bg);
    expect(host.style.getPropertyValue("--skin-ink")).toBe(def.ink);
    // Remembered per view, on this device, in the catalog's own store.
    expect(JSON.parse(window.localStorage.getItem(storageKeyFor(section))!).extras["backdrop:grid"]).toBe(lightKey);
  });

  it("the type Seg is the site's own faces, and picking one repoints the display font", () => {
    const { container } = mount("movies");
    openPanel();
    expect(screen.getByText("Type")).toBeInTheDocument();
    const segs = container.querySelectorAll(".twk-seg");
    const typeSeg = segs[segs.length - 1] as HTMLElement;
    fireEvent.click(within(typeSeg).getByRole("radio", { name: "Mono" }));
    const host = container.querySelector(".bx-host") as HTMLElement;
    expect(host.style.getPropertyValue("--skin-display")).toContain("ui-monospace");
    fireEvent.click(within(typeSeg).getByRole("radio", { name: "Site" }));
    expect(host.style.getPropertyValue("--skin-display")).toBe("");
  });

  it("a dark swatch picked in the light theme asks the site to switch, so no swatch is inert", () => {
    const seen: string[] = [];
    const onRequest = (e: Event) => seen.push((e as CustomEvent).detail);
    window.addEventListener("site:theme", onRequest);
    const { container } = mount("music");
    openPanel();
    const grid = container.querySelector(".twk-swatches") as HTMLElement;
    const dark = within(grid).getByRole("radio", { name: /Vinyl/ });
    expect(dark.dataset.inactive).toBe("1");
    fireEvent.click(dark);
    expect(seen).toEqual(["dark"]);
    window.removeEventListener("site:theme", onRequest);
  });
});
