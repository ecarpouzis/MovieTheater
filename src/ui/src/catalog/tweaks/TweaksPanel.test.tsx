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

  it("a per-view extra reads `key:view` before `key` and writes `key:view`", () => {
    const extras = [{ key: "backdrop", label: "Backdrop", perView: true, options: [{ value: "paper", label: "Paper" }, { value: "bookcase", label: "Bookcase" }] }];
    const { onExtra } = mount(vi.fn(), extras, { ...DEFAULT_TWEAKS, extras: { backdrop: "paper", "backdrop:shelf": "bookcase" } });
    expect(screen.getByRole("radio", { name: "Bookcase" })).toHaveAttribute("aria-checked", "true");
    fireEvent.click(screen.getByRole("radio", { name: "Paper" }));
    expect(onExtra).toHaveBeenCalledWith("backdrop:shelf", "paper");
    expect(screen.getByText("Backdrop (this view)")).toBeInTheDocument();
  });
});
