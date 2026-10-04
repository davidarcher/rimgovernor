import {useMemo, useState} from 'react';
import {HTTPError} from './http';
import {usePoll} from './usePoll';

// One polled, read-only reading. Hidden means the process does not serve the
// route (404); stale keeps the last good value beside the error that
// replaced it.
export type Reading<T> = {value: T | null; stale: boolean; hidden: boolean; error: string};

export function useReading<T>(active: boolean, fetcher: (signal: AbortSignal) => Promise<T>, intervalMs: number, unavailable: string): Reading<T> {
  const [state, setState] = useState<Reading<T>>({value: null, stale: true, hidden: false, error: ''});
  usePoll(active ? 'on' : null, fetcher, intervalMs,
    value => setState({value, stale: false, hidden: false, error: ''}),
    error => setState(previous => ({value: previous.value, stale: true, hidden: error instanceof HTTPError && error.status === 404, error: error instanceof Error ? error.message : unavailable})));
  return state;
}

// Decodes one shared reading for a panel; a value this panel cannot decode
// is that panel's error, not its siblings'.
export function useDecoded<T, U>(reading: Reading<T>, decode: (value: T) => U): Reading<U> {
  return useMemo(() => {
    if (reading.value === null) return {...reading, value: null};
    try {return {...reading, value: decode(reading.value)};}
    catch (error) {return {value: null, stale: true, hidden: false, error: error instanceof Error ? error.message : 'Invalid reading'};}
  }, [reading, decode]);
}
