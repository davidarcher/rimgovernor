import {useEffect, useState} from 'react';
import {readObservation, readPlan, type BuildingAction, type BuildingPlan, type ObservationState, type UnsuccessfulReason} from './observationData';
import {readPlayerSession} from './playerData';
import {fetchPresentation, readRoster, type Roster} from './presentationData';
import './ObservationDashboard.css';
import PlayerControls from './PlayerControls';
import PresentationPanel from './PresentationPanel';
import NotificationPanel from './NotificationPanel';
import DevelopmentPanel from './DevelopmentPanel';
import GameVideoGo, {MapOverviewGo, PawnFeedGo} from './GameVideoGo';
import PawnPortraitGo from './PawnPortraitGo';
import PlayerGuide from './PlayerGuide';

const stageLabels: Record<BuildingAction['progress']['stage'], string> = {pending: 'Pending', prepared: 'Prepared', dispatched: 'Order sent', awaiting_observation: 'Awaiting observation', completed: 'Completed', cancelled: 'Cancelled', unsuccessful: 'Unsuccessful'};
const cleanupLabels = {awaiting_claim: 'Ownership not yet known', not_acquired: 'No owned draft acquired', required: 'Release required', dispatched: 'Release sent', uncertain: 'Release outcome unknown', released: 'Release observed', superseded: 'Original ownership no longer applies'};
const reasonLabels: Record<UnsuccessfulReason, string> = {native_failure: 'Native operation failed', cancelled: 'Operation cancelled', interrupted: 'Operation interrupted', expired: 'Operation expired', target_dead: 'Target died', outcome_not_achieved: 'Expected outcome not achieved'};

type View = 'watch' | 'work' | 'colony' | 'help';
function currentView(): View {
  const hash = location.hash.replace(/^#/, '');
  if (hash.startsWith('help')) return 'help';
  if (hash === 'work') return 'work';
  if (hash === 'colony') return 'colony';
  return 'watch';
}

// A shared player token, used by the video and portrait panels below;
// PlayerControls independently bootstraps its own copy of the same read.
function usePlayerToken(sessionId: string): string | null {
  const [token, setToken] = useState<string | null>(null);
  useEffect(() => {
    if (!sessionId) {setToken(null); return;}
    let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    const controller = new AbortController();
    const poll = async () => {
      try {
        const next = await readPlayerSession(AbortSignal.any([controller.signal, AbortSignal.timeout(5000)]));
        if (!stopped) {setToken(next); timer = setTimeout(() => void poll(), 15000);}
      } catch { if (!stopped) timer = setTimeout(() => void poll(), 3000); }
    };
    void poll();
    return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, [sessionId]);
  return token;
}

function WorkPanel({state, plan}: {state: ObservationState | null; plan: BuildingPlan | null}) {
  return <section className="observation-panel" aria-label="Work">
    <h2>Active plan</h2>
    {!state ? <p>Waiting for plan status.</p> : state.activePlanId === null ? <p>No active plan reported.</p> : !plan ? <p>Waiting for the active plan.</p> : <>
      <p className="observation-plan-id">{plan.id} · Revision {plan.revision}</p>
      {plan.actions.length === 0 ? <p>This plan has no actions.</p> : <ol className="observation-actions">{plan.actions.map(action => <li key={action.id}>
        <div>{action.kind === 'building' ? <><h3>{action.building.defName}</h3><p>{action.building.stuff || 'Native default material'} · ({action.building.x}, {action.building.z}) · {action.building.rotation}</p></> : <><h3>Temporary draft</h3><p>Pawn {action.draft.pawnId}</p></>}</div>
        <div><strong>{stageLabels[action.progress.stage]}</strong><p>{action.progress.unsuccessfulReason !== null ? reasonLabels[action.progress.unsuccessfulReason] : action.progress.unresolved ? 'Outcome requires observation' : action.progress.effect === 'completed' ? 'Completion observed' : 'Effect: ' + (action.progress.effect ?? 'unknown')}</p>
          <p>Receipt: {action.progress.receipt ?? 'unknown'}</p>{action.progress.draftCleanup && <p>Draft cleanup: {cleanupLabels[action.progress.draftCleanup.stage]}</p>}</div>
      </li>)}</ol>}
    </>}
    <p className="observation-note">Work here is submitted through Player controls on Watch; this list is a read-only status feed — there is no control to cancel a step in place yet.</p>
  </section>;
}

// A secondary, portrait-only view of the same roster PresentationPanel already reads;
// kept separate since PresentationPanel doesn't expose the roster it fetches to its parent.
function ColonyPortraits({token, active}: {token: string | null; active: boolean}) {
  const [roster, setRoster] = useState<Roster | null>(null);
  useEffect(() => {
    if (!active) return;
    let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    const controller = new AbortController();
    const poll = async () => {
      try {
        const next = await fetchPresentation('colonists', readRoster, AbortSignal.any([controller.signal, AbortSignal.timeout(5000)]));
        if (!stopped) setRoster(next);
      } catch { /* PresentationPanel above already surfaces roster read errors */ }
      if (!stopped) timer = setTimeout(() => void poll(), 5000);
    };
    void poll();
    return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, [active]);
  if (!token || !roster || roster.colonists.length === 0) return null;
  const colonists = roster.colonists.filter(c => c.pawnId !== null).slice(0, 24);
  return <>
    <section className="observation-panel" aria-label="Colonist feeds"><h2>Colonist feeds</h2><div className="pawn-feed-grid">
      {colonists.slice(0, 8).map(c => <PawnFeedGo key={c.pawnId} token={token} pawnId={c.pawnId as string} name={c.name ?? (c.pawnId as string)} active={active}/>)}
    </div></section>
    <section className="observation-panel" aria-label="Colonist portraits"><h2>Portraits</h2><div className="pawn-portrait-grid">
      {colonists.map(c => <PawnPortraitGo key={c.pawnId} token={token} pawnId={c.pawnId as string} name={c.name ?? (c.pawnId as string)} active={active}/>)}
    </div></section>
  </>;
}

export default function ObservationDashboard() {
  const [state, setState] = useState<ObservationState | null>(null);
  const [plan, setPlan] = useState<BuildingPlan | null>(null);
  const [error, setError] = useState('');
  const [view, setView] = useState<View>(currentView);
  useEffect(() => {
    const onHashChange = () => setView(currentView());
    window.addEventListener('hashchange', onHashChange);
    return () => window.removeEventListener('hashchange', onHashChange);
  }, []);
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
      } finally {if (!stopped) timer = setTimeout(() => void poll(), 1500);}
    };
    void poll();
    return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, []);
  const token = usePlayerToken(state?.sessionId ?? '');
  const observationFresh = !error && state !== null;

  return <main className="observation-shell">
    <header className="observation-header"><div><p className="observation-eyebrow">Colony field station</p><h1>RimGovernor</h1><p>Observation mode</p></div>
      <span className="observation-mode">{state?.mode === 'automate' ? 'Automate' : 'Manual'}</span></header>
    <nav className="observation-nav" aria-label="Dashboard sections">
      <a href="#watch" aria-current={view === 'watch' ? 'page' : undefined}>Watch</a>
      <a href="#work" aria-current={view === 'work' ? 'page' : undefined}>Work</a>
      <a href="#colony" aria-current={view === 'colony' ? 'page' : undefined}>Colony</a>
      <a href="#help" aria-current={view === 'help' ? 'page' : undefined}>Help</a>
    </nav>
    {view === 'help' ? <PlayerGuide/> : <>
      <div role="status" className={error ? 'observation-notice observation-warning' : 'observation-notice'}>
        {error ? `${error}. Last available readings remain visible.` : state ? state.status.label || 'Waiting for colony readings' : 'Waiting for the game — start it, then load a save. See Help for launch instructions.'}
      </div>
      <section className="observation-grid" aria-label="Colony observations">
        <article><h2>Connection</h2><p className="observation-value">{state ? state.connected ? 'Connected' : 'Disconnected' : 'Waiting for the game'}</p>
          <p>{state && !state.game.stale && !error ? 'Current observation' : 'Readings unavailable or stale'}</p></article>
        <article><h2>Simulation</h2><p className="observation-value">{state?.game.paused == null ? '—' : state.game.paused ? 'Paused' : 'Running'}</p>
          <p>Tick {state?.game.tick == null ? '—' : state.game.tick.toLocaleString()}</p></article>
        <article><h2>Last observed</h2><p className="observation-value observation-time">{state?.game.observedAt ? new Date(state.game.observedAt).toLocaleString() : '—'}</p>
          <p>Updates automatically while this view is open.</p></article>
      </section>
      <section className="observation-panel"><h2>Colony identity</h2><dl className="observation-identity">
        <div><dt>Colony</dt><dd>{state?.identity?.colonyId ?? '—'}</dd></div>
        <div><dt>Map</dt><dd>{state?.identity?.mapId ?? '—'}</dd></div>
        <div><dt>Load</dt><dd>{state?.identity?.loadToken ?? '—'}</dd></div>
      </dl></section>
      {view === 'watch' && <>
        <section className="observation-panel"><h2>Camera</h2><GameVideoGo token={token} active/></section>
        <section className="observation-panel"><h2>Map overview</h2><MapOverviewGo token={token} active/></section>
        <PlayerControls observation={state} observationFresh={observationFresh}/>
      </>}
      {view === 'work' && <>
        <WorkPanel state={state} plan={plan}/>
        <DevelopmentPanel active/>
        <NotificationPanel observation={state} observationFresh={observationFresh}/>
      </>}
      {view === 'colony' && <>
        <PresentationPanel observation={state} observationFresh={observationFresh}/>
        <ColonyPortraits token={token} active/>
      </>}
    </>}
  </main>;
}
