import useMediaQuery from "./useMediaQuery";

// The app-shell "is this the phone layout?" switch, aligned with index.css's 768px breakpoint.
// Implemented on matchMedia (via useMediaQuery) rather than a resize listener: consumers re-render
// only when the answer CHANGES, not on every resize event.
function useIsMobile(breakpoint = 768) {
  return useMediaQuery(`(max-width: ${breakpoint}px)`);
}

export default useIsMobile;
