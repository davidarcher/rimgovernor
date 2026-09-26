// Read-only view of GET /api/spectator/now (#632): the watcher's one read of
// what the colony is trying to do, why the clock runs as it does and the last
// stop with its latency split. Nulls are unknown facts; nothing is derived
// here that the server did not send, and reading it changes no simulation
// state.

export type PacingReason = 'unknown' | 'governor_off' | 'held' | 'window_refused' | 'running' | 'tick_budget' | 'stopped' | 'cinematic';
export const pacingReasons: PacingReason[] = ['unknown', 'governor_off', 'held', 'window_refused', 'running', 'tick_budget', 'stopped', 'cinematic'];

export type NowStage = {stage: string; since: number; blocker: string; reason: string; held: boolean};
export type NowGoal = {goal: string; method: string; expected: string; lastProgress: number; nextReview: number; blocked: string; prerequisite: string; observed: number | null};
export type NowPacing = {reason: PacingReason; detail: string; mode: string; effectiveTps: number; windowTicks: number};
// The stop's latency split: the tick legs from the hazard arising through the
// supervisor raising the stop to the stop landing, then the wall legs — how
// long it sat unobserved in native, how long the controller took to act on it
// and how long the pause lasted before a window was readmitted.
export type NowStop = {
  reason: string; evidence: string; cursor: number; benign: boolean; tick: number;
  detectedTick: number | null; occurrenceTick: number | null; detectTicks: number | null; stopTicks: number | null;
  observeMs: number | null; actedMs: number | null; readmitMs: number | null;
};
export type NowStops = {stops: number; budget: number; reactive: number};
export type Now = {tick: number | null; stage: NowStage | null; goals: NowGoal[]; pacing: NowPacing; lastStop: NowStop | null; stops: NowStops};

export class SpectatorHTTPError extends Error {constructor(public status: number, detail: string) {super(detail);}}

type Json = Record<string, unknown>;
function isObject(v: unknown): v is Json {return typeof v === 'object' && v !== null && !Array.isArray(v);}
function object(v: unknown, keys: string[]): Json {
  if (!isObject(v)) throw Error('Invalid spectator object');
  for (const key of keys) if (!Object.hasOwn(v, key)) throw Error(`Missing spectator field ${key}`);
  return v;
}
function text(v: unknown): string {if (typeof v !== 'string' || v.includes('\0') || v.length > 4096) throw Error('Invalid spectator text'); return v;}
function finite(v: unknown): number {if (typeof v !== 'number' || !Number.isFinite(v)) throw Error('Invalid spectator number'); return v;}
function bool(v: unknown): boolean {if (typeof v !== 'boolean') throw Error('Invalid spectator flag'); return v;}
function optional(v: unknown): number | null {return v === null || v === undefined ? null : finite(v);}
function tick(v: unknown): number {const n = finite(v); if (!Number.isInteger(n) || n < 0) throw Error('Invalid spectator tick'); return n;}

function readStage(v: unknown): NowStage {
  const s = object(v, ['stage', 'since', 'blocker', 'reason', 'held']);
  return {stage: text(s.stage), since: tick(s.since), blocker: text(s.blocker), reason: text(s.reason), held: bool(s.held)};
}
function readGoal(v: unknown): NowGoal {
  const g = object(v, ['goal', 'method', 'expected', 'lastProgress', 'nextReview', 'blocked', 'prerequisite']);
  const goal = {goal: text(g.goal), method: text(g.method), expected: text(g.expected), lastProgress: tick(g.lastProgress), nextReview: tick(g.nextReview), blocked: text(g.blocked), prerequisite: text(g.prerequisite), observed: optional(g.observed)};
  if (!goal.goal) throw Error('Invalid spectator goal');
  if (goal.nextReview !== 0 && goal.nextReview < goal.lastProgress) throw Error('Inconsistent spectator goal progress');
  return goal;
}
function readPacing(v: unknown): NowPacing {
  const p = object(v, ['reason', 'detail', 'mode', 'effectiveTps', 'windowTicks']);
  const reason = text(p.reason) as PacingReason;
  if (!pacingReasons.includes(reason)) throw Error(`Unknown pacing reason ${reason}`);
  const pacing = {reason, detail: text(p.detail), mode: text(p.mode), effectiveTps: finite(p.effectiveTps), windowTicks: tick(p.windowTicks)};
  if (pacing.effectiveTps < 0 || !pacing.mode) throw Error('Invalid spectator pacing');
  return pacing;
}
function readStop(v: unknown): NowStop {
  const s = object(v, ['reason', 'evidence', 'cursor', 'benign', 'tick']);
  return {
    reason: text(s.reason), evidence: text(s.evidence), cursor: finite(s.cursor), benign: bool(s.benign), tick: tick(s.tick),
    detectedTick: optional(s.detectedTick), occurrenceTick: optional(s.occurrenceTick), detectTicks: optional(s.detectTicks), stopTicks: optional(s.stopTicks),
    observeMs: optional(s.observeMs), actedMs: optional(s.actedMs), readmitMs: optional(s.readmitMs),
  };
}
export function readNow(value: unknown): Now {
  const v = object(value, ['tick', 'stage', 'goals', 'pacing', 'lastStop', 'stops']);
  if (!Array.isArray(v.goals) || v.goals.length > 64) throw Error('Invalid spectator goals');
  const goals = v.goals.map(readGoal);
  if (new Set(goals.map(g => g.goal)).size !== goals.length) throw Error('Duplicate spectator goal');
  const counts = object(v.stops, ['stops', 'budget', 'reactive']);
  return {
    tick: v.tick === null ? null : tick(v.tick), stage: v.stage === null ? null : readStage(v.stage), goals, pacing: readPacing(v.pacing),
    lastStop: v.lastStop === null ? null : readStop(v.lastStop),
    stops: {stops: tick(counts.stops), budget: tick(counts.budget), reactive: tick(counts.reactive)},
  };
}

export async function fetchNow(signal: AbortSignal): Promise<Now> {
  const response = await fetch('/api/spectator/now', {method: 'GET', cache: 'no-store', credentials: 'same-origin', signal});
  if (!response.ok) {
    let detail = `The now panel is unavailable (${response.status})`;
    try {const error = object(await response.json(), ['code', 'detail']); detail = text(error.detail);} catch { /* Retain the local diagnostic for a malformed error. */ }
    throw new SpectatorHTTPError(response.status, detail);
  }
  const source = await response.text();
  if (new TextEncoder().encode(source).length > 262144) throw Error('Spectator response exceeds size bound');
  return readNow(JSON.parse(source) as unknown);
}
