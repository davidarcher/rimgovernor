import type {MetricsReading, Sample} from './useTelemetry';

// Sparkline is the sample series as one polyline, newest at the right.
export function Sparkline({values, label}: {values: number[]; label: string}) {
  const width = 120, height = 28;
  if (values.length < 2) return <svg className="governor-spark" width={width} height={height} role="img" aria-label={`${label}: collecting samples`}/>;
  const max = Math.max(...values), min = Math.min(...values), range = max - min || 1;
  const points = values.map((v, i) => `${(i / (values.length - 1) * (width - 2) + 1).toFixed(1)},${(height - 2 - (v - min) / range * (height - 4)).toFixed(1)}`).join(' ');
  return <svg className="governor-spark" width={width} height={height} viewBox={`0 0 ${width} ${height}`} role="img" aria-label={`${label} over the session, ${values.length} samples, ${formatNumber(min)} to ${formatNumber(max)}`}>
    <polyline points={points} fill="none" stroke="currentColor" strokeWidth="1.5"/>
  </svg>;
}

export function formatNumber(v: number): string {
  if (!Number.isFinite(v)) return '—';
  if (Math.abs(v) >= 1000) return Math.round(v).toLocaleString();
  if (Math.abs(v) >= 10) return v.toFixed(1);
  return v.toFixed(2);
}

function Reading({label, value, series, pick, unit}: {label: string; value: string; series: Sample[]; pick: (s: Sample) => number; unit?: string}) {
  return <article className="governor-reading"><h3>{label}</h3><p className="governor-reading-value">{value}{unit && <small> {unit}</small>}</p><Sparkline values={series.map(pick)} label={label}/></article>;
}

export default function HealthStrip({reading}: {reading: MetricsReading}) {
  const m = reading.value;
  const authority = m === null ? '—' : m.authority === null ? 'None' : `Plan ${m.authority.plan} r${m.authority.revision} · native ${m.authority.native}`;
  return <section className="observation-panel governor-health" aria-label="Governor health">
    <div className="governor-health-heading"><h2>Health</h2>
      {reading.stale && <p role="status" className="governor-stale">{reading.value ? 'Stale — last reading shown. ' : 'Unavailable. '}{reading.error || 'Waiting for telemetry.'}</p>}
    </div>
    <div className="governor-readings">
      <Reading label="Tick" value={m?.tick == null ? '—' : m.tick.toLocaleString()} series={reading.series} pick={s => s.tps} unit={m ? `· ${formatNumber(m.tps)} TPS` : undefined}/>
      <Reading label="Last step" value={m ? formatNumber(m.lastStepMs) : '—'} unit="ms" series={reading.series} pick={s => s.lastStepMs}/>
      <Reading label="Native errors" value={m ? String(m.metrics.native_errors ?? 0) : '—'} unit={m ? `of ${m.metrics.native_calls ?? 0} calls` : undefined} series={reading.series} pick={s => s.nativeErrors}/>
      <Reading label="Reads per step" value={m ? formatNumber(m.metrics.reads_per_step_mean ?? 0) : '—'} series={reading.series} pick={s => s.readsPerStep}/>
      <article className="governor-reading"><h3>Authority</h3><p className="governor-reading-value governor-authority">{authority}</p>
        {m && <p className="governor-reading-note">Run {m.run ? m.run.slice(0, 8) : '—'} · cache hit {formatNumber((m.metrics.cache_hit_ratio ?? 0) * 100)}% · queue {formatNumber(m.metrics.native_queue_ms_mean ?? 0)} ms · exec {formatNumber(m.metrics.native_exec_ms_mean ?? 0)} ms</p>}
      </article>
    </div>
  </section>;
}
