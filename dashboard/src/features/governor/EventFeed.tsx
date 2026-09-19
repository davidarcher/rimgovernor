import {useMemo, useState} from 'react';
import {messageOf, scalarText, tickOf, toolOf, traceIdOf, type TelemetryEvent} from './telemetryData';
import type {EventsReading} from './useTelemetry';
import {shortId} from './trace';

// Row kinds hidden until asked for: a decode row shadows its response and
// says nothing a reader wants at feed scale.
const hiddenByDefault = new Set(['native_decode']);
export const feedLength = 200;

export function eventSummary(event: TelemetryEvent): string {
  switch (event.kind) {
    case 'native_request': case 'native_response': case 'native_error': case 'native_cache_hit': case 'native_decode': {
      const tool = toolOf(event.payload);
      const error = event.payload.error;
      return (tool || '') + (typeof error === 'string' && error ? ' — ' + error : '');
    }
    case 'recording_gap': return `${event.reason || 'gap'} ${event.before}..${event.after}`;
    default: {
      const msg = messageOf(event);
      const parts: string[] = msg ? [msg] : [];
      for (const key of ['action', 'goal', 'plan', 'reason', 'cause', 'outcome', 'receipt', 'stage', 'elapsed_ms', 'reads', 'window_ticks', 'err']) {
        const value = event.payload[key];
        if (value === undefined || value === null || value === '' || value === false) continue;
        parts.push(`${key}=${scalarText(value)}`);
      }
      return parts.join(' ');
    }
  }
}

export default function EventFeed({reading, selectedTrace, onSelectTrace}: {reading: EventsReading; selectedTrace: string; onSelectTrace: (traceId: string) => void}) {
  const [hidden, setHidden] = useState<Set<string>>(hiddenByDefault);
  const [needle, setNeedle] = useState('');
  const kinds = useMemo(() => {
    const seen = new Map<string, number>();
    for (const event of reading.events) seen.set(event.kind, (seen.get(event.kind) ?? 0) + 1);
    return [...seen.entries()].sort((a, b) => a[0].localeCompare(b[0]));
  }, [reading.events]);
  const lowered = needle.trim().toLowerCase();
  const shown = useMemo(() => {
    const out: TelemetryEvent[] = [];
    for (const event of reading.events) {
      if (hidden.has(event.kind)) continue;
      if (lowered && !event.text.includes(lowered) && !traceIdOf(event).toLowerCase().includes(lowered)) continue;
      out.push(event);
      if (out.length >= feedLength) break;
    }
    return out;
  }, [reading.events, hidden, lowered]);
  const toggle = (kind: string) => setHidden(previous => {const next = new Set(previous); if (next.has(kind)) next.delete(kind); else next.add(kind); return next;});
  const origin = reading.events.length ? reading.events[0].wallTime : 0;
  return <section className="observation-panel governor-feed" aria-label="Governor events">
    <div className="governor-feed-heading"><h2>Events</h2>
      <p className="governor-feed-status" role="status">{reading.stale ? (reading.events.length ? 'Stale — ' : 'Unavailable — ') + (reading.error || 'waiting for telemetry') : `${reading.events.length.toLocaleString()} rows held · newest sequence ${reading.lastSequence.toLocaleString()}${reading.caughtUp ? '' : ' · catching up'}`}</p>
    </div>
    <div className="governor-filters">
      <label className="governor-search">Filter <input type="search" value={needle} placeholder="goal, plan, action or trace id" onChange={event => setNeedle(event.target.value)}/></label>
      <fieldset className="governor-kinds"><legend>Kinds</legend>
        {kinds.map(([kind, count]) => <label key={kind}><input type="checkbox" checked={!hidden.has(kind)} onChange={() => toggle(kind)}/> {kind} <small>{count}</small></label>)}
      </fieldset>
    </div>
    {shown.length === 0 ? <p>{reading.events.length ? 'No rows match the filter.' : 'No rows retained yet.'}</p> : <table className="governor-table"><thead><tr><th scope="col">Seq</th><th scope="col">Age</th><th scope="col">Tick</th><th scope="col">Kind</th><th scope="col">Summary</th><th scope="col">Trace</th></tr></thead>
      <tbody>{shown.map(event => {
        const trace = traceIdOf(event), tick = tickOf(event);
        const key = event.sequence ?? `gap-${event.before}-${event.after}`;
        return <tr key={key} className={trace && trace === selectedTrace ? 'governor-selected' : event.kind === 'native_error' || event.kind === 'recording_gap' ? 'governor-error' : undefined}>
          <td>{event.sequence ?? '—'}</td><td>{origin ? `-${((origin - event.wallTime)).toFixed(1)} s` : ''}</td><td>{tick === null ? '' : tick.toLocaleString()}</td>
          <td><code>{event.kind}</code></td><td className="governor-summary">{eventSummary(event)}</td>
          <td>{trace ? <button type="button" className="governor-trace-button" aria-pressed={trace === selectedTrace} onClick={() => onSelectTrace(trace)} title={trace}>{shortId(trace)}</button> : ''}</td>
        </tr>;
      })}</tbody></table>}
    {shown.length >= feedLength && <p className="observation-note">Showing the newest {feedLength} matching rows; narrow the filter to see older ones.</p>}
  </section>;
}
