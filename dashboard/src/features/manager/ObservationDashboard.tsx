import {useEffect, useState} from 'react';
import {readObservation, readPlan, type BuildingAction, type BuildingPlan, type ObservationState, type UnsuccessfulReason} from './observationData';
import './ObservationDashboard.css';
import BuildingControls from './BuildingControls';
import PresentationPanel from './PresentationPanel';

const stageLabels: Record<BuildingAction['progress']['stage'], string> = {pending: 'Pending', prepared: 'Prepared', dispatched: 'Order sent', awaiting_observation: 'Awaiting observation', completed: 'Completed', cancelled: 'Cancelled', unsuccessful: 'Unsuccessful'};
const reasonLabels: Record<UnsuccessfulReason, string> = {native_failure: 'Native operation failed', cancelled: 'Operation cancelled', interrupted: 'Operation interrupted', expired: 'Operation expired', target_dead: 'Target died', outcome_not_achieved: 'Expected outcome not achieved'};
export default function ObservationDashboard() {
  const [state, setState] = useState<ObservationState | null>(null);
  const [plan, setPlan] = useState<BuildingPlan | null>(null);
  const [error, setError] = useState('');
  useEffect(() => {
    let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    let currentKey = '';
    const controller = new AbortController();
    const read = async (path: string): Promise<unknown> => {
      const response = await fetch(path, {signal: AbortSignal.any([controller.signal, AbortSignal.timeout(5000)])});
      if (!response.ok) throw Error(`Refresh unavailable (${response.status})`);
      return response.json();
    };
    const poll = async () => {
      try {
        const next = readObservation(await read('/api/state'));
        if (stopped) return;
        const key = JSON.stringify([next.sessionId, next.identity, next.activePlanId]);
        if (key !== currentKey) {setPlan(null); currentKey = key;}
        setState(next);
        if (next.activePlanId !== null) {
          const nextPlan = readPlan(await read(`/api/plan?id=${encodeURIComponent(next.activePlanId)}`));
          if (nextPlan.id !== next.activePlanId) throw Error('Plan response does not match the active plan');
          if (stopped) return;
          setPlan(nextPlan);
        }
        setError('');
      } catch (reason) {
        if (!stopped) setError(reason instanceof Error ? reason.message : 'Refresh unavailable');
      } finally {if (!stopped) timer = setTimeout(poll, 1500);}
    };
    void poll();
    return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, []);

  return <main className="observation-shell">
    <header className="observation-header"><div><p className="observation-eyebrow">Colony field station</p><h1>RimGovernor</h1><p>Observation mode</p></div>
      <span className="observation-mode">{state?.mode === 'automate' ? 'Automate' : 'Manual'}</span></header>
    <div role="status" className={error ? 'observation-notice observation-warning' : 'observation-notice'}>
      {error ? `${error}. Last available readings remain visible.` : state ? state.status.label || 'Waiting for colony readings' : 'Connecting to your colony…'}
    </div>
    <section className="observation-grid" aria-label="Colony observations">
      <article><h2>Connection</h2><p className="observation-value">{state ? state.connected ? 'Connected' : 'Disconnected' : 'Unknown'}</p>
        <p>{state && !state.game.stale && !error ? 'Current observation' : 'Readings unavailable or stale'}</p></article>
      <article><h2>Simulation</h2><p className="observation-value">{state?.game.paused == null ? 'Unknown' : state.game.paused ? 'Paused' : 'Running'}</p>
        <p>Tick {state?.game.tick == null ? 'unknown' : state.game.tick.toLocaleString()}</p></article>
      <article><h2>Last observed</h2><p className="observation-value observation-time">{state?.game.observedAt ? new Date(state.game.observedAt).toLocaleString() : 'Unknown'}</p>
        <p>Updates automatically while this view is open.</p></article>
    </section>
    <section className="observation-panel"><h2>Colony identity</h2><dl className="observation-identity">
      <div><dt>Colony</dt><dd>{state?.identity?.colonyId ?? 'Unknown'}</dd></div>
      <div><dt>Map</dt><dd>{state?.identity?.mapId ?? 'Unknown'}</dd></div>
      <div><dt>Load</dt><dd>{state?.identity?.loadToken ?? 'Unknown'}</dd></div>
    </dl></section>
    <BuildingControls observation={state} observationFresh={!error && state !== null}/>
    <PresentationPanel observation={state} observationFresh={!error && state !== null}/>
    <section className="observation-panel"><h2>Building plan</h2>
      {!state ? <p>Waiting for plan status.</p> : state.activePlanId === null ? <p>No active plan reported.</p> : !plan ? <p>Waiting for the active plan.</p> : <>
        <p className="observation-plan-id">{plan.id} · Revision {plan.revision}</p>
        {plan.actions.length === 0 ? <p>This plan has no building actions.</p> : <ol className="observation-actions">{plan.actions.map(action => <li key={action.id}>
          <div><h3>{action.building.defName}</h3><p>{action.building.stuff || 'Native default material'} · ({action.building.x}, {action.building.z}) · {action.building.rotation}</p></div>
          <div><strong>{stageLabels[action.progress.stage]}</strong><p>{action.progress.unsuccessfulReason !== null ? reasonLabels[action.progress.unsuccessfulReason] : action.progress.unresolved ? 'Outcome requires observation' : action.progress.effect === 'completed' ? 'Completion observed' : 'Effect: ' + (action.progress.effect ?? 'unknown')}</p>
            <p>Receipt: {action.progress.receipt ?? 'unknown'}</p></div>
        </li>)}</ol>}
      </>}
    </section>
  </main>;
}

