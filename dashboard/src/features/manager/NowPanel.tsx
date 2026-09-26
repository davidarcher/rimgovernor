import {useEffect, useState} from 'react';
import {blockedLabel} from './DevelopmentPanel';
import {fetchNow, SpectatorHTTPError, type Now, type NowStop, type PacingReason} from './spectatorData';

// The spectator "now" panel (#632): what the colony is trying to do, what it
// last achieved, what blocks it and why the governor paced or stopped the
// clock. It polls one read-only route and keeps the last good reading through
// a failed refresh; watching the panel never changes the simulation — no
// journal row, no speed request, no native call.
export const nowInterval = 2000;
export type NowReading = {value: Now | null; stale: boolean; hidden: boolean; error: string};

export function useNow(active: boolean): NowReading {
  const [state, setState] = useState<NowReading>({value: null, stale: true, hidden: false, error: ''});
  useEffect(() => {
    if (!active) return;
    const controller = new AbortController(); let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      try {const value = await fetchNow(AbortSignal.any([controller.signal, AbortSignal.timeout(5000)])); if (!stopped) setState({value, stale: false, hidden: false, error: ''});}
      catch (error) {if (!stopped) setState(previous => ({value: previous.value, stale: true, hidden: error instanceof SpectatorHTTPError && error.status === 404, error: error instanceof Error ? error.message : 'The now panel is unavailable'}));}
      finally {if (!stopped) timer = setTimeout(() => void poll(), nowInterval);}
    };
    void poll(); return () => {stopped = true; controller.abort(); if (timer) clearTimeout(timer);};
  }, [active]);
  return state;
}

// Pacing reasons as the controller records them (spectator.PacingReason); the
// panel names the evidence, it does not advise.
export const pacingLabels: Record<PacingReason, string> = {
  unknown: 'Pace unknown: no step has run yet',
  governor_off: 'Governor off: routine reviews are disabled',
  held: 'Held: a clock event awaits review',
  window_refused: 'Window refused',
  running: 'Running',
  tick_budget: 'Between windows: the last one spent its tick budget',
  stopped: 'Stopped',
  cinematic: 'Cinematic: an interesting moment is slowed on purpose',
};
const ticks = (v: number) => v.toLocaleString();
const ms = (v: number | null) => v === null ? null : `${v.toLocaleString(undefined, {maximumFractionDigits: 1})} ms`;
// stopReason drops the wire prefix: STOP_REASON_COLONIST_HEALTH reads
// "colonist health".
export function stopReason(reason: string): string {
  return reason.replace(/^STOP_REASON_/, '').toLowerCase().replace(/_/g, ' ') || 'unspecified';
}
// stopLegs is the latency split in reading order, skipping the legs the stop
// did not carry.
export function stopLegs(stop: NowStop): string[] {
  const legs: string[] = [];
  if (stop.detectTicks !== null) legs.push(`${ticks(stop.detectTicks)} ticks to detect`);
  if (stop.stopTicks !== null) legs.push(`${ticks(stop.stopTicks)} ticks to stop`);
  const observe = ms(stop.observeMs), acted = ms(stop.actedMs), readmit = ms(stop.readmitMs);
  if (observe) legs.push(`${observe} unobserved in native`);
  if (acted) legs.push(`${acted} to the step that acted`);
  if (readmit) legs.push(`${readmit} paused before readmission`);
  return legs;
}

export default function NowPanel({active}: {active: boolean}) {
  const state = useNow(active);
  if (state.hidden) return null;
  const now = state.value;
  return <section className="observation-panel now-panel" aria-label="Now">
    <h2>Now</h2>
    {state.stale && <p role="status">{now ? 'Stale — the last good reading. ' : 'Unavailable. '}{state.error || 'Waiting for the controller.'}</p>}
    {now && <>
      <p className="now-stage" data-testid="now-stage">
        {now.stage
          ? <>Stage <strong>{now.stage.stage}</strong> since tick {ticks(now.stage.since)}{now.stage.blocker ? <> · next stage waits on {now.stage.blocker}: {now.stage.reason}</> : <> · every condition met</>}{now.stage.held ? ' · development held' : ''}</>
          : <>No routine review has derived a colony stage yet.</>}
        {now.tick !== null && <> · tick {ticks(now.tick)}</>}
      </p>
      <p className="now-pacing" data-testid="now-pacing">
        {pacingLabels[now.pacing.reason]}{now.pacing.detail ? `: ${now.pacing.detail}` : ''} · {now.pacing.effectiveTps.toLocaleString(undefined, {maximumFractionDigits: 0})} ticks/s
        {now.pacing.windowTicks > 0 && <> · window {ticks(now.pacing.windowTicks)} ticks</>}
      </p>
      {now.goals.length === 0
        ? <p>No active goal has filed a progress record yet.</p>
        : <table className="now-goals"><caption>What the colony is working on, the most urgent first</caption>
          <thead><tr><th scope="col">Goal</th><th scope="col">Method</th><th scope="col">Expected</th><th scope="col">Last progress</th><th scope="col">Review by</th><th scope="col">Status</th></tr></thead>
          <tbody>{now.goals.map(goal => <tr key={goal.goal} data-blocked={goal.blocked ? 'yes' : 'no'}>
            <th scope="row">{goal.goal}</th><td>{goal.method || 'none'}</td><td>{goal.expected}</td>
            <td>{ticks(goal.lastProgress)}</td><td>{goal.nextReview === 0 ? 'no deadline' : ticks(goal.nextReview)}</td>
            <td>{blockedLabel(goal.blocked)}{goal.observed !== null ? ` · deficit ${Math.round(goal.observed * 100)}%` : ''}</td>
          </tr>)}</tbody>
        </table>}
      <p className="now-stop" data-testid="now-stop">
        {now.lastStop
          ? <>Last stop: {stopReason(now.lastStop.reason)}{now.lastStop.evidence ? ` (${now.lastStop.evidence})` : ''} at tick {ticks(now.lastStop.tick)}{now.lastStop.benign ? ', the controller’s own' : ''}{stopLegs(now.lastStop).length > 0 ? ` — ${stopLegs(now.lastStop).join(', ')}` : ''} · {now.stops.stops} stop(s) this launch, {now.stops.budget} on budget and {now.stops.reactive} reactive</>
          : <>No window has stopped on this launch.</>}
      </p>
      <p className="observation-note">Watching is read-only: the panel polls recorded evidence, so connecting or leaving changes no game speed and writes nothing. Progress is native evidence, not dispatch.</p>
    </>}
  </section>;
}
