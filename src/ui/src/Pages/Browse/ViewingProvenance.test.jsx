import { render, screen } from "@testing-library/react";
import ViewingProvenance, { formatMarkDate } from "./ViewingProvenance";
import { forUserOf, isOwnLists } from "../../hooks/useUserLists";

const AUG3 = "2026-08-03T10:00:00Z";
const JUL12 = "2026-07-12T10:00:00Z";

describe("Pages/Browse/ViewingProvenance — the who/when lines under the pills", () => {
  it("on my own lists: a Want a friend placed is a suggestion with Not interested; a friend's Seen reads as on my behalf; the others line", () => {
    const detail = {
      seen: { atUtc: JUL12, byUserId: 7, byUsername: "Jamie" },
      want: { atUtc: AUG3, byUserId: 8, byUsername: "Bob" },
      others: [
        { userId: 2, username: "Alex", seen: true, want: false },
        { userId: 7, username: "Jamie", seen: true, want: false },
        { userId: 9, username: "Dana", seen: false, want: true },
        { userId: 1, username: "Eric", seen: true, want: true },
      ],
    };
    const onDismiss = vi.fn();
    const { container } = render(<ViewingProvenance detail={detail} scope={{ me: true }} viewer="Eric" onDismiss={onDismiss} />);
    const lines = Array.from(container.querySelectorAll(".prov-line")).map((n) => n.textContent);
    expect(lines[0]).toContain("On your list · suggested by Bob");
    expect(lines[0]).toContain(formatMarkDate(AUG3));
    expect(lines[1]).toContain("marked by Jamie on your behalf");
    expect(lines[2]).toContain("Alex and Jamie have seen it");
    expect(lines[2]).toContain("Dana wants to watch it");
    expect(lines[2]).not.toContain("Eric");
    screen.getByText("Not interested").click();
    expect(onDismiss).toHaveBeenCalled();
  });

  it("my own Want offers no dismissal, and a legacy date reads 'before Sep 2026'", () => {
    const detail = { seen: { atUtc: null, byUserId: null, byUsername: null }, want: { atUtc: AUG3, byUserId: 1, byUsername: "Eric" }, others: [] };
    const { container } = render(<ViewingProvenance detail={detail} scope={{ me: true }} viewer="Eric" onDismiss={vi.fn()} />);
    const lines = Array.from(container.querySelectorAll(".prov-line")).map((n) => n.textContent);
    expect(lines[0]).toContain("On your list since");
    expect(lines[1]).toContain("before Sep 2026");
    expect(container.querySelector(".prov-act")).toBeNull();
  });

  it("on a friend's lists: my placement reads 'you suggested it', theirs by name", () => {
    const detail = {
      seen: { atUtc: JUL12, byUserId: 1, byUsername: "Eric" },
      want: { atUtc: AUG3, byUserId: 1, byUsername: "Eric" },
      others: [{ userId: 7, username: "Jamie", seen: true, want: false }],
    };
    const { container } = render(<ViewingProvenance detail={detail} scope={{ me: false, username: "Alex", forUser: "Alex" }} viewer="Eric" />);
    const lines = Array.from(container.querySelectorAll(".prov-line")).map((n) => n.textContent);
    expect(lines[0]).toContain("Alex wants to watch it · you suggested it");
    expect(lines[1]).toContain("Alex has seen it · marked by you");
    expect(lines[2]).toContain("Jamie has seen it");
    expect(container.querySelector(".prov-act")).toBeNull();
  });

  it("draws nothing for a title with no marks", () => {
    const { container } = render(<ViewingProvenance detail={{ seen: null, want: null, others: [] }} scope={{ me: true }} viewer="Eric" />);
    expect(container.firstChild).toBeNull();
  });

  it("the `for=` scope helpers", () => {
    expect(forUserOf("?for=Alex&my=seen")).toBe("Alex");
    expect(forUserOf("?my=seen")).toBeNull();
    expect(isOwnLists(null, { username: "Eric" })).toBe(true);
    expect(isOwnLists("eric", { username: "Eric" })).toBe(true);
    expect(isOwnLists("Alex", { username: "Eric" })).toBe(false);
    expect(isOwnLists("Alex", null)).toBe(false);
  });
});
