import { describe, it, expect, vi, afterEach } from "vitest";
import { render, fireEvent, cleanup } from "@testing-library/react";
import TouchPresetPicker, { type TouchPresetPickerProps } from "./TouchPresetPicker";
import { presetsFor, suggestedPreset } from "./touchPresets";
import { CUSTOM_PICK } from "./touchLayout";

afterEach(cleanup);

function setup(over: Partial<TouchPresetPickerProps> = {}) {
  const onPick = vi.fn();
  const ctx = { controls: "joy4/1" };
  const utils = render(
    <TouchPresetPicker
      presets={presetsFor("arcade", ctx)}
      suggestedId={suggestedPreset("arcade", ctx)}
      resolved={{ presetId: "joy4", source: "default", pickScope: null, hasGameLayout: false, hasSystemLayout: false }}
      systemName="Arcade"
      canPickForGame
      defaultScope="game"
      onPick={onPick}
      {...over}
    />,
  );
  const chip = (name: RegExp) => utils.getAllByRole("radio").find((el) => name.test(el.textContent || ""))!;
  return { ...utils, onPick, chip };
}

describe("TouchPresetPicker", () => {
  it("marks the preset on screen and stars the suggestion", () => {
    const { chip } = setup();
    expect(chip(/4-way stick/).getAttribute("aria-checked")).toBe("true");
    expect(chip(/4-way stick/).textContent).toContain("★");
    expect(chip(/^1 button/).getAttribute("aria-checked")).toBe("false");
  });

  it("picks for this game by default on the arcade, and for the system when switched", () => {
    const { chip, onPick, getByText } = setup();
    fireEvent.click(chip(/Trackball/));
    expect(onPick).toHaveBeenLastCalledWith("trackball", "game");
    fireEvent.click(getByText("All Arcade games"));
    fireEvent.click(chip(/^6 buttons/));
    expect(onPick).toHaveBeenLastCalledWith("b6", "system");
  });

  it("offers 'My layout' only when an edited layout exists, and shows it selected when it's on screen", () => {
    expect(setup().queryByText(/My layout/)).toBeNull();
    cleanup();
    const { chip, onPick } = setup({ resolved: { presetId: null, source: "system", pickScope: null, hasGameLayout: false, hasSystemLayout: true } });
    expect(chip(/My layout/).getAttribute("aria-checked")).toBe("true");
    expect(chip(/4-way stick/).getAttribute("aria-checked")).toBe("false");
    fireEvent.click(chip(/My layout/));
    expect(onPick).toHaveBeenLastCalledWith(CUSTOM_PICK, "game");
  });

  it("without a game key, only the system scope exists", () => {
    const { queryByText, chip, onPick } = setup({ canPickForGame: false, defaultScope: "system" });
    expect(queryByText("This game")).toBeNull();
    fireEvent.click(chip(/^2 buttons/));
    expect(onPick).toHaveBeenLastCalledWith("b2", "system");
  });

  it("draws nothing for a system with a single preset and no edits", () => {
    const { container } = setup({ presets: presetsFor("nes"), suggestedId: "standard", resolved: null });
    expect(container.innerHTML).toBe("");
  });
});
