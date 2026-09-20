import {type ColonyGrid, type DevelopmentReason, type ResourceRunway} from './routineData';
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
const gridSourceLabels: Record<string, string> = {starter_shell: 'starter shell', largest_room: 'largest room'};
// gridLines lists the map coordinates of every grid line along one map axis
// inside a bound: lines pass through the origin's coordinate on that axis
// every pitch cells, whichever grid axis runs along it.
export function gridLines(origin: number, pitch: number, bound: number): number[] {
  const lines: number[] = [];
  for (let c = origin - Math.floor(origin / pitch) * pitch; c < bound; c += pitch) lines.push(c);
  return lines;
}
// ColonyGridOverlay draws the persisted grid over the map bounds: one line
// per grid line on each axis and a marker at the origin. Map z grows north,
// so the drawing flips it to keep south at the bottom.
export function ColonyGridOverlay({grid}: {grid: ColonyGrid}) {
  const {width, height} = grid.bounds;
  if (width === 0 || height === 0) return null;
  const scale = Math.min(320 / width, 320 / height);
  const px = (x: number) => x * scale;
  const pz = (z: number) => (height - z) * scale;
  const xs = gridLines(grid.origin.x, grid.pitch, width);
  const zs = gridLines(grid.origin.z, grid.pitch, height);
  return <svg className="colony-grid-overlay" role="img" aria-label={`Colony grid overlay: ${xs.length} lines across, ${zs.length} lines up, origin at ${grid.origin.x}, ${grid.origin.z}`} viewBox={`0 0 ${px(width)} ${pz(0)}`} width={px(width)} height={pz(0)}>
    <rect x={0} y={0} width={px(width)} height={pz(0)} fill="none" stroke="currentColor" strokeWidth={1}/>
    {xs.map(x => <line key={`x${x}`} data-grid-line="x" x1={px(x)} x2={px(x)} y1={0} y2={pz(0)} stroke="currentColor" strokeOpacity={0.35} strokeWidth={0.5}/>)}
    {zs.map(z => <line key={`z${z}`} data-grid-line="z" x1={0} x2={px(width)} y1={pz(z)} y2={pz(z)} stroke="currentColor" strokeOpacity={0.35} strokeWidth={0.5}/>)}
    <circle data-grid-origin cx={px(grid.origin.x)} cy={pz(grid.origin.z)} r={3} fill="currentColor"/>
  </svg>;
}
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
    {state.value?.extentEligibility && <section aria-label="Colony extent"><h3>Colony extent</h3>
      <p>{state.value.extentEligibility.known ? 'Established territory' : 'Territory unknown'}{state.value.extentEligibility.reason && ` · ${state.value.extentEligibility.reason}`}</p>
      {state.value.resourceReach && <p>Resource reach: {state.value.resourceReach.stage} · {state.value.resourceReach.reason}</p>}
      {state.value.extentEligibility.regions.map(r => <p key={r.region}>Region {r.region + 1} · {r.stage} · {r.cells} cells · Origins: {r.origins.join(', ')} · Active facilities: {r.activeFacilities.join(', ') || 'none'} · {r.eligible ? 'Eligible' : r.holdReasons.join(', ')}</p>)}
      <p>Current holds do not erase territory history. Home coverage is managed separately.</p>
    </section>}
    {state.value && <section aria-label="Colony grid"><h3>Colony grid</h3>
      {state.value.colonyGrid ? <>
        <p>Origin {state.value.colonyGrid.origin.x}, {state.value.colonyGrid.origin.z} · Pitch {state.value.colonyGrid.pitch} · From {gridSourceLabels[state.value.colonyGrid.source] ?? state.value.colonyGrid.source} · Map {state.value.colonyGrid.bounds.width}×{state.value.colonyGrid.bounds.height}</p>
        <ColonyGridOverlay grid={state.value.colonyGrid}/>
      </> : <p>No colony grid has been established yet.</p>}
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
