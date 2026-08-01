// Extends Vitest's expect with jest-dom matchers (e.g. toBeInTheDocument)
import "@testing-library/jest-dom";

// antd 5+/6 injects its component styles at runtime (CSS-in-JS) as <style> tags in <head>.
// happy-dom then evaluates those thousands of rules on EVERY getComputedStyle call — and
// testing-library's role queries and user-event's visibility checks call it per element —
// which turned a single queryByRole into ~11s and timed out whole test files (measured on the
// v4→v6 upgrade, 2026-08-01). Under antd 4 the suite never had any antd CSS in the DOM (vitest
// stubs CSS imports), so those checks only ever saw INLINE styles. Returning the element's
// inline style declaration restores exactly that pre-upgrade semantic, at pre-upgrade speed:
// display/visibility set inline (how antd hides closed popups) are still honored, and no test
// in this suite asserts on stylesheet-derived styles. (Blocking the style-tag insertion
// instead corrupts React — it happens inside useInsertionEffect.)
window.getComputedStyle = (el) => el.style;
