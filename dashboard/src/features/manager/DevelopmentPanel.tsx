import {type DevelopmentReason, type ResourceRunway} from './routineData';
import {useRoutineStatus} from './useRoutineStatus';

// Deferral reasons as the controller records them; the panel shows evidence,
// not advice, so labels stay close to the contract vocabulary.
export const reasonLabels: Record<DevelopmentReason, string> = {
  '': 'Eligible', cancelled: 'Cancelled', adviser: 'Adviser hold', emergency: 'Emergency precedence', startup_survival: 'Startup survival precedence', blocked: 'Blocked',
  existing_commitment: 'Already committed', labor_idle: 'Committed work idle: slot released', workers_unknown: 'Worker count unknown', no_workers: 'No workers', deficit_unknown: 'Deficit unknown',
  capacity_committed: 'Waiting for capacity', method_unavailable: 'No method available', labor_unavailable: 'Waiting for labor', risk_deferred: 'Deferred: outdoor risk', control_disabled: 'Controller not in control',
};
const percent = (v: number | null) => v === null ? 'unknown' : `${Math.round(v * 100)}%`;
const number = (v: number | null) => v === null ? 'unknown' : v.toLocaleString(undefined, {maximumFractionDigits: 1});
const days = (v: number | null, r: ResourceRunway) => v !== null ? number(v) : r.consumptionPerDay === 0 ? 'No observed consumption' : 'unknown';
const materialName = (resource: string) => resource === 'ComponentIndustrial' ? 'Components' : resource;
export default function DevelopmentPanel({active}: {active: boolean}) {
  const state = useRoutineStatus(active);
  if (state.hidden) return null;
  const d = state.value?.development ?? null;
  return <section className="observation-panel development-panel" aria-label="Development priorities"><h2>Development priorities</h2>
    {state.stale && <p role="status">{state.value ? 'Stale — last recorded ranking and forecasts. ' : 'Unavailable. '}{state.error || 'Waiting for routine diagnostics.'}</p>}
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
    {state.value && <section aria-label="Material runway"><h3>Material runway</h3>
      {state.value.resourceRunways.length === 0 ? <p>No material runway has been recorded yet.</p> : <>
        <div className="material-runway-scroll" role="region" aria-label="Material runway details" tabIndex={0}><table className="development-table"><thead><tr><th scope="col">Material</th><th scope="col">Days left</th><th scope="col">Stock-only days</th><th scope="col">Stock</th><th scope="col">Surface ore</th><th scope="col">Consumption/day</th><th scope="col">Reserve</th><th scope="col">Status</th><th scope="col">Target</th><th scope="col">Review</th></tr></thead>
          <tbody>{state.value.resourceRunways.map((r, index) => <tr key={index}>
            <th scope="row">{materialName(r.resource)}</th><td>{days(r.daysLeft, r)}</td><td>{days(r.stockDays, r)}</td><td>{number(r.stock)}</td><td>{number(r.surfaceOre)}</td><td>{number(r.consumptionPerDay)}</td><td>{number(r.reserve)}</td>
            <td>{r.deficit === null ? 'unknown' : r.deficit ? 'Deficit' : 'Sufficient'} (threshold {number(r.thresholdDays)} days)</td><td>{number(r.target)}</td><td>Tick {r.tick.toLocaleString()} · {number(r.windowDays)}-day window</td>
          </tr>)}</tbody>
        </table></div>
        <p>Days left includes stock above reserve and safe surface ore; stock-only days excludes ore. Unknown values mean evidence is missing. No observed consumption means no finite runway estimate.</p>
      </>}
    </section>}
    <p className="observation-note">Ordering evidence from the last routine review: emergencies pre-empt this list, and admission is bounded by capacity and free labor.</p>
  </section>;
}
