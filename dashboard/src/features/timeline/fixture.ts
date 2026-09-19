// A synthetic case recording for the timeline tests: the row shapes the
// service writes (bridge.FlightRecorder, ReadTally.Publish, the clock
// scheduler's stderr) around one admitted window that a watch latch stops.
export const T0 = 1789779497;

type Row = {sequence: number; wall_time: number; kind: string; context: Record<string, unknown>; payload: Record<string, unknown>};

export function row(sequence: number, offset: number, kind: string, payload: Record<string, unknown>): Row {
  return {sequence, wall_time: T0 + offset, kind, context: {}, payload};
}

export function toolReply(reply: unknown): Record<string, unknown> {
  return {operation: {Success: true}, payload: JSON.stringify(reply)};
}

const context = (tick: number) => ({identity: {colonyId: 'c', loadToken: 'l', mapId: 0}, tick: String(tick), nativeGeneration: '6'});

// bundle is one observations_read_bundle request/response pair carrying an
// events page (optionally a long poll) and the clock status at `tick`.
export function bundle(seq: number, at: number, tick: number, paused: boolean, events: Record<string, unknown>[], waitMs = 0): Row[] {
  const request = {identity: context(tick).identity, events: {afterCursor: '0', limit: 32, waitMs}};
  return [
    row(seq, at, 'native_request', {tool: 'games_call_tool', arguments: {gameId: 'g', tool: 'rimgovernor/observations_read_bundle', arguments: {request: JSON.stringify(request)}}}),
    row(seq + 1, at + 0.05, 'native_response', {request: seq, tool: 'games_call_tool', native_tool: 'rimgovernor/observations_read_bundle', timing: {total_ms: 50}, result: toolReply({observed: {context: context(tick), clockStatus: {context: context(tick), actualPaused: paused}, events: {context: context(tick), events}}})}),
  ];
}

export function started(cursor: number, atMs: number, tick: number, deadline: number): Record<string, unknown> {
  return {cursor: String(cursor), context: context(tick), observedAtUnixMs: String(atMs), detail: 'Supervised play started.', started: {epoch: {owner: {epoch: '1'}, startTick: String(tick), tickDeadline: String(deadline), requestedSpeed: 'SPEED_SUPERFAST'}}};
}
export function stopped(cursor: number, atMs: number, tick: number, reason: string): Record<string, unknown> {
  return {cursor: String(cursor), context: context(tick), observedAtUnixMs: String(atMs), detail: `stop ${reason}`, stopped: {reason}};
}
export function alert(cursor: number, atMs: number, tick: number, label: string): Record<string, unknown> {
  return {cursor: String(cursor), context: context(tick), observedAtUnixMs: String(atMs), detail: label, alert: {key: label, label, priority: 'High'}};
}

// recording is the whole synthetic flight: a start admission at +1s, a
// window from +1.2s (tick 11) to +3.0s (tick 735, watch latched), a step
// woken by that stop, an alert delivered late, and a refused renew.
export function recording(): string {
  const rows: Row[] = [
    row(1, 0, 'coverage', {coverage: 'test'}),
    ...bundle(2, 0.1, 11, true, []),
    row(4, 0.9, 'clock_step', {reads: 2, tools: {'rimgovernor/observations_read_bundle': 1}, schema_fetches: 0, cache_hits: 1, parent_hits: 0, running: false, elapsed_ms: 800, reason: 'full', window_ticks: 2500, window_target_s: 2, window_tps: 360}),
    row(5, 1.0, 'native_request', {tool: 'games_call_tool', arguments: {gameId: 'g', tool: 'rimgovernor/clock_start', arguments: {request: JSON.stringify({speed: 'SPEED_SUPERFAST', tickBudget: 2500})}}}),
    row(6, 1.2, 'native_response', {request: 5, tool: 'games_call_tool', native_tool: 'rimgovernor/clock_start', timing: {total_ms: 200}, result: toolReply({receipt: {applied: {status: {context: context(11), running: {epoch: {epoch: '1'}}}}}})}),
    ...bundle(7, 1.3, 11, false, [started(1, (T0 + 1.2) * 1000, 11, 2511)], 4000),
    ...bundle(9, 2.0, 400, false, []),
    ...bundle(11, 3.1, 735, true, [alert(2, (T0 + 2.5) * 1000, 600, 'NeedFood'), stopped(3, (T0 + 3.0) * 1000, 735, 'STOP_REASON_WATCH_LATCHED')]),
    row(13, 3.7, 'clock_step', {reads: 1, tools: {}, schema_fetches: 0, cache_hits: 0, parent_hits: 0, running: false, elapsed_ms: 500, reason: 'wake', stop: true, stop_latency_ms: 200}),
    row(14, 3.8, 'worker_dispatch', {reads: 1, tools: {}, schema_fetches: 0, action: 'routine-haul-abcdef0123456789abcdef0123456789-0', attempt: 1, stage: 'awaiting_observation', receipt: 'accepted', running: false}),
    row(15, 4.0, 'native_request', {tool: 'games_call_tool', arguments: {gameId: 'g', tool: 'rimgovernor/clock_renew', arguments: {request: '{}'}}}),
    row(16, 4.1, 'native_response', {request: 15, tool: 'games_call_tool', native_tool: 'rimgovernor/clock_renew', timing: {total_ms: 100}, result: toolReply({failure: {code: 'FAILURE_CODE_STALE', detail: 'window stopped'}})}),
    row(17, 4.2, 'native_request', {tool: 'games_call_tool', arguments: {gameId: 'g', tool: 'rimgovernor/lifecycle_read_tick', arguments: {request: '{}'}}}),
    row(18, 4.3, 'native_error', {request: 17, tool: 'games_call_tool', native_tool: 'rimgovernor/lifecycle_read_tick', error: 'bridge transport failure', timing: {total_ms: 100}}),
  ];
  return rows.map(r => JSON.stringify({version: 1, run: 'r', ...r})).join('\n') + '\n';
}

// stderrLog is the scheduler log aligned with the two clock_step rows.
export const stderrLog = [
  '[clock-scheduler] step waited 56ms for the player gate',
  '[clock-scheduler] status: running=false stopping=false stopped=false neverStarted=true stopReason=STOP_REASON_UNSPECIFIED tick=11 tickAdvanced=false',
  '[clock-scheduler] step reason: full tick_advanced planners=true',
  '[clock-scheduler] routine.step: ReviewRoutine ok newRevision=1 emergency=[]',
  '[clock-scheduler] Haul.step result: reason=admitted plan=routine-haul-1',
  '[clock-scheduler] colony window: 2500 ticks (target 2.0s at 360 ticks/s, pause estimate known=false 0.0s, observed rate known=false 0 ticks/s)',
  '[clock-scheduler] EvaluateClockWindow: work=true combatPlan=false admitted=true mode=colony hostiles=[] refused=[] watched=1',
  '[clock-scheduler] step reads: total=6 observations_read_bundle=2 clock_start=1 cache hits=7 misses=2 coalesced=0 parent_hits=0 invalidations=2 running=false elapsed=800ms',
  '[clock-scheduler] step done: err=<nil> planner failures=<nil>',
  '[clock-scheduler] stop committed: pause-bound admissions held 114ms before the review',
  '[clock-scheduler] status: running=false stopping=false stopped=true neverStarted=false stopReason=STOP_REASON_WATCH_LATCHED tick=735 tickAdvanced=true',
  '[clock-scheduler] step reason: wake tick_advanced stopped routine-haul-1 planners=true',
  '[clock-scheduler] Haul.step result: reason=no_active_deficit plan=',
  '[clock-scheduler] colony window: 2500 ticks (target 2.0s at 90 ticks/s, pause estimate known=true 1.2s, observed rate known=true 90 ticks/s)',
  '[clock-scheduler] EvaluateClockWindow: work=false combatPlan=false admitted=false mode= hostiles=[] refused=[no_work] watched=0',
  '[clock-scheduler] step reads: total=2 observations_read_bundle=2 cache hits=6 misses=0 coalesced=0 parent_hits=0 invalidations=0 running=false elapsed=1.5s',
  '[clock-scheduler] step done: err=building execution held planner failures=<nil>',
  '[clock-worker] step failed: building execution held',
  '[worker] routine-haul-1-0 stage=pending attempt=0 receipt=- effect=- refused=[hauler_unavailable] err=building execution held',
].join('\n') + '\n';

export const result = {case: 'routinehaul/storage', passed: true, started_at: new Date((T0 - 5) * 1000).toISOString(), finished_at: new Date((T0 + 6) * 1000).toISOString(), boot_ms: 4000, wall_ms: 11000, ticks_advanced: 724, wall_tps: 65.8, wait_stats: {waits: 3, max_quiet_ms: 1200, max_quiet_signature: 'goal|x', stalled: 0}, checkpoints: []};
