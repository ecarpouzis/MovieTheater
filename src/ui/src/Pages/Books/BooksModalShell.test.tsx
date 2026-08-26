import { render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import BooksModalShell from "./BooksModalShell";

/**
 * The regression Eric hit on 2026-08-26: the whole screen blurred and the modal sat BEHIND it. The
 * skin tokens rode `wrapProps.style`, which the dialog spreads over its own wrap style — so the wrap
 * lost the inline z-index the mask still had. The wrap must keep its z-index AND wear the skin.
 */
describe("Books/BooksModalShell — the wrap keeps its z-index under the section skin", () => {
  it("renders the wrap at z-index 1500 with the --books-* tokens inline, above the mask", () => {
    render(
      <MemoryRouter initialEntries={["/books?item=1"]}>
        <BooksModalShell open onClose={() => {}} ariaLabel="Item">
          <div>content</div>
        </BooksModalShell>
      </MemoryRouter>,
    );
    const wrap = document.querySelector<HTMLElement>(".books-modal.ant-modal-wrap");
    const mask = document.querySelector<HTMLElement>(".books-modal-root .ant-modal-mask");
    expect(wrap).not.toBeNull();
    expect(mask).not.toBeNull();
    expect(wrap!.style.zIndex).toBe("1500");
    expect(mask!.style.zIndex).toBe("1500");
    // same z-index ⇒ DOM order decides: the wrap must come AFTER the mask
    expect(mask!.compareDocumentPosition(wrap!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(wrap!.style.getPropertyValue("--books-bg")).not.toBe("");
    expect(wrap!.style.getPropertyValue("--books-display")).not.toBe("");
  });
});
