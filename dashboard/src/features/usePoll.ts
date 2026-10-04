import {useEffect, useRef} from 'react';
import {requestTimeoutMs} from './http';

// One keyed read loop: fetch, report, wait intervalMs, repeat. A null key
// is idle; a new key (a different world, token or view) aborts the loop in
// flight and starts a fresh one, so a late reply never lands under the
// wrong key. The callbacks always see the latest render; only the key
// restarts the loop. The signal handed to the fetcher also covers the
// timeout, so a fetcher may test signal.aborted before writing state.
export function usePoll<T>(key: string | null, fetcher: (signal: AbortSignal) => Promise<T>, intervalMs: number, onValue: (value: T) => void, onError: (error: unknown) => void, timeoutMs = requestTimeoutMs): void {
  const latest = useRef({fetcher, onValue, onError});
  useEffect(() => {latest.current = {fetcher, onValue, onError};});
  useEffect(() => {
    if (key === null) return;
    const controller = new AbortController(); let timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      const signal = AbortSignal.any([controller.signal, AbortSignal.timeout(timeoutMs)]);
      try {const value = await latest.current.fetcher(signal); if (!controller.signal.aborted) latest.current.onValue(value);}
      catch (error) {if (!controller.signal.aborted) latest.current.onError(error);}
      finally {if (!controller.signal.aborted) timer = setTimeout(() => void poll(), intervalMs);}
    };
    void poll(); return () => {controller.abort(); if (timer) clearTimeout(timer);};
  }, [key, intervalMs, timeoutMs]);
}
