import { act, fireEvent, render } from "@testing-library/react";
import { DEAD_COOLDOWN_MS, RETRY_LIMIT, RETRY_STEP_MS } from "../catalog/cards/CardImage";
import FallbackImage from "./FallbackImage";

describe("Components/FallbackImage — the catalog's image-failure law, opt-in for a section card", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("without `retry` a failure shows the fallback at once (a lone image in a modal)", () => {
    const { container, getByTestId } = render(<FallbackImage src="/x.jpg" alt="" fallback={<i data-testid="fb" />} />);
    fireEvent.error(container.querySelector("img"));
    expect(getByTestId("fb")).toBeInTheDocument();
  });

  it("with `retry` it re-asks with backoff, goes dormant after RETRY_LIMIT, and tries once more after the cooldown", () => {
    const onError = vi.fn();
    const { container, queryByTestId } = render(<FallbackImage src="/x.jpg" alt="" retry onError={onError} fallback={<i data-testid="fb" />} />);
    for (let attempt = 0; attempt < RETRY_LIMIT; attempt += 1) {
      fireEvent.error(container.querySelector("img"));
      expect(queryByTestId("fb")).toBeNull(); // still an <img>, still asking
      act(() => { vi.advanceTimersByTime((attempt + 1) * RETRY_STEP_MS + 1); });
      expect(container.querySelector("img")).not.toBeNull();
    }
    fireEvent.error(container.querySelector("img"));
    expect(queryByTestId("fb")).not.toBeNull(); // dormant
    expect(onError).toHaveBeenCalledTimes(RETRY_LIMIT + 1); // the caller heard every failure
    act(() => { vi.advanceTimersByTime(DEAD_COOLDOWN_MS + 1); });
    expect(queryByTestId("fb")).toBeNull(); // one fresh round
    expect(container.querySelector("img")).not.toBeNull();
  });
});
