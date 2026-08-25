import { render, screen } from "@testing-library/react";
import { CardBadge, CardBadges } from "./CardBadge";
import { cardKey, DEFAULT_ASPECT, type CardItem } from "./types";

describe("catalog/CardBadge (the TSX toolchain's first real component)", () => {
  it("renders the label with its tone class and a title fallback", () => {
    render(<CardBadge label="IMDb 7.8" tone="rating" />);
    const el = screen.getByText("IMDb 7.8");
    expect(el).toHaveClass("catalog-badge", "catalog-badge--rating");
    expect(el).toHaveAttribute("title", "IMDb 7.8");
    expect(el).toHaveAttribute("data-tone", "rating");
  });

  it("defaults to the neutral tone and honours an explicit title", () => {
    render(<CardBadge label="#12" title="Issue 12" />);
    const el = screen.getByText("#12");
    expect(el).toHaveClass("catalog-badge--neutral");
    expect(el).toHaveAttribute("title", "Issue 12");
  });

  it("renders nothing for an empty badge list and a list otherwise", () => {
    const { container } = render(<CardBadges badges={[]} />);
    expect(container).toBeEmptyDOMElement();
    render(<CardBadges badges={[{ label: "4K" }, { label: "Want", tone: "want" }]} />);
    expect(screen.getAllByRole("listitem")).toHaveLength(2);
  });

  it("type-checks the CardItem contract (compile-time) and derives the composite key", () => {
    const item: CardItem = {
      kind: "movie",
      id: 42,
      key: cardKey("movie", 42),
      title: "Example",
      aspect: DEFAULT_ASPECT,
      imageUrl: "/ImageThumb/42",
      raw: null,
    };
    expect(item.key).toBe("movie:42");
    expect(item.aspect).toBeCloseTo(0.66);
  });
});
