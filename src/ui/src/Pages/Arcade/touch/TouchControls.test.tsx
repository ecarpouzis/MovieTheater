import { describe, it, expect, vi, afterEach } from "vitest";
import { render, fireEvent, cleanup } from "@testing-library/react";
import { PAD } from "../cloudRetroClient";
import TouchControls from "./TouchControls";
import type { Layout } from "./touchLayout";

// jsdom reports a zero rect for everything, so a pointer's clientX/Y IS its layer-relative position.
const size = { w: 1000, h: 500 };
const bit = (n: keyof typeof PAD) => 1 << PAD[n];

const layout: Layout = {
  v: 1, opacity: 1, idleFade: null, haptics: false, flashOnPress: false, slide: true,
  controls: [
    { id: "a", kind: "button", bits: ["A"], shape: "circle", mode: "press", x: 0.9, y: 0.5, s: 0.2 },
    { id: "b", kind: "button", bits: ["B"], shape: "circle", mode: "press", x: 0.78, y: 0.5, s: 0.2 },
    { id: "ls", kind: "stick", output: "left", floating: false, deadzone: 0, x: 0.2, y: 0.5, s: 0.4 },
    { id: "rw", kind: "action", action: "rewind", x: 0.5, y: 0.1, s: 0.1 },
    { id: "rs", kind: "action", action: "reset", x: 0.6, y: 0.1, s: 0.1 },
  ],
};

function setup(over: Partial<Parameters<typeof TouchControls>[0]> = {}) {
  const onFrame = vi.fn();
  const onAction = vi.fn();
  const utils = render(
    <TouchControls layout={layout} size={size} onFrame={onFrame} onAction={onAction} actionAllowed={() => true} {...over} />,
  );
  const ctl = (id: string) => {
    const el = utils.container.querySelector<HTMLElement>(`[data-control-id="${id}"]`);
    if (!el) throw new Error(`no control ${id}`);
    return el;
  };
  const lastFrame = () => onFrame.mock.calls.at(-1) as [number, number[]];
  return { ...utils, onFrame, onAction, ctl, lastFrame };
}

afterEach(cleanup);

describe("TouchControls", () => {
  it("holds two buttons at once and releases each independently", () => {
    const { ctl, lastFrame } = setup();
    fireEvent.pointerDown(ctl("a"), { pointerId: 1, clientX: 900, clientY: 250 });
    fireEvent.pointerDown(ctl("b"), { pointerId: 2, clientX: 780, clientY: 250 });
    expect(lastFrame()[0]).toBe(bit("A") | bit("B"));
    fireEvent.pointerUp(ctl("a"), { pointerId: 1 });
    expect(lastFrame()[0]).toBe(bit("B"));
    fireEvent.pointerCancel(ctl("b"), { pointerId: 2 });
    expect(lastFrame()[0]).toBe(0);
  });

  it("slides a thumb from one face button onto the next", () => {
    const { ctl, lastFrame } = setup();
    fireEvent.pointerDown(ctl("a"), { pointerId: 1, clientX: 900, clientY: 250 });
    fireEvent.pointerMove(ctl("a"), { pointerId: 1, clientX: 770, clientY: 250 });
    expect(lastFrame()[0]).toBe(bit("B"));
  });

  it("deflects the stick toward the thumb", () => {
    const { ctl, lastFrame } = setup();
    fireEvent.pointerDown(ctl("ls"), { pointerId: 3, clientX: 200, clientY: 250 });
    fireEvent.pointerMove(ctl("ls"), { pointerId: 3, clientX: 200, clientY: 50 });
    expect(lastFrame()[1]).toEqual([0, -32767, 0, 0]);
  });

  it("sends hold actions on press AND release, and presses no bits for them", () => {
    const { ctl, onAction, lastFrame } = setup();
    fireEvent.pointerDown(ctl("rw"), { pointerId: 4, clientX: 500, clientY: 50 });
    fireEvent.pointerUp(ctl("rw"), { pointerId: 4 });
    expect(onAction.mock.calls).toEqual([["rewind", true], ["rewind", false]]);
    expect(lastFrame()[0]).toBe(0);
  });

  it("hides actions the room's gate refuses", () => {
    const { container } = setup({ actionAllowed: (a) => a !== "reset" });
    expect(container.querySelectorAll(".tc--action")).toHaveLength(1);
  });

  it("releases everything when the window loses focus, and on unmount", () => {
    const { ctl, lastFrame, unmount, onFrame } = setup();
    fireEvent.pointerDown(ctl("a"), { pointerId: 1, clientX: 900, clientY: 250 });
    window.dispatchEvent(new Event("blur"));
    expect(lastFrame()[0]).toBe(0);
    fireEvent.pointerDown(ctl("b"), { pointerId: 2, clientX: 780, clientY: 250 });
    unmount();
    expect(onFrame.mock.calls.at(-1)).toEqual([0, [0, 0, 0, 0]]);
  });

  it("marks every control so the room's chrome-reveal ignores presses on it", () => {
    const { container } = setup();
    for (const el of container.querySelectorAll(".tc")) expect(el.hasAttribute("data-touch-control")).toBe(true);
  });
});
