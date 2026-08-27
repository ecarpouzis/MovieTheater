import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import TweaksPanel from "./TweaksPanel";
import { DEFAULT_TWEAKS } from "./useTweaks";

afterEach(cleanup);

function mount(onClose = vi.fn(), extras?: React.ComponentProps<typeof TweaksPanel>["extras"], tweaks = DEFAULT_TWEAKS) {
  const onExtra = vi.fn();
  render(<TweaksPanel view="shelf" tweaks={tweaks} coverScale={1} onCoverScale={() => {}} onChange={() => {}} onExtra={onExtra} extras={extras} onClose={onClose} />);
  return { onClose, onExtra };
}

describe("catalog/TweaksPanel — closing, and per-view extras", () => {
  it("closes on the ✕, on Escape, and on a tap outside (the phone's scrim)", () => {
    const { onClose } = mount();
    fireEvent.click(screen.getByRole("button", { name: "Close tweaks" }));
    fireEvent.keyDown(window, { key: "Escape" });
    fireEvent.click(document.querySelector(".twk-scrim")!);
    expect(onClose).toHaveBeenCalledTimes(3);
  });

  it("a swatch extra draws the Long Box grid — 4 columns, a tick on the chosen one — and a Seg otherwise", () => {
    const extras = [
      {
        key: "backdrop", label: "Backdrop", perView: true, render: "swatch" as const,
        options: [
          { value: "site", label: "Site", color: "var(--content-bg)", family: "any" as const, inactive: false },
          { value: "paper", label: "Paper", color: "#f6f5f1", family: "light" as const, inactive: false },
          { value: "slate", label: "Slate", color: "#1c1f24", family: "dark" as const, inactive: true },
        ],
      },
      { key: "display", label: "Type", options: [{ value: "site", label: "Site" }, { value: "mono", label: "Mono" }] },
    ];
    const { onExtra } = mount(vi.fn(), extras, { ...DEFAULT_TWEAKS, extras: { "backdrop:shelf": "paper" } });
    // `.twk-swatches` IS the 4-column grid (catalog-views.css) — the class is the contract here.
    const grid = document.querySelector(".twk-swatches")!;
    expect(grid.getAttribute("role")).toBe("radiogroup");
    const swatches = grid.querySelectorAll(".twk-swatch");
    expect(swatches).toHaveLength(3);
    expect(swatches[1].getAttribute("aria-checked")).toBe("true");
    expect(swatches[1].querySelector("svg")).toBeTruthy();
    // The out-of-family swatch is dimmed but LIVE (the host answers it with a theme switch).
    expect((swatches[2] as HTMLElement).dataset.inactive).toBe("1");
    fireEvent.click(swatches[2]);
    expect(onExtra).toHaveBeenCalledWith("backdrop:shelf", "slate");
    // The type row is still a Seg — a swatch grid is only for colours.
    expect(document.querySelectorAll(".twk-seg").length).toBeGreaterThan(0);
    expect(screen.getByRole("radio", { name: "Mono" }).closest(".twk-seg")).toBeTruthy();
  });

  it("a page that draws its own cards gets ONLY the rows that reach them", () => {
    render(
      <TweaksPanel
        view="grid" tweaks={DEFAULT_TWEAKS} coverScale={1} onCoverScale={() => {}} onChange={() => {}}
        onExtra={() => {}} onClose={() => {}} rows={{ hover: false, rounded: false, metadata: false }}
        footNote="Remembered on this device for your shelf."
      />,
    );
    expect(screen.getByLabelText("Cover size")).toBeInTheDocument();
    expect(screen.queryByText("Hover")).toBeNull();
    expect(screen.queryByText("Rounded corners")).toBeNull();
    expect(screen.queryByText("Under the cover")).toBeNull();
    expect(screen.getByText("Remembered on this device for your shelf.")).toBeInTheDocument();
  });

  it("a per-view extra reads `key:view` before `key` and writes `key:view`", () => {
    const extras = [{ key: "backdrop", label: "Backdrop", perView: true, options: [{ value: "paper", label: "Paper" }, { value: "bookcase", label: "Bookcase" }] }];
    const { onExtra } = mount(vi.fn(), extras, { ...DEFAULT_TWEAKS, extras: { backdrop: "paper", "backdrop:shelf": "bookcase" } });
    expect(screen.getByRole("radio", { name: "Bookcase" })).toHaveAttribute("aria-checked", "true");
    fireEvent.click(screen.getByRole("radio", { name: "Paper" }));
    expect(onExtra).toHaveBeenCalledWith("backdrop:shelf", "paper");
    expect(screen.getByText("Backdrop (this view)")).toBeInTheDocument();
  });
});
