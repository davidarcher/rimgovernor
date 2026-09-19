// Fixture rows shared by the governor tests: one traced scheduler step
// and one untraced clock_step, as /api/telemetry/events serves them.
export const T = 'a1b2c3d4e5f60718', root = 'span-root', child = 'span-child';
export const ctx = (span: string, parent = '', extra: Record<string, unknown> = {}) => ({trace_id: T, span_id: span, parent_id: parent, ...extra});
const row = (sequence: number, wall: number, kind: string, context: Record<string, unknown>, payload: Record<string, unknown>) => ({sequence, run: 'run-1', wall_time: 1000 + wall / 1000, kind, context, payload});

// One scheduler step: a cached read, a timed native read with the
// companion's split, a refused call under a worker dispatch, the step row.
export const stepRows = [
  row(10, 0, 'native_cache_hit', ctx(root), {native_tool: 'rimgovernor/lifecycle_read_tick', tool: 'games_call_tool'}),
  row(11, 5, 'native_request', ctx(root), {tool: 'games_call_tool', arguments: {tool: 'rimgovernor/observations_read_bundle'}}),
  row(12, 105, 'native_decode', ctx(root), {native_tool: 'rimgovernor/observations_read_bundle', request: 11}),
  row(13, 106, 'native_response', ctx(root), {request: 11, tool: 'games_call_tool', native_tool: 'rimgovernor/observations_read_bundle', result: {}, timing: {gate_wait_ms: 10, call_ms: 80, decode_ms: 5, total_ms: 100, native_queue_ms: 20, native_execute_ms: 50, native_trace: `${T}/${root}`}}),
  row(14, 120, 'worker_dispatch', ctx(child, root), {action: 'routine-acquire-1', receipt: 'refused', reads: 1}),
  row(15, 125, 'native_request', ctx(child, root), {tool: 'games_call_tool', arguments: {tool: 'rimgovernor/operations_execute'}}),
  row(16, 145, 'native_error', ctx(child, root), {request: 15, tool: 'games_call_tool', native_tool: 'rimgovernor/operations_execute', error: 'refused: stale_facts', timing: {gate_wait_ms: 0, call_ms: 18, decode_ms: 1, total_ms: 20}}),
  row(17, 200, 'scheduler_step', ctx(root, '', {tick: 29900, component: 'clock-worker'}), {msg: 'step done', admitted: true, cause: 'live', window_ticks: 250}),
];
export const untraced = row(9, -50, 'clock_step', {}, {elapsed_ms: 3456, reads: 7});
