import { act, fireEvent, render } from "@testing-library/react";
import CardImage, { RETRY_LIMIT, RETRY_STEP_MS } from "./CardImage";

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
