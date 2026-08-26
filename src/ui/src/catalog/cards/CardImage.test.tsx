import { act, fireEvent, render } from "@testing-library/react";
import CardImage, { DEAD_COOLDOWN_MS, RETRY_LIMIT, RETRY_STEP_MS } from "./CardImage";

describe("catalog/CardImage — a failed cover retries with backoff, then becomes the hue placeholder", () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  it("retries RETRY_LIMIT times before giving up, and never mutates the src in place", () => {
    const { container } = render(<CardImage src="/thumb/1.webp" hue={120} />);
    const img = () => container.querySelector("img")!;
    expect(img().getAttribute("src")).toBe("/thumb/1.webp");
    for (let attempt = 1; attempt <= RETRY_LIMIT; attempt += 1) {
      fireEvent.error(img());
      // between the error and the retry the SAME element is still there, untouched
      expect(img().getAttribute("src")).toBe("/thumb/1.webp");
      act(() => { vi.advanceTimersByTime(attempt * RETRY_STEP_MS + 5); });
      expect(img().getAttribute("data-attempt")).toBe(String(attempt));
    }
    fireEvent.error(img());
    expect(img().getAttribute("src")?.startsWith("data:image/svg+xml")).toBe(true);
    expect(img().getAttribute("data-fallback")).toBe("1");
    expect(decodeURIComponent(img().getAttribute("src") ?? "")).toContain("0.18 120");
  });

  it("is dormant, not dead: after the cooldown the placeholder gives way to one fresh round (R9 S0, views-perf #3)", () => {
    const { container } = render(<CardImage src="/thumb/2.webp" hue={30} />);
    const img = () => container.querySelector("img")!;
    for (let attempt = 1; attempt <= RETRY_LIMIT; attempt += 1) {
      fireEvent.error(img());
      act(() => { vi.advanceTimersByTime(attempt * RETRY_STEP_MS + 5); });
    }
    fireEvent.error(img());
    expect(img().getAttribute("data-fallback")).toBe("1");
    // the placeholder is inert — nothing retries inside the cooldown
    act(() => { vi.advanceTimersByTime(DEAD_COOLDOWN_MS - 50); });
    expect(img().getAttribute("data-fallback")).toBe("1");
    // …then the real src is asked for again, with the backoff count reset
    act(() => { vi.advanceTimersByTime(100); });
    expect(img().getAttribute("src")).toBe("/thumb/2.webp");
    expect(img().getAttribute("data-fallback")).toBeNull();
    expect(img().getAttribute("data-attempt")).toBeNull();
  });

  it("a new src starts the count over", () => {
    const { container, rerender } = render(<CardImage src="/a.webp" />);
    fireEvent.error(container.querySelector("img")!);
    act(() => { vi.advanceTimersByTime(RETRY_STEP_MS + 5); });
    expect(container.querySelector("img")!.getAttribute("data-attempt")).toBe("1");
    rerender(<CardImage src="/b.webp" />);
    expect(container.querySelector("img")!.getAttribute("data-attempt")).toBeNull();
    expect(container.querySelector("img")!.getAttribute("src")).toBe("/b.webp");
  });
});
