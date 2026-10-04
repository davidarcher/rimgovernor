import {type DevelopmentReason, type GoalProgress, type ResourceRunway} from './routineData';
import {useRoutineStatus} from './useRoutineStatus';

// Deferral reasons as the controller records them; the panel shows evidence,
// not advice, so labels stay close to the contract vocabulary.
export const reasonLabels: Record<DevelopmentReason, string> = {
  '': 'Eligible', cancelled: 'Cancelled', emergency: 'Emergency precedence', startup_survival: 'Startup survival precedence', blocked: 'Blocked',
  existing_commitment: 'Already committed', labor_idle: 'Committed work idle: slot released', workers_unknown: 'Worker count unknown', no_workers: 'No workers', deficit_unknown: 'Deficit unknown',
  capacity_committed: 'Waiting for capacity', method_unavailable: 'No method available', labor_unavailable: 'Waiting for labor', risk_deferred: 'Deferred: outdoor risk', control_disabled: 'Controller not in control', stage_foothold: 'Held at Foothold: shelter unmet', workers_overcommitted: 'Paused: open work holds every worker',
};
// Blockers as the controller records them (policy.BlockedReason); a
// prerequisite names the goal that must land first.
export function blockedLabel(blocked: string): string {
  const labels: Record<string, string> = {'': 'Progressing', no_worker: 'No capable worker available', native_ineligible: 'Native holds the order ineligible', reconcile_write: 'Reconciling an uncertain order', cooldown: 'Every method on cooldown', no_method: 'No method in play'};
  if (blocked.startsWith('prerequisite:')) return `Needs ${blocked.slice('prerequisite:'.length)} first`;
  return labels[blocked] ?? blocked;
}
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
    {state.value?.stage && <p className="colony-stage" data-testid="colony-stage">Colony stage {state.value.stage.stage} since tick {state.value.stage.since.toLocaleString()}{state.value.stage.blocker ? ` · next stage waits on ${state.value.stage.blocker}: ${state.value.stage.reason}` : ''}{state.value.stage.held ? ' · comfort-class development held' : ''}</p>}
    {state.value && !d && <p>No routine review has ranked development yet.</p>}
    {d && <>
      <p>Reviewed tick {d.tick.toLocaleString()} · Automatic admission, at most {d.capacity} · Workers {d.workers ?? 'unknown'} · Committed {d.committed.length ? d.committed.join(', ') : 'none'}</p>
      <p className="development-capacity" data-testid="development-capacity">Held by startup work: {d.heldWorkers} · {d.limiting ? `Limited by: ${reasonLabels[d.limiting]}` : 'No eligible goal waiting'}</p>
      {d.labor.length > 0 && <p className="development-labor">Free labor: {d.labor.map(l => `${l.work} ${l.free}`).join(' · ')}</p>}
      {d.rows.length === 0 ? <p>No optional goals are competing.</p> : <table className="development-table"><thead><tr><th scope="col">Goal</th><th scope="col">Status</th><th scope="col">Score</th><th scope="col">Deficit</th><th scope="col">Risk</th><th scope="col">Waiting since</th></tr></thead>
        <tbody>{d.rows.map(row => <tr key={row.goal}>
          <th scope="row">{row.goal}</th>
          <td>{row.selected ? 'Selected' : row.committed ? 'In progress' : reasonLabels[row.reason]}{row.bottleneck && ` (${row.bottleneck})`}</td>
          <td>{row.score.toFixed(1)}</td><td>{percent(row.deficit)}</td><td>{percent(row.risk)}</td><td>{row.waitingSince.toLocaleString()}</td>
        </tr>)}</tbody></table>}
    </>}
    {state.value && state.value.progress.length > 0 && <section aria-label="Goal progress"><h3>Goal progress</h3>
      <table className="development-table"><thead><tr><th scope="col">Goal</th><th scope="col">Method</th><th scope="col">Expected</th><th scope="col">Last progress</th><th scope="col">Next review</th><th scope="col">Blocked</th></tr></thead>
        <tbody>{state.value.progress.map((p: GoalProgress) => <tr key={p.goal}>
          <th scope="row">{p.goal}</th><td>{p.method}</td><td>{p.expected}</td><td>{p.lastProgress.toLocaleString()}</td><td>{p.nextReview.toLocaleString()}</td>
          <td>{blockedLabel(p.blocked)}{p.cooldowns.length > 0 && ` · cooldowns: ${p.cooldowns.map(c => `${c.key} until ${c.until.toLocaleString()}`).join(', ')}`}</td>
        </tr>)}</tbody></table>
      <p>Progress is native evidence, not dispatch: a deadline that passes rotates the method or target and keys the failed situation out for a bounded cooldown.</p>
    </section>}
    {state.value?.extentEligibility && <section aria-label="Colony extent"><h3>Colony extent</h3>
      <p>{state.value.extentEligibility.known ? 'Established territory' : 'Territory unknown'}{state.value.extentEligibility.reason && ` · ${state.value.extentEligibility.reason}`}</p>
      {state.value.resourceReach && <p>Resource reach: {state.value.resourceReach.stage} · {state.value.resourceReach.reason}</p>}
      {state.value.extentEligibility.regions.map(r => <p key={r.region}>Region {r.region + 1} · {r.stage} · {r.cells} cells · Origins: {r.origins.join(', ')} · Active facilities: {r.activeFacilities.join(', ') || 'none'} · {r.eligible ? 'Eligible' : r.holdReasons.join(', ')}</p>)}
      <p>Current holds do not erase territory history. Home coverage is managed separately.</p>
    </section>}
    {state.value?.layoutTidy && <section aria-label="Layout tidy"><h3>Layout tidy</h3>
      {state.value.layoutTidy.proposal ? <>
        <p>Pending re-site: {state.value.layoutTidy.proposal.kind} {state.value.layoutTidy.proposal.item} from {state.value.layoutTidy.proposal.from.width}�{state.value.layoutTidy.proposal.from.height} at {state.value.layoutTidy.proposal.from.x}, {state.value.layoutTidy.proposal.from.z}{state.value.layoutTidy.proposal.kind === 'shell' ? ' (deconstruct)' : ` to ${state.value.layoutTidy.proposal.to.width}�${state.value.layoutTidy.proposal.to.height} at ${state.value.layoutTidy.proposal.to.x}, ${state.value.layoutTidy.proposal.to.z}`}{state.value.layoutTidy.proposal.crop && ` � crop ${state.value.layoutTidy.proposal.crop}`} � gain {state.value.layoutTidy.proposal.gain}</p>
        <p>{state.value.layoutTidy.proposal.explanation}</p>
      </> : <p>No re-site pending{state.value.layoutTidy.reason && ` � ${state.value.layoutTidy.reason}`}{state.value.layoutTidy.candidates > 0 && ` � ${state.value.layoutTidy.candidates} to tidy`}</p>}
      <p>One re-site moves at a time, only while no construction or hauling work is open; a tidied item is never re-sited again.</p>
    </section>}
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
