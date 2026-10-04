import {useEffect, useState} from 'react';
import {HTTPError, requestTimeoutMs} from './http';

// One polled, read-only reading. Hidden means the process does not serve the
// route (404); stale keeps the last good value beside the error that
// replaced it.
export type Reading<T> = {value: T | null; stale: boolean; hidden: boolean; error: string};

export function useReading<T>(active: boolean, fetcher: (signal: AbortSignal) => Promise<T>, intervalMs: number, unavailable: string): Reading<T> {
  const [state, setState] = useState<Reading<T>>({value: null, stale: true, hidden: false, error: ''});
  useEffect(() => {
    if (!active) return;
    const controller = new AbortController(); let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      try {const value = await fetcher(AbortSignal.any([controller.signal, AbortSignal.timeout(requestTimeoutMs)])); if (!stopped) setState({value, stale: false, hidden: false, error: ''});}
      catch (error) {if (!stopped) setState(previous => ({value: previous.value, stale: true, hidden: error instanceof HTTPError && error.status === 404, error: error instanceof Error ? error.message : unavailable}));}
      finally {if (!stopped) timer = setTimeout(() => void poll(), intervalMs);}
    };
    void poll(); return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, [active]);
  return state;
}
