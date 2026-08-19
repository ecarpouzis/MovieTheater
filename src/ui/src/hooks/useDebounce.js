import { useEffect, useMemo, useRef } from "react";

/**
 * Trailing-edge debounce for a callback: the returned function re-arms a timer on every call and
 * runs `fn` once the calls stop for `delayMs`. It is referentially stable for the component's
 * lifetime — safe in an effect's dependency list — and always invokes the LATEST `fn`, so the
 * callback may close over render state without stale reads.
 *
 * `.cancel()` drops a pending run (a search box whose term fell below the minimum length must not
 * let an already-armed request land). Unmount cancels too — a debounced call that fires after
 * teardown would setState on a dead component.
 *
 * Deliberately trailing-only: every site the app actually has (song search, the person typeahead,
 * the ratings autosave) wants "act once the typing stops". A leading-edge variant would be a
 * different contract and belongs here only when something needs it.
 */
export function useDebouncedCallback(fn, delayMs) {
  const fnRef = useRef(fn);
  fnRef.current = fn;
  const timerRef = useRef(null);

  const debounced = useMemo(() => {
    const call = (...args) => {
      clearTimeout(timerRef.current);
      timerRef.current = setTimeout(() => {
        timerRef.current = null;
        fnRef.current(...args);
      }, delayMs);
    };
    call.cancel = () => {
      clearTimeout(timerRef.current);
      timerRef.current = null;
    };
    return call;
  }, [delayMs]);

  useEffect(() => () => debounced.cancel(), [debounced]);

  return debounced;
}

export default useDebouncedCallback;
