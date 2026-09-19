import {useEffect, useState} from 'react';
import {fetchRoutineStatus, RoutineHTTPError, type RoutineStatus} from './routineData';

// One polled reading of /api/routines shared by the panels that show its
// evidence. Hidden means the process has no routine diagnostics (404); stale
// keeps the last good reading beside the error that replaced it.
export type RoutineReading = {value: RoutineStatus | null; stale: boolean; hidden: boolean; error: string};
export function useRoutineStatus(active: boolean): RoutineReading {
  const [state, setState] = useState<RoutineReading>({value: null, stale: true, hidden: false, error: ''});
  useEffect(() => {
    if (!active) return;
    const controller = new AbortController(); let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      try {const value = await fetchRoutineStatus(AbortSignal.any([controller.signal, AbortSignal.timeout(5000)])); if (!stopped) setState({value, stale: false, hidden: false, error: ''});}
      catch (error) {if (!stopped) setState(previous => ({value: previous.value, stale: true, hidden: error instanceof RoutineHTTPError && error.status === 404, error: error instanceof Error ? error.message : 'Routine diagnostics unavailable'}));}
      finally {if (!stopped) timer = setTimeout(() => void poll(), 3000);}
    };
    void poll(); return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, [active]);
  return state;
}
