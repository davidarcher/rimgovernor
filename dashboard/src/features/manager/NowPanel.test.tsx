import '@testing-library/jest-dom/vitest';
import {cleanup, render, screen, waitFor, within} from '@testing-library/react';
import {afterEach, expect, it, vi} from 'vitest';
import NowPanel, {nowInterval, stopLegs, stopReason} from './NowPanel';
import {readNow} from './spectatorData';

const reply = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), {status, headers: {'content-type': 'application/json'}}));
const stop = {
  reason: 'STOP_REASON_COLONIST_HEALTH', evidence: 'health', cursor: 41, benign: false, tick: 5040,
  detectedTick: 5030, occurrenceTick: 5000, detectTicks: 30, stopTicks: 10, observeMs: 12, actedMs: 40, readmitMs: 500,
};
const now = {
  tick: 5050,
  stage: {stage: 'Reserves', since: 4000, blocker: 'wood', reason: 'wood floor 120 of 400', held: false},
  goals: [
    {goal: 'MaintainFoodStorage', method: 'hunt', expected: 'designated animal killed', lastProgress: 4500, nextReview: 6000, blocked: 'no_worker', prerequisite: '', observed: 0.4},
    {goal: 'MaintainResource-WoodLog', method: 'chop', expected: 'wood in storage', lastProgress: 4900, nextReview: 0, blocked: '', prerequisite: '', observed: null},
  ],
  pacing: {reason: 'stopped', detail: 'STOP_REASON_COLONIST_HEALTH', mode: 'autonomous', effectiveTps: 820, windowTicks: 2500},
  lastStop: stop,
  stops: {stops: 3, budget: 2, reactive: 1},
};
afterEach(() => {cleanup(); vi.unstubAllGlobals();});

it('shows the stage, the pacing reason, the goals and the last stop with its latency split', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply(now)));
  render(<NowPanel active/>);
  await waitFor(() => expect(screen.getByTestId('now-stage')).toHaveTextContent('Stage Reserves since tick 4,000 · next stage waits on wood: wood floor 120 of 400 · tick 5,050'));
  expect(screen.getByTestId('now-pacing')).toHaveTextContent('Stopped: STOP_REASON_COLONIST_HEALTH · 820 ticks/s · window 2,500 ticks');
  const food = within(screen.getByRole('row', {name: /MaintainFoodStorage/}));
  expect(food.getByText('hunt')).toBeInTheDocument();
  expect(food.getByText('designated animal killed')).toBeInTheDocument();
  expect(food.getByText('4,500')).toBeInTheDocument();
  expect(food.getByText('6,000')).toBeInTheDocument();
  expect(food.getByText('No capable worker available · deficit 40%')).toBeInTheDocument();
  expect(within(screen.getByRole('row', {name: /MaintainResource-WoodLog/})).getByText('no deadline')).toBeInTheDocument();
  expect(screen.getByTestId('now-stop')).toHaveTextContent('Last stop: colonist health (health) at tick 5,040 — 30 ticks to detect, 10 ticks to stop, 12 ms unobserved in native, 40 ms to the step that acted, 500 ms paused before readmission · 3 stop(s) this launch, 2 on budget and 1 reactive');
});

it('shows a player-accelerated window pacing reason, held rate and effective speed', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({...now, lastStop: null, pacing: {reason: 'frame_budget', detail: 'ticks per frame held to the frame budget', mode: 'autonomous', effectiveTps: 3900, windowTicks: 60000, pacedTps: 4200}})));
  render(<NowPanel active/>);
  await waitFor(() => expect(screen.getByTestId('now-pacing')).toHaveTextContent('Frame-paced: ticks per frame held to the frame budget · 3,900 ticks/s · holding 4,200 ticks/s · window 60,000 ticks'));
});

it('names an opt-in cinematic pace as the pacing reason', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({...now, pacing: {reason: 'cinematic', detail: 'cinematic', mode: 'cinematic', effectiveTps: 60, windowTicks: 0}})));
  render(<NowPanel active/>);
  await waitFor(() => expect(screen.getByTestId('now-pacing')).toHaveTextContent('Cinematic: an interesting moment is slowed on purpose: cinematic · 60 ticks/s'));
});

it('says what is missing before a review and before any stop', async () => {
  vi.stubGlobal('fetch', vi.fn(() => reply({tick: null, stage: null, goals: [], pacing: {reason: 'unknown', detail: '', mode: 'autonomous', effectiveTps: 0, windowTicks: 0}, lastStop: null, stops: {stops: 0, budget: 0, reactive: 0}})));
  render(<NowPanel active/>);
  await waitFor(() => expect(screen.getByText('No routine review has derived a colony stage yet.')).toBeInTheDocument());
  expect(screen.getByText('No active goal has filed a progress record yet.')).toBeInTheDocument();
  expect(screen.getByTestId('now-stop')).toHaveTextContent('No window has stopped on this launch.');
});

it('keeps the last good reading through a failed refresh and hides itself on 404', async () => {
  let calls = 0;
  vi.stubGlobal('fetch', vi.fn(() => {calls++; return calls === 1 ? reply(now) : reply({code: 'unavailable', detail: 'Controller data is unavailable'}, 503);}));
  render(<NowPanel active/>);
  await waitFor(() => expect(screen.getByTestId('now-stage')).toHaveTextContent('Stage Reserves'));
  // The next poll is one nowInterval away, so this waits past it.
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Stale — the last good reading. Controller data is unavailable'), {timeout: nowInterval + 3000});
  expect(screen.getByTestId('now-stage')).toHaveTextContent('Stage Reserves');
  cleanup();
  vi.unstubAllGlobals();
  vi.stubGlobal('fetch', vi.fn(() => reply({code: 'not_found', detail: 'Routine diagnostics are not enabled'}, 404)));
  const {container} = render(<NowPanel active/>);
  await waitFor(() => expect(container).toBeEmptyDOMElement());
});

it('rejects a malformed panel rather than showing an invented fact', () => {
  expect(() => readNow({...now, pacing: {...now.pacing, reason: 'whatever'}})).toThrow(/Unknown pacing reason/);
  expect(() => readNow({...now, goals: [{...now.goals[0], nextReview: 1}]})).toThrow(/Inconsistent/);
  expect(() => readNow({...now, goals: [now.goals[0], now.goals[0]]})).toThrow(/Duplicate/);
  expect(() => readNow({...now, stops: {stops: 1, budget: 0}})).toThrow(/Missing spectator field reactive/);
  const {lastStop: _omitted, ...withoutStop} = now;
  expect(() => readNow(withoutStop)).toThrow(/Missing spectator field lastStop/);
});

it('reads a stop that carries only the legs it observed', () => {
  expect(stopReason('STOP_REASON_TICK_BUDGET')).toBe('tick budget');
  expect(stopLegs({...stop, detectTicks: null, occurrenceTick: null, observeMs: null, readmitMs: null})).toEqual(['10 ticks to stop', '40 ms to the step that acted']);
});
