// Polling state for the governor panel: the metrics block sampled on a
// cadence into a bounded series (the health strip's sparklines), and the
// event ring paged by sequence into a bounded, newest-first buffer. Neither
// route follows the tail, so the panel polls at its own cadence and keeps
// the last good reading through a failed refresh.
import {useEffect, useState} from 'react';
import {fetchTelemetryEvents, fetchTelemetryMetrics, TelemetryHTTPError, type TelemetryEvent, type TelemetryMetrics} from './telemetryData';

export const metricsInterval = 2000, eventsInterval = 2000, seriesLength = 150;
// The buffer holds this many rows; the first read starts this far behind
// the ring's newest sequence so a trace picked from the feed has its rows.
export const eventBuffer = 4000, backfill = 2500, pageLimit = 500;

export type Sample = {at: number; tps: number; lastStepMs: number; nativeErrors: number; readsPerStep: number; nativeCalls: number};
export type MetricsReading = {value: TelemetryMetrics | null; series: Sample[]; stale: boolean; unavailable: boolean; error: string};
export type EventsReading = {events: TelemetryEvent[]; lastSequence: number; caughtUp: boolean; stale: boolean; error: string};

const timeout = (controller: AbortController) => AbortSignal.any([controller.signal, AbortSignal.timeout(8000)]);
const describe = (error: unknown, fallback: string) => error instanceof Error ? error.message : fallback;

export function useTelemetryMetrics(active: boolean): MetricsReading {
  const [reading, setReading] = useState<MetricsReading>({value: null, series: [], stale: true, unavailable: false, error: ''});
  useEffect(() => {
    if (!active) return;
    const controller = new AbortController(); let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      try {
        const value = await fetchTelemetryMetrics(timeout(controller));
        if (stopped) return;
        const sample: Sample = {at: Date.now(), tps: value.tps, lastStepMs: value.lastStepMs, nativeErrors: value.metrics.native_errors ?? 0, readsPerStep: value.metrics.reads_per_step_mean ?? 0, nativeCalls: value.metrics.native_calls ?? 0};
        setReading(previous => ({value, series: [...previous.series, sample].slice(-seriesLength), stale: false, unavailable: false, error: ''}));
      } catch (error) {
        if (!stopped) setReading(previous => ({...previous, stale: true, unavailable: error instanceof TelemetryHTTPError && error.status === 404, error: describe(error, 'Telemetry unavailable')}));
      } finally {if (!stopped) timer = setTimeout(() => void poll(), metricsInterval);}
    };
    void poll(); return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, [active]);
  return reading;
}

// mergeEvents adds a page (oldest first) ahead of the buffer (newest
// first), dropping rows already held, and trims the oldest past the cap.
export function mergeEvents(buffer: TelemetryEvent[], page: TelemetryEvent[]): TelemetryEvent[] {
  const held = new Set<number>();
  for (const event of buffer) if (event.sequence !== null) held.add(event.sequence);
  const fresh = page.filter(event => event.sequence === null || !held.has(event.sequence)).reverse();
  return [...fresh, ...buffer].slice(0, eventBuffer);
}

export function useTelemetryEvents(active: boolean): EventsReading {
  const [reading, setReading] = useState<EventsReading>({events: [], lastSequence: 0, caughtUp: false, stale: true, error: ''});
  useEffect(() => {
    if (!active) return;
    const controller = new AbortController(); let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    let since = -1;
    const poll = async () => {
      try {
        if (since < 0) {
          const probe = await fetchTelemetryEvents({since: 0, limit: 1}, timeout(controller));
          since = Math.max(0, probe.lastSequence - backfill);
        }
        const page = await fetchTelemetryEvents({since, limit: pageLimit}, timeout(controller));
        if (stopped) return;
        since = Math.max(since, page.nextSince);
        setReading(previous => ({events: mergeEvents(previous.events, page.events), lastSequence: page.lastSequence, caughtUp: !page.more, stale: false, error: ''}));
        // Drain a backlog before yielding to the poll cadence.
        if (page.more && !stopped) {timer = setTimeout(() => void poll(), 0); return;}
      } catch (error) {
        if (!stopped) setReading(previous => ({...previous, stale: true, error: describe(error, 'Telemetry unavailable')}));
      }
      if (!stopped) timer = setTimeout(() => void poll(), eventsInterval);
    };
    void poll(); return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, [active]);
  return reading;
}
