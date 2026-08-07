import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { useState } from "react";
import MusicLyricsSettingsButton, { LYRICS_DEFAULTS } from "./MusicLyricsSettings";

// The panel opened fine and then ate every click: touching any control just dismissed it without
// applying anything. These pin the two halves of that — the control must fire, and the panel must
// still be open afterwards — because the failure looked identical to "the button does nothing" and
// hit-testing the CSS said the controls were perfectly reachable.

/** A host that behaves like the real one: the settings live above this component and come back
 *  down as a prop, so every change re-renders it. */
function Host({ onChange }) {
  const [settings, setSettings] = useState(LYRICS_DEFAULTS);
  return (
    <MusicLyricsSettingsButton
      settings={settings}
      onChange={(k, v) => {
        onChange(k, v);
        setSettings((prev) => ({ ...prev, [k]: v }));
      }}
    />
  );
}

function open() {
  fireEvent.click(screen.getByTestId("music-lyrics-settings-toggle"));
  return screen.getByTestId("music-lyrics-settings-panel");
}

describe("MusicLyricsSettingsButton", () => {
  it("opens and closes from its own button", () => {
    render(<Host onChange={() => {}} />);
    open();
    expect(screen.queryByTestId("music-lyrics-settings-panel")).toBeTruthy();
    fireEvent.click(screen.getByTestId("music-lyrics-settings-toggle"));
    expect(screen.queryByTestId("music-lyrics-settings-panel")).toBeNull();
  });

  // The reported bug. A pointerdown inside the panel must not reach the dismiss path.
  it("stays open when a font chip is clicked, and applies it", () => {
    const onChange = vi.fn();
    render(<Host onChange={onChange} />);
    open();
    const chip = screen.getByRole("button", { name: "Serif" });
    fireEvent.pointerDown(chip);
    fireEvent.click(chip);
    expect(onChange).toHaveBeenCalledWith("font", "serif");
    expect(screen.queryByTestId("music-lyrics-settings-panel")).toBeTruthy();
  });

  it("stays open when a checkbox is toggled, and applies it", () => {
    const onChange = vi.fn();
    render(<Host onChange={onChange} />);
    open();
    const box = screen.getByLabelText(/Dark backdrop/);
    fireEvent.pointerDown(box);
    fireEvent.click(box);
    expect(onChange).toHaveBeenCalledWith("scrim", false);
    expect(screen.queryByTestId("music-lyrics-settings-panel")).toBeTruthy();
  });

  it("stays open while the size slider is dragged, and applies it", () => {
    const onChange = vi.fn();
    render(<Host onChange={onChange} />);
    open();
    const slider = screen.getByRole("slider");
    fireEvent.pointerDown(slider);
    fireEvent.change(slider, { target: { value: "1.5" } });
    expect(onChange).toHaveBeenCalledWith("scale", 1.5);
    expect(screen.queryByTestId("music-lyrics-settings-panel")).toBeTruthy();
  });

  it("still closes on a click outside it", () => {
    render(<Host onChange={() => {}} />);
    open();
    fireEvent.pointerDown(document.body);
    expect(screen.queryByTestId("music-lyrics-settings-panel")).toBeNull();
  });
});
