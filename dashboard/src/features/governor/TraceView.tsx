import {useMemo} from 'react';
import type {TelemetryEvent} from './telemetryData';
import {buildTrace, type Phases, type TraceLine} from './trace';
import {formatNumber} from './HealthStrip';

// Bar splits a native call's duration into its phases: the bridge gate
// wait, the GABS round trip (with the companion's queue/execute split
// inside it when echoed) and decoding. Widths are shares of the trace span.
function Bar({line, spanMs}: {line: TraceLine; spanMs: number}) {
  const scale = spanMs > 0 ? 100 / spanMs : 0;
  const left = line.offsetMs * scale;
  if (line.durationMs === null || line.phases === null) return <div className="governor-bar-track"><span className="governor-mark" style={{left: `${Math.min(left, 100)}%`}} title={`${formatNumber(line.offsetMs)} ms`}/></div>;
  const p: Phases = line.phases;
  const other = Math.max(0, p.callMs - (p.nativeQueueMs ?? 0) - (p.nativeExecuteMs ?? 0));
  const segments: [string, number, string][] = [['gate', p.gateWaitMs, 'gate wait'], ['queue', p.nativeQueueMs ?? 0, 'native queue'], ['exec', p.nativeExecuteMs ?? 0, 'native execute'], ['call', other, p.nativeQueueMs === null ? 'call' : 'transport'], ['decode', p.decodeMs, 'decode']];
  const total = Math.max(line.durationMs, segments.reduce((sum, [, ms]) => sum + ms, 0)) || 1;
  return <div className="governor-bar-track"><div className={line.error ? 'governor-bar governor-bar-error' : 'governor-bar'} style={{left: `${Math.min(left, 100)}%`, width: `${Math.max(0.4, Math.min(100 - left, line.durationMs * scale))}%`}} title={`${formatNumber(line.durationMs)} ms: ${segments.filter(([, ms]) => ms > 0).map(([, ms, label]) => `${label} ${formatNumber(ms)}`).join(', ')}`}>
    {segments.map(([name, ms]) => ms > 0 && <span key={name} className={`governor-phase governor-phase-${name}`} style={{width: `${ms / total * 100}%`}}/>)}
  </div></div>;
}

function phaseText(p: Phases): string {
  let out = `gate ${formatNumber(p.gateWaitMs)} · call ${formatNumber(p.callMs)} · decode ${formatNumber(p.decodeMs)}`;
  if (p.nativeQueueMs !== null) out += ` · native queue ${formatNumber(p.nativeQueueMs)} exec ${formatNumber(p.nativeExecuteMs ?? 0)}`;
  if (p.echoed) out += ` · echoed ${p.echoed}`;
  return out;
}

export default function TraceView({events, traceId, stale}: {events: readonly TelemetryEvent[]; traceId: string; stale: boolean}) {
  const trace = useMemo(() => buildTrace(events, traceId), [events, traceId]);
  return <section className="observation-panel governor-trace" aria-label="Step trace">
    <h2>Trace</h2>
    {!traceId ? <p>Pick a row's trace in the event feed — a <code>scheduler_step</code> row shows the whole step: its reads, the worker dispatches under it and each native call's phases.</p>
      : !trace ? <p>No retained rows carry trace <code>{traceId}</code>{stale ? ' (telemetry is stale).' : '; it may have left the buffer.'}</p>
      : <>
        <p className="governor-trace-summary"><code>{trace.traceId}</code> · {trace.root} · {trace.rows.length} rows over {formatNumber(trace.spanMs)} ms · tick {trace.tick === null ? '—' : trace.tick.toLocaleString()} · sequence {trace.rows[0].sequence}..{trace.rows[trace.rows.length - 1].sequence}</p>
        <p className="governor-legend"><span className="governor-phase-gate"/> gate <span className="governor-phase-queue"/> native queue <span className="governor-phase-exec"/> native execute <span className="governor-phase-call"/> transport <span className="governor-phase-decode"/> decode</p>
        <table className="governor-table governor-waterfall"><thead><tr><th scope="col">At ms</th><th scope="col">Dur ms</th><th scope="col">Span</th><th scope="col">Row</th><th scope="col" className="governor-bar-column">Waterfall</th></tr></thead>
          <tbody>{trace.lines.map(line => <tr key={line.sequence} className={line.error ? 'governor-error' : undefined}>
            <td>{formatNumber(line.offsetMs)}</td><td>{line.durationMs === null ? '—' : formatNumber(line.durationMs)}</td><td><code>{line.span}</code></td>
            <td className="governor-row-text" style={{paddingLeft: `${10 + line.depth * 16}px`}}>
              {line.text}
              {line.phases && <small className="governor-phases">{phaseText(line.phases)}</small>}
              {line.error && <small className="governor-row-error">{line.error}</small>}
              {line.attrs.length > 0 && <small className="governor-attrs">{line.attrs.map(([key, value]) => `${key}=${value}`).join(' ')}</small>}
            </td>
            <td className="governor-bar-column"><Bar line={line} spanMs={trace.spanMs}/></td>
          </tr>)}</tbody></table>
      </>}
  </section>;
}
