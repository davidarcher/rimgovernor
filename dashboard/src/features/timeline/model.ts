// The case timeline: every stamped fact a case output directory holds,
// placed on one wall-time axis with the game tick mapped beside it.
//
// Sources and what each contributes:
// - `flight.jsonl` (and `service-N/flight.jsonl` for a restarted service):
//   native calls as spans (request row to response row), scheduler steps
//   (`clock_step`: elapsed, reason, stop latency, the window sized), worker
//   dispatches, and the native events the replies carried (windows from
//   `started` to `stopped`, alerts, outcomes, authority changes) at the
//   companion's own `observedAtUnixMs`, with the delivery latency to the
//   reply that carried them.
// - `service*/stderr.log`: unstamped, aligned to the `clock_step` rows by
//   index (see stderr.ts) for admissions, refusals and holds.
// - `result.json`: the case span (`started_at`, `finished_at`, `boot_ms`)
//   and the scalars the summary shows.
// - `service*/http-NNNN.json` and `NNNN-*.json` evidence: unstamped today;
//   placed when a row carries `observed_at` (#296), otherwise listed.
import {findEvents, nativeToolOf, replyClock, replyOf, requestOf, type Flight, type FlightGap, type FlightRow} from './flight';
import {bool, isObject, num, path, str} from './json';
import {parseStderr, type StderrStep} from './stderr';

export type LaneId = 'clock' | 'steps' | 'admissions' | 'events' | 'calls' | 'harness';
export type Severity = 'info' | 'ok' | 'warn' | 'error';
export type Item = {
  id: string;
  lane: LaneId;
  launch: number;
  kind: string;
  start: number;
  end: number;
  tick: number | null;
  label: string;
  title: string;
  severity: Severity;
  detail: unknown;
  // A stamp the item was delivered at (the reply row), drawn as a thin
  // connector from `end`: the delivery latency of a native event.
  delivered: number | null;
};
export type Launch = {index: number; name: string; first: number; last: number; rowSteps: number; stderrSteps: number; aligned: boolean; gaps: FlightGap[]};
export type TickSample = {wall: number; tick: number};
export type SummaryField = {label: string; value: string};
export type Unplaced = {name: string; detail: unknown};
export type Timeline = {t0: number; t1: number; launches: Launch[]; items: Item[]; ticks: TickMap; summary: SummaryField[]; error: string | null; notes: string[]; unplaced: Unplaced[]};

export type LaunchFiles = {name: string; flight: Flight; stderr: string | null; http: {name: string; body: unknown}[]};
export type CaseFiles = {result: unknown; launches: LaunchFiles[]; evidence: {name: string; body: unknown}[]};

export const lanes: {id: LaneId; label: string}[] = [
  {id: 'harness', label: 'Harness'},
  {id: 'clock', label: 'Clock'},
  {id: 'steps', label: 'Steps'},
  {id: 'admissions', label: 'Admissions'},
  {id: 'events', label: 'Events'},
  {id: 'calls', label: 'Native calls'},
];

// Event oneof keys (clock.proto Event) that are not the window boundary.
const eventKinds: Record<string, Severity> = {
  speedChanged: 'info', notification: 'info', alert: 'warn', injuryObserved: 'warn', hostilesCleared: 'info',
  pauseFailed: 'error', forcePauseWaiting: 'warn', forcePauseCleared: 'info', operationOutcome: 'ok', authorityChanged: 'warn', observationInvalidated: 'info',
};
const stopSeverity = (reason: string): Severity => reason === 'STOP_REASON_TICK_BUDGET' ? 'info' : reason === 'STOP_REASON_WATCH_LATCHED' ? 'ok' : reason.endsWith('ERROR') || reason === 'STOP_REASON_UNAVAILABLE' || reason === 'STOP_REASON_LEASE_EXPIRED' ? 'error' : 'warn';
const shortTool = (tool: string) => tool.replace(/^rimgovernor\//, '');
const shortStop = (reason: string) => reason.replace(/^STOP_REASON_/, '').toLowerCase();

export function buildTimeline(files: CaseFiles): Timeline {
  const items: Item[] = [];
  const launches: Launch[] = [];
  const notes: string[] = [];
  const unplaced: Unplaced[] = [];
  const samples: TickSample[] = [];
  const paused: {wall: number; paused: boolean}[] = [];
  const counts = {steps: 0, reasons: new Map<string, number>(), stops: 0, latency: [] as number[], calls: 0, errors: 0, admits: 0, refusals: 0, dispatches: 0, dispatchRefused: 0, events: 0, gaps: 0};
  let t0 = Infinity;
  let t1 = -Infinity;
  const span = (a: number, b: number) => {t0 = Math.min(t0, a); t1 = Math.max(t1, b);};

  files.launches.forEach((launch, index) => {
    const rows = launch.flight.rows;
    const log = launch.stderr === null ? null : parseStderr(launch.stderr);
    const rowSteps = rows.filter(r => r.kind === 'clock_step').length;
    const stderrSteps = log?.steps.length ?? 0;
    const aligned = log !== null && stderrSteps === rowSteps && rowSteps > 0;
    if (log !== null && !aligned) notes.push(`${launch.name}: ${stderrSteps} stderr step blocks vs ${rowSteps} clock_step rows; refusals from stderr are not placed.`);
    counts.gaps += launch.flight.gaps.length;
    let first = Infinity;
    let last = -Infinity;
    const requests = new Map<number, {row: FlightRow; tool: string; poll: boolean}>();
    const seen = new Set<string>();
    let window: {start: number; tick: number | null; detail: unknown} | null = null;
    let stepIndex = 0;
    const id = (kind: string, n: number) => `${index}:${kind}:${n}`;
    const wallOf = (event: Record<string, unknown>, fallback: number) => {const ms = num(event.observedAtUnixMs); return ms === null ? fallback : ms / 1000;};

    for (const row of rows) {
      first = Math.min(first, row.wallTime);
      last = Math.max(last, row.wallTime);
      switch (row.kind) {
        case 'native_request': {
          const request = requestOf(row);
          const poll = request !== null && deepNumber(request, 'waitMs') > 0;
          requests.set(row.sequence, {row, tool: nativeToolOf(row), poll});
          break;
        }
        case 'native_response':
        case 'native_error': {
          const seq = num(row.payload.request);
          const request = seq === null ? undefined : requests.get(seq);
          if (seq !== null) requests.delete(seq);
          const tool = nativeToolOf(row) || request?.tool || '?';
          const wrapper = str(row.payload.tool) ?? '';
          const error = row.kind === 'native_error';
          const start = request?.row.wallTime ?? row.wallTime;
          counts.calls++;
          if (error) counts.errors++;
          const kind = error ? 'error' : wrapper === 'games_tool_detail' ? 'schema' : request?.poll ? 'poll' : 'call';
          const timing = isObject(row.payload.timing) ? row.payload.timing : null;
          const ms = ((row.wallTime - start) * 1000).toFixed(0);
          items.push({id: id('call', row.sequence), lane: 'calls', launch: index, kind, start, end: row.wallTime, tick: null, label: shortTool(tool), title: `${shortTool(tool)} ${ms}ms${error ? ' error' : ''}${request?.poll ? ' (long poll)' : ''}`, severity: error ? 'error' : kind === 'poll' ? 'info' : 'info', detail: {tool, wrapper, request: request?.row.payload.arguments, timing, error: row.payload.error, truncated: row.payload.truncated}, delivered: null});
          const reply = replyOf(row);
          if (reply === null) break;
          const clock = replyClock(reply);
          if (clock.tick !== null) samples.push({wall: row.wallTime, tick: clock.tick});
          if (clock.paused !== null) paused.push({wall: row.wallTime, paused: clock.paused});
          if (tool.startsWith('rimgovernor/clock_') && !tool.startsWith('rimgovernor/clock_read_')) {
            const outcome = Object.keys(reply)[0] ?? '';
            const ok = outcome === 'receipt';
            const refused = outcome === 'failure';
            if (ok) counts.admits++; else if (refused) counts.refusals++;
            const arg = request === null || request === undefined ? null : requestOf(request.row);
            const speed = str(path(arg, 'speed'))?.replace(/^SPEED_/, '').toLowerCase() ?? '';
            items.push({id: id('control', row.sequence), lane: 'admissions', launch: index, kind: ok ? 'admit' : refused ? 'refuse' : 'uncertain', start: row.wallTime, end: row.wallTime, tick: clock.tick, label: `${shortTool(tool).replace(/^clock_/, '')}${speed ? ' ' + speed : ''}`, title: `${shortTool(tool)} → ${outcome}`, severity: ok ? 'ok' : refused ? 'error' : 'warn', detail: {request: arg, reply}, delivered: null});
          }
          for (const event of findEvents(reply)) {
            const cursor = str(event.cursor) ?? (num(event.cursor)?.toString() ?? null);
            const key = cursor ?? `row-${row.sequence}-${String(event.observedAtUnixMs)}`;
            if (seen.has(key)) continue;
            seen.add(key);
            const at = wallOf(event, row.wallTime);
            const tick = num(path(event, 'context', 'tick'));
            const detail = str(event.detail) ?? '';
            span(at, row.wallTime);
            if (isObject(event.started)) {
              if (window !== null) items.push(windowItem(id('window', items.length), index, window, at, 'unterminated', 'warn', tick));
              window = {start: at, tick, detail: event};
              continue;
            }
            if (isObject(event.stopped)) {
              const reason = str(event.stopped.reason) ?? 'STOP_REASON_UNSPECIFIED';
              counts.stops++;
              if (window !== null) {items.push(windowItem(id('window', items.length), index, window, at, shortStop(reason), stopSeverity(reason), tick)); window = null;}
              items.push({id: id('stop', items.length), lane: 'clock', launch: index, kind: 'stop', start: at, end: at, tick, label: shortStop(reason), title: `stop: ${shortStop(reason)} — ${detail}`, severity: stopSeverity(reason), detail: event, delivered: row.wallTime});
              continue;
            }
            const kind = Object.keys(eventKinds).find(k => isObject(event[k]));
            if (kind === undefined) continue;
            counts.events++;
            let severity = eventKinds[kind];
            if (kind === 'operationOutcome' && !isObject(path(event, kind, 'completed'))) severity = 'warn';
            const body = event[kind];
            const outcomeKey = isObject(body) ? Object.keys(body).find(k => k !== 'attempt' && k !== 'latchedTick') ?? '' : '';
            const label = kind === 'alert' ? `alert ${str(path(event, 'alert', 'label')) ?? ''}` : kind === 'authorityChanged' ? `authority ${str(path(event, kind, 'previousGeneration')) ?? '?'}→${str(path(event, kind, 'generation')) ?? '?'}` : kind === 'operationOutcome' ? `outcome ${outcomeKey}` : kind.replace(/([A-Z])/g, ' $1').toLowerCase();
            items.push({id: id('event', items.length), lane: 'events', launch: index, kind: `event:${kind}`, start: at, end: at, tick, label, title: `${label} — ${detail}`, severity, detail: event, delivered: row.wallTime});
          }
          break;
        }
        case 'clock_step': {
          counts.steps++;
          const elapsed = num(row.payload.elapsed_ms) ?? 0;
          const reason = str(row.payload.reason) ?? '';
          counts.reasons.set(reason, (counts.reasons.get(reason) ?? 0) + 1);
          const begin = row.wallTime - elapsed / 1000;
          const step = aligned && log !== null ? log.steps[stepIndex] : null;
          stepIndex++;
          const windowTicks = num(row.payload.window_ticks);
          const label = `${reason}${windowTicks !== null ? ` · ${windowTicks}t` : ''}`;
          const stepDetail = step === null ? {row: row.payload} : {row: row.payload, stderr: summarizeStep(step), log: step.lines};
          const stepError = step?.error ?? null;
          const reads = num(row.payload.reads) ?? 0;
          items.push({id: id('step', row.sequence), lane: 'steps', launch: index, kind: 'step', start: begin, end: row.wallTime, tick: step?.tick ?? null, label, title: `step ${reason} ${elapsed.toFixed(0)}ms reads=${reads}${windowTicks !== null ? ` window ${windowTicks} ticks` : ''}${stepError !== null ? ` — ${stepError}` : ''}`, severity: stepError !== null ? 'error' : bool(row.payload.stop) ? 'ok' : 'info', detail: stepDetail, delivered: null});
          const latency = num(row.payload.stop_latency_ms);
          if (bool(row.payload.stop) && latency !== null && latency >= 0) {
            counts.latency.push(latency);
            items.push({id: id('latency', row.sequence), lane: 'steps', launch: index, kind: 'stop_latency', start: begin - latency / 1000, end: begin, tick: null, label: '', title: `stop→step ${latency.toFixed(0)}ms`, severity: latency > 1000 ? 'warn' : 'info', detail: {stop_latency_ms: latency}, delivered: null});
          }
          if (step !== null) {
            if (step.heldMs !== null) items.push({id: id('hold', row.sequence), lane: 'admissions', launch: index, kind: 'hold', start: begin - step.heldMs / 1000, end: begin, tick: step.tick, label: '', title: `pause-bound admissions held ${step.heldMs.toFixed(0)}ms`, severity: 'info', detail: summarizeStep(step), delivered: null});
            if (step.admitted === false) {counts.refusals++; items.push({id: id('refusal', row.sequence), lane: 'admissions', launch: index, kind: 'refuse', start: row.wallTime, end: row.wallTime, tick: step.tick, label: step.refused.join(',') || 'not admitted', title: `window refused: ${step.refused.join(', ') || 'no reason'}`, severity: step.refused.length === 1 && step.refused[0] === 'no_work' ? 'info' : 'warn', detail: summarizeStep(step), delivered: null});}
            if (step.deferred) items.push({id: id('defer', row.sequence), lane: 'admissions', launch: index, kind: 'defer', start: row.wallTime, end: row.wallTime, tick: step.tick, label: 'deferred', title: 'admission deferred: undispatched work awaits the worker', severity: 'info', detail: summarizeStep(step), delivered: null});
            for (const planner of step.planners) if (planner.reason !== 'admitted' && planner.reason !== 'shared_admission_refused') items.push({id: id(`planner-${planner.planner}`, row.sequence), lane: 'admissions', launch: index, kind: 'planner', start: row.wallTime, end: row.wallTime, tick: step.tick, label: `${planner.planner}: ${planner.reason}`, title: `${planner.planner}.step ${planner.reason}${planner.plan ? ' ' + planner.plan : ''}`, severity: 'info', detail: planner, delivered: null});
          }
          break;
        }
        case 'worker_dispatch': {
          counts.dispatches++;
          const receipt = str(row.payload.receipt) ?? '-';
          const refused = receipt === 'refused';
          if (refused) counts.dispatchRefused++;
          const action = str(row.payload.action) ?? '?';
          items.push({id: id('dispatch', row.sequence), lane: 'admissions', launch: index, kind: 'dispatch', start: row.wallTime, end: row.wallTime, tick: null, label: `${action.replace(/-[0-9a-f]{16,}.*$/, '')} ${receipt}`, title: `dispatch ${action} attempt ${num(row.payload.attempt) ?? '?'} → ${receipt}${bool(row.payload.running) ? ' (live)' : ''}${str(row.payload.error) ? ' — ' + str(row.payload.error) : ''}`, severity: refused ? 'error' : str(row.payload.error) ? 'warn' : 'ok', detail: row.payload, delivered: null});
          break;
        }
        default:
          break;
      }
    }
    if (window !== null && Number.isFinite(last)) items.push(windowItem(id('window', items.length), index, window, last, 'open at end', 'info', null));
    for (const pending of requests.values()) {
      items.push({id: id('unanswered', pending.row.sequence), lane: 'calls', launch: index, kind: 'error', start: pending.row.wallTime, end: last, tick: null, label: shortTool(pending.tool), title: `${shortTool(pending.tool)} never answered`, severity: 'error', detail: pending.row.payload, delivered: null});
    }
    for (const http of launch.http) placeOrList(items, unplaced, 'harness', index, `${launch.name}/${http.name}`, http.body, `${str(path(http.body, 'method')) ?? ''} ${str(path(http.body, 'path')) ?? http.name}`.trim());
    if (Number.isFinite(first)) span(first, last);
    launches.push({index, name: launch.name, first, last, rowSteps, stderrSteps, aligned, gaps: launch.flight.gaps});
    if (log !== null && log.steps.length === 0 && launch.stderr !== null && launch.stderr.trim() !== '') notes.push(`${launch.name}: stderr holds no clock-scheduler step blocks.`);
  });

  // Paused strip: the wall time between consecutive status samples the
  // earlier sample said was paused (bridge.ClockSample's weighting).
  paused.sort((a, b) => a.wall - b.wall);
  let pausedSecs = 0;
  let sampledSecs = 0;
  for (let i = 0; i + 1 < paused.length; i++) {
    const width = paused[i + 1].wall - paused[i].wall;
    if (width <= 0) continue;
    sampledSecs += width;
    if (!paused[i].paused) continue;
    pausedSecs += width;
    items.push({id: `paused:${i}`, lane: 'clock', launch: -1, kind: 'paused', start: paused[i].wall, end: paused[i + 1].wall, tick: null, label: '', title: `clock paused ${(width * 1000).toFixed(0)}ms (between status samples)`, severity: 'info', detail: {from: paused[i], to: paused[i + 1]}, delivered: null});
  }

  // Harness span from result.json.
  const result = isObject(files.result) ? files.result : null;
  const startedAt = isoSeconds(result?.started_at);
  const finishedAt = isoSeconds(result?.finished_at);
  if (startedAt !== null) {
    span(startedAt, startedAt);
    items.push({id: 'case:start', lane: 'harness', launch: -1, kind: 'case', start: startedAt, end: startedAt, tick: null, label: 'case start', title: `case started ${String(result?.started_at)}`, severity: 'info', detail: {started_at: result?.started_at}, delivered: null});
    const boot = num(result?.boot_ms);
    if (boot !== null && boot > 0) items.push({id: 'case:boot', lane: 'harness', launch: -1, kind: 'boot', start: startedAt, end: startedAt + boot / 1000, tick: null, label: 'boot', title: `boot ${boot}ms`, severity: 'info', detail: {boot_ms: boot}, delivered: null});
  }
  if (finishedAt !== null) {
    span(finishedAt, finishedAt);
    const passed = bool(result?.passed);
    items.push({id: 'case:end', lane: 'harness', launch: -1, kind: 'case', start: finishedAt, end: finishedAt, tick: null, label: passed === true ? 'passed' : passed === false ? 'failed' : 'finished', title: `case finished ${String(result?.finished_at)}${str(result?.error) ? ' — ' + (str(result?.error) ?? '') : ''}`, severity: passed === true ? 'ok' : passed === false ? 'error' : 'info', detail: {finished_at: result?.finished_at, passed, error: result?.error}, delivered: null});
  }
  for (const checkpoint of Array.isArray(result?.checkpoints) ? result.checkpoints : []) placeOrList(items, unplaced, 'harness', -1, 'checkpoint', checkpoint, `checkpoint ${str(path(checkpoint, 'label')) ?? str(path(checkpoint, 'save')) ?? ''}`.trim());
  for (const evidence of files.evidence) placeOrList(items, unplaced, 'harness', -1, evidence.name, evidence.body, evidence.name);

  if (!Number.isFinite(t0)) {t0 = 0; t1 = 1;}
  if (t1 <= t0) t1 = t0 + 1;
  items.sort((a, b) => a.start - b.start || a.end - b.end);
  const ticks = buildTickMap(samples);
  const summary = summarize(result, counts, ticks, pausedSecs, sampledSecs, t1 - t0, launches);
  return {t0, t1, launches, items, ticks, summary, error: str(result?.error), notes, unplaced};
}

function windowItem(id: string, launch: number, window: {start: number; tick: number | null; detail: unknown}, end: number, stop: string, severity: Severity, endTick: number | null): Item {
  // EpochStarted carries the Epoch, whose startTick and tickDeadline are
  // the window's budget.
  const startTick = num(path(window.detail, 'started', 'epoch', 'startTick'));
  const deadline = num(path(window.detail, 'started', 'epoch', 'tickDeadline'));
  const ticks = startTick !== null && endTick !== null ? endTick - startTick : null;
  const budget = startTick !== null && deadline !== null ? deadline - startTick : null;
  return {id, lane: 'clock', launch, kind: 'window', start: window.start, end, tick: window.tick, label: `${ticks ?? '?'}${budget !== null ? '/' + budget : ''}t · ${stop}`, title: `window ${((end - window.start) * 1000).toFixed(0)}ms, ${ticks ?? '?'} of ${budget ?? '?'} ticks, stopped: ${stop}`, severity, detail: window.detail, delivered: null};
}

function summarizeStep(step: StderrStep): Record<string, unknown> {
  return {tick: step.tick, running: step.running, stopped: step.stopped, stopReason: step.stopReason, reason: step.reason, planners: step.planners, window: step.window, admitted: step.admitted, refused: step.refused, mode: step.mode, deferred: step.deferred, heldMs: step.heldMs, gateWaitMs: step.gateWaitMs, elapsedMs: step.elapsedMs, error: step.error};
}

// placeOrList puts an unstamped harness row on the axis when it carries a
// stamp (`observed_at`/`observedAt` RFC 3339, or `wall_time` seconds),
// otherwise lists it as unplaced.
function placeOrList(items: Item[], unplaced: Unplaced[], lane: LaneId, launch: number, name: string, body: unknown, label: string): void {
  const at = isoSeconds(path(body, 'observed_at')) ?? isoSeconds(path(body, 'observedAt')) ?? num(path(body, 'wall_time'));
  if (at === null) {unplaced.push({name, detail: body}); return;}
  const status = num(path(body, 'status'));
  items.push({id: `harness:${name}`, lane, launch, kind: 'evidence', start: at, end: at, tick: num(path(body, 'tick')), label, title: `${label}${status !== null ? ' → ' + status : ''}`, severity: status !== null && status >= 400 ? 'error' : 'info', detail: body, delivered: null});
}

function isoSeconds(v: unknown): number | null {
  if (typeof v !== 'string') return null;
  const ms = Date.parse(v);
  return Number.isFinite(ms) ? ms / 1000 : null;
}

function deepNumber(v: unknown, key: string, depth = 0): number {
  if (depth > 8 || !isObject(v)) return 0;
  const own = num(v[key]);
  if (own !== null) return own;
  for (const inner of Object.values(v)) {const found = deepNumber(inner, key, depth + 1); if (found > 0) return found;}
  return 0;
}

// TickMap maps wall time to the game tick from the ticks native replies
// carried: piecewise linear between samples, restarting at a reset (the
// tick went backwards: a load or rewind). `cumulative` counts ticks
// advanced since the first sample with resets excluded, the tick axis the
// page can lay the timeline out on.
export type TickMap = {samples: TickSample[]; resets: number; tickAt(wall: number): number | null; cumulativeAt(wall: number): number; wallAtCumulative(cumulative: number): number | null; ticksAdvanced: number};

export function buildTickMap(raw: TickSample[]): TickMap {
  const samples = [...raw].sort((a, b) => a.wall - b.wall).filter((s, i, all) => i === 0 || s.wall > all[i - 1].wall || s.tick !== all[i - 1].tick);
  const cumulative: number[] = [];
  let resets = 0;
  let total = 0;
  for (let i = 0; i < samples.length; i++) {
    if (i > 0) {const delta = samples[i].tick - samples[i - 1].tick; if (delta < 0) resets++; else total += delta;}
    cumulative.push(total);
  }
  const locate = (wall: number): number => {
    let lo = 0;
    let hi = samples.length - 1;
    while (lo < hi) {const mid = (lo + hi + 1) >> 1; if (samples[mid].wall <= wall) lo = mid; else hi = mid - 1;}
    return lo;
  };
  const between = (wall: number, value: (i: number) => number): number | null => {
    if (samples.length === 0) return null;
    if (wall <= samples[0].wall) return value(0);
    const i = locate(wall);
    if (i >= samples.length - 1) return value(samples.length - 1);
    const a = samples[i];
    const b = samples[i + 1];
    if (b.tick < a.tick) return value(i);
    const f = b.wall === a.wall ? 0 : (wall - a.wall) / (b.wall - a.wall);
    return value(i) + f * (value(i + 1) - value(i));
  };
  return {
    samples, resets, ticksAdvanced: total,
    tickAt: wall => between(wall, i => samples[i].tick),
    cumulativeAt: wall => between(wall, i => cumulative[i]) ?? 0,
    // The inverse over the cumulative count: the earliest wall time the
    // count was reached, interpolated within the sample interval that
    // crossed it (paused intervals have zero width on this axis).
    wallAtCumulative: target => {
      if (samples.length === 0) return null;
      if (target <= 0) return samples[0].wall;
      let lo = 0;
      let hi = samples.length - 1;
      while (lo < hi) {const mid = (lo + hi + 1) >> 1; if (cumulative[mid] <= target) lo = mid; else hi = mid - 1;}
      if (lo >= samples.length - 1) return samples[samples.length - 1].wall;
      const span = cumulative[lo + 1] - cumulative[lo];
      const f = span <= 0 ? 0 : (target - cumulative[lo]) / span;
      return samples[lo].wall + f * (samples[lo + 1].wall - samples[lo].wall);
    },
  };
}

type Counts = {steps: number; reasons: Map<string, number>; stops: number; latency: number[]; calls: number; errors: number; admits: number; refusals: number; dispatches: number; dispatchRefused: number; events: number; gaps: number};

function summarize(result: Record<string, unknown> | null, counts: Counts, ticks: TickMap, pausedSecs: number, sampledSecs: number, wallSecs: number, launches: Launch[]): SummaryField[] {
  const fields: SummaryField[] = [];
  const add = (label: string, value: string | number | null | undefined) => {if (value !== null && value !== undefined && value !== '') fields.push({label, value: String(value)});};
  add('case', str(result?.case));
  const passed = bool(result?.passed);
  add('outcome', passed === null ? null : passed ? 'passed' : 'failed');
  add('wall', `${wallSecs.toFixed(1)}s`);
  const wallMs = num(result?.wall_ms);
  if (wallMs !== null) add('case wall', `${(wallMs / 1000).toFixed(1)}s (boot ${num(result?.boot_ms) ?? '?'}ms)`);
  add('ticks', ticks.ticksAdvanced > 0 ? `${ticks.ticksAdvanced}${ticks.resets ? ` (${ticks.resets} resets)` : ''}` : num(result?.ticks_advanced));
  const tps = num(result?.wall_tps);
  add('wall TPS', tps === null ? null : tps.toFixed(1));
  add('paused', sampledSecs > 0 ? `${(100 * pausedSecs / sampledSecs).toFixed(0)}% of ${sampledSecs.toFixed(1)}s sampled` : null);
  add('launches', launches.length > 1 ? launches.length : null);
  add('steps', counts.steps > 0 ? `${counts.steps} (${[...counts.reasons].map(([k, v]) => `${k || '?'}=${v}`).join(' ')})` : null);
  add('stops', counts.stops > 0 ? `${counts.stops}; stop→step latency ${counts.latency.length ? `mean ${mean(counts.latency).toFixed(0)}ms max ${Math.max(...counts.latency).toFixed(0)}ms over ${counts.latency.length}` : 'unsampled'}` : null);
  add('admissions', counts.admits + counts.refusals > 0 ? `${counts.admits} admitted, ${counts.refusals} refused` : null);
  add('dispatches', counts.dispatches > 0 ? `${counts.dispatches} (${counts.dispatchRefused} refused)` : null);
  add('native calls', counts.calls > 0 ? `${counts.calls} (${counts.errors} errors)` : null);
  add('events', counts.events > 0 ? counts.events : null);
  add('recording gaps', counts.gaps > 0 ? counts.gaps : null);
  const waits = isObject(result?.wait_stats) ? result.wait_stats : null;
  if (waits !== null) add('harness waits', `${num(waits.waits) ?? '?'}, longest quiet ${num(waits.max_quiet_ms) ?? '?'}ms${str(waits.max_quiet_signature) ? ` (${str(waits.max_quiet_signature) ?? ''})` : ''}, stalled ${num(waits.stalled) ?? 0}`);
  return fields;
}

function mean(values: number[]): number {return values.reduce((a, b) => a + b, 0) / values.length;}
