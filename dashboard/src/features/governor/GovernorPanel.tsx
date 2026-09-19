import {useRef, useState} from 'react';
import HealthStrip from './HealthStrip';
import EventFeed from './EventFeed';
import TraceView from './TraceView';
import {useTelemetryEvents, useTelemetryMetrics} from './useTelemetry';
import './governor.css';

// traceFromHash is the trace a #governor/<trace_id> link names, or ''.
export function traceFromHash(): string {
  const match = /^#governor\/([^/?]+)$/.exec(location.hash);
  return match ? decodeURIComponent(match[1]) : '';
}

// GovernorPanel is the read-only view of what the governor is doing and
// whether it is healthy (#300): the live metrics block as a health strip,
// the flight-recorder ring as a filterable feed, and one step's trace as a
// waterfall. It shows evidence as the recorder wrote it, not advice.
export default function GovernorPanel({active}: {active: boolean}) {
  const metrics = useTelemetryMetrics(active);
  const events = useTelemetryEvents(active && !metrics.unavailable);
  const [selectedTrace, setSelectedTrace] = useState(traceFromHash);
  const traceView = useRef<HTMLDivElement>(null);
  // The feed is long and sits last; picking a trace brings the waterfall into
  // view and names it in the hash so the link can be shared or reloaded.
  const selectTrace = (traceId: string) => {
    setSelectedTrace(traceId);
    history.replaceState(null, '', `#governor/${encodeURIComponent(traceId)}`);
    const el = traceView.current; if (el && typeof el.scrollIntoView === 'function') el.scrollIntoView({behavior: 'smooth', block: 'start'});
  };
  if (metrics.unavailable) return <section className="observation-panel" aria-label="Governor"><h2>Governor</h2>
    <p role="status">Telemetry is unavailable: {metrics.error || 'the service runs without a flight recorder'}.</p>
    <p className="observation-note">A plain <code>rimgovernor serve</code> records to <code>&lt;profile&gt;/flight/flight.jsonl</code>; <code>--no-flight-recorder</code> and <code>--observe</code> run without one.</p>
  </section>;
  return <>
    <HealthStrip reading={metrics}/>
    <div ref={traceView}><TraceView events={events.events} traceId={selectedTrace} stale={events.stale}/></div>
    <EventFeed reading={events} selectedTrace={selectedTrace} onSelectTrace={selectTrace}/>
  </>;
}
