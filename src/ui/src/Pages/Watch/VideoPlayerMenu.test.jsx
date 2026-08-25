import { render, fireEvent, cleanup } from "@testing-library/react";
import { vi, describe, it, expect, afterEach } from "vitest";

// The settings menu now renders from the shared option model (playerMenuModel.js). This mounts the
// real player and pins the menu DOM the model must keep producing: every quality rung, the audio
// list, the leading subtitle Off entry, the burned-in hint, and the selection marks — the surface
// that used to drift between this menu and the TV player's accordion.

global.IS_REACT_ACT_ENVIRONMENT = true;
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };

// hls.js constructs a real engine on mount; none of that matters to the menu.
vi.mock("hls.js", () => ({ default: class { static isSupported() { return false; } } }));
vi.mock("../../streamEngine", () => ({ createHls: () => null, bandwidthSample: () => null }));
vi.mock("../../streamCapabilities", () => ({ detectStreamCapabilities: () => ({ maxAudioChannels: 2 }) }));

import VideoPlayer, { QUALITY_LADDER } from "./VideoPlayer";

afterEach(cleanup);

const AUDIO = [
  { index: 1, label: "English 5.1", channels: 6 },
  { index: 2, label: "Commentary", channels: 2 },
];
const SUBS = [
  { index: 4, label: "English", deliveryUrl: "/sub.vtt" },
  { index: 7, label: "Signs (PGS)", deliveryUrl: null },
];

function mountWithMenu(extra = {}) {
  const utils = render(
    <VideoPlayer
      src="/stream.m3u8"
      isHls={false}
      audioTracks={AUDIO}
      subtitleTracks={SUBS}
      selectedAudioIndex={1}
      selectedSubtitleIndex={null}
      {...extra}
    />
  );
  fireEvent.click(utils.container.querySelector(".vp-btn-gear"));
  return utils;
}

describe("the Watch settings menu (shared option model)", () => {
  it("lists every quality rung with the active one marked", () => {
    const { container } = mountWithMenu({ qualityKey: "720-4" });
    const items = [...container.querySelectorAll(".vp-menu-item")];
    for (const q of QUALITY_LADDER) {
      expect(items.some((el) => el.textContent.includes(q.label) && el.textContent.includes(q.hint))).toBe(true);
    }
    const on = [...container.querySelectorAll('.vp-menu-item--on[role="menuitemradio"]')];
    expect(on.some((el) => el.textContent.includes("4 Mbps"))).toBe(true);
  });

  it("leads subtitles with Off (selected when nothing is picked) and hints burned-in tracks", () => {
    const { container } = mountWithMenu();
    const items = [...container.querySelectorAll(".vp-menu-item")];
    const off = items.find((el) => el.textContent.trim() === "Off");
    expect(off).toBeTruthy();
    expect(off.className).toContain("vp-menu-item--on");
    const pgs = items.find((el) => el.textContent.includes("Signs (PGS)"));
    expect(pgs.textContent).toContain("burned in");
    const eng = items.find((el) => el.textContent.includes("English") && !el.textContent.includes("5.1"));
    expect(eng.textContent).not.toContain("burned in");
  });

  it("sends the RAW track to onSelectAudio and null to onSelectSubtitle for Off", () => {
    const onSelectAudio = vi.fn();
    const onSelectSubtitle = vi.fn();
    const { container } = mountWithMenu({ onSelectAudio, onSelectSubtitle, selectedSubtitleIndex: 4 });
    const items = [...container.querySelectorAll(".vp-menu-item")];
    fireEvent.click(items.find((el) => el.textContent.includes("Commentary")));
    expect(onSelectAudio).toHaveBeenCalledWith(AUDIO[1]);
    fireEvent.click(container.querySelector(".vp-btn-gear")); // reopen (selecting closed it)
    const items2 = [...container.querySelectorAll(".vp-menu-item")];
    fireEvent.click(items2.find((el) => el.textContent.trim() === "Off"));
    expect(onSelectSubtitle).toHaveBeenCalledWith(null);
  });

  it("explains a missing cast button in a TV readout", () => {
    // No button renders for an unsupported browser (nothing to press), but the menu says why.
    const { container } = mountWithMenu({
      cast: { supported: false, sdkReady: false, state: "unavailable", reason: "unsupported-browser", connected: false },
    });
    expect(container.querySelector(".vp-btn-cast")).toBeNull();
    const readouts = [...container.querySelectorAll(".vp-menu-readout")].map((el) => el.textContent);
    expect(readouts.some((t) => /can't Google Cast/.test(t))).toBe(true);
  });
});
