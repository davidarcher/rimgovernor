import { useEffect, useState } from 'react';
import { ColonyReadings } from './Autopilot';
import type { Committed } from './CommittedPlan';
import type { PeopleObservation } from './People';
import './Autopilot.css';
import './LocalColonies.css';

type State = { sessionId: string; status: { label: string }; game: { tick?: number; paused: boolean };
  currentPlan?: Committed; observation?: PeopleObservation; mode: string; headless: boolean;
  cameraVersion: number; cameraCapturedAt: number;
  feed: {id: number; text: string}[]; };

export default function ScenarioWatch() {
  const [state, setState] = useState<State | null>(null);
  const [error, setError] = useState('');
  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    const poll = async () => {
      try {
        if (!document.hidden) {
          const response = await fetch('/api/state', {signal: AbortSignal.timeout(5000)});
          if (!response.ok) throw Error('Scenario is between runtimes or unavailable. Last readings are retained.');
          const data = await response.json();
          if (!stopped) { setState(data); setError(''); }
        }
      } catch (e) { if (!stopped) setError(e instanceof Error ? e.message : 'Refresh failed'); }
      if (!stopped) timer = setTimeout(poll, 2000);
    };
    void poll();
    return () => { stopped = true; clearTimeout(timer); };
  }, []);
  return <main className="colony-directory"><section className="local-colonies">
    <h1>Scenario watch</h1>
    <p>Observation only · the test owns colony control.</p>
    {error && <p role="status">{error}</p>}
    {!state ? <p>Waiting for scenario readings…</p> : <>
      <h2>{state.status.label}</h2>
      <p>Tick {state.game.tick ?? 'unknown'} · {state.game.paused ? 'Paused' : 'Running'} · {state.mode}</p>
      <ColonyReadings plan={state.currentPlan}/>
      {state.headless ? <p>Headless scenario · colony state is available without game images.</p>
        : state.cameraVersion > 0 ? <figure key={state.sessionId}>
          <img style={{maxWidth: '100%'}} src={`/api/camera?session_id=${encodeURIComponent(state.sessionId)}&v=${state.cameraVersion}`} alt="Last frame retained by the scenario"/>
          <figcaption>Retained scenario frame · {new Date(state.cameraCapturedAt * 1000).toLocaleTimeString()}. Viewing does not request new captures.</figcaption>
        </figure> : <p>No frame retained by this scenario yet.</p>}
      <h2>Colonists</h2><ul>{state.observation?.pawns.map(pawn => <li key={pawn.thing_id}>
        <strong>{pawn.name}</strong><span>{pawn.dead ? 'Dead' : pawn.downed ? 'Downed' : pawn.job || 'No observed job'}</span>
      </li>)}</ul>
      <h2>Current work</h2><p>{state.currentPlan?.rationale || 'No plan recorded yet.'}</p>
      <ul>{state.currentPlan?.steps.map(step => <li key={step.id}><strong>{step.title}</strong><span>{step.state}</span></li>)}</ul>
      <h2>Recent activity</h2><ul>{state.feed.slice(-12).reverse().map(item => <li key={item.id}>{item.text}</li>)}</ul>
    </>}
  </section></main>;
}
