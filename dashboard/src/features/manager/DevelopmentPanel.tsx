import {useEffect, useState} from 'react';
import {fetchRoutineStatus, RoutineHTTPError, type DevelopmentReason, type RoutineStatus} from './routineData';

// Deferral reasons as the controller records them; the panel shows evidence,
// not advice, so labels stay close to the contract vocabulary.
export const reasonLabels: Record<DevelopmentReason, string> = {
  '': 'Eligible', cancelled: 'Cancelled', adviser: 'Adviser hold', emergency: 'Emergency precedence', startup_survival: 'Startup survival precedence', blocked: 'Blocked',
  existing_commitment: 'Already committed', workers_unknown: 'Worker count unknown', no_workers: 'No workers', deficit_unknown: 'Deficit unknown',
  capacity_committed: 'Waiting for capacity', method_unavailable: 'No method available', labor_unavailable: 'Waiting for labor', risk_deferred: 'Deferred: outdoor risk',
};
type Reading = {value: RoutineStatus | null; stale: boolean; hidden: boolean; error: string};
const percent = (v: number | null) => v === null ? 'unknown' : `${Math.round(v * 100)}%`;
export default function DevelopmentPanel({active}: {active: boolean}) {
  const [state, setState] = useState<Reading>({value: null, stale: true, hidden: false, error: ''});
  useEffect(() => {
    if (!active) return;
    const controller = new AbortController(); let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      try {const value = await fetchRoutineStatus(AbortSignal.any([controller.signal, AbortSignal.timeout(5000)])); if (!stopped) setState({value, stale: false, hidden: false, error: ''});}
      catch (error) {if (!stopped) setState(previous => ({value: previous.value, stale: true, hidden: error instanceof RoutineHTTPError && error.status === 404, error: error instanceof Error ? error.message : 'Routine diagnostics unavailable'}));}
      finally {if (!stopped) timer = setTimeout(poll, 3000);}
    };
    void poll(); return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, [active]);
  if (state.hidden) return null;
  const d = state.value?.development ?? null;
  return <section className="observation-panel development-panel" aria-label="Development priorities"><h2>Development priorities</h2>
    {state.stale && <p role="status">{state.value ? 'Stale — last recorded ranking. ' : 'Unavailable. '}{state.error || 'Waiting for routine diagnostics.'}</p>}
    {state.value && !d && <p>No routine review has ranked development yet.</p>}
    {d && <>
      <p>Reviewed tick {d.tick.toLocaleString()} · Capacity {d.capacity} · Workers {d.workers ?? 'unknown'} · Committed {d.committed.length ? d.committed.join(', ') : 'none'}</p>
      {d.labor.length > 0 && <p className="development-labor">Free labor: {d.labor.map(l => `${l.work} ${l.free}`).join(' · ')}</p>}
      {d.rows.length === 0 ? <p>No optional goals are competing.</p> : <table className="development-table"><thead><tr><th scope="col">Goal</th><th scope="col">Status</th><th scope="col">Score</th><th scope="col">Deficit</th><th scope="col">Risk</th><th scope="col">Waiting since</th></tr></thead>
        <tbody>{d.rows.map(row => <tr key={row.goal}>
          <th scope="row">{row.goal}</th>
          <td>{row.selected ? 'Selected' : row.committed ? 'In progress' : reasonLabels[row.reason]}{row.bottleneck && ` (${row.bottleneck})`}</td>
          <td>{row.score.toFixed(1)}</td><td>{percent(row.deficit)}</td><td>{percent(row.risk)}</td><td>{row.waitingSince.toLocaleString()}</td>
        </tr>)}</tbody></table>}
    </>}
    <p className="observation-note">Ordering evidence from the last routine review: emergencies pre-empt this list, and admission is bounded by capacity and free labor.</p>
  </section>;
}
