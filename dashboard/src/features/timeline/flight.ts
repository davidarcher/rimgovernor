// Flight-recorder rows as `bridge.ReadTimeline` reads them: one JSON line per
// native request, response, error, cache hit, decode, scheduler step and
// worker dispatch, with rotated segments (`flight.jsonl.N`, higher N older)
// before the active file. Corrupt lines and sequence discontinuities become
// gaps, not rows. Payloads are kept as decoded `unknown` and read through
// the guards in `json.ts`; nothing here trusts a row's shape.
import {isObject, num, str} from './json';

export type FlightRow = {kind: string; sequence: number; wallTime: number; context: Record<string, unknown>; payload: Record<string, unknown>};
export type FlightGap = {reason: string; before: number; after: number; line: number};
export type Flight = {rows: FlightRow[]; gaps: FlightGap[]};

// segmentOrder sorts the segment names of one recording oldest first: the
// highest rotated index, down to `.1`, then the active file itself.
export function segmentOrder(base: string, names: readonly string[]): string[] {
  const indexed: {name: string; index: number}[] = [];
  for (const name of names) {
    if (name === base) {indexed.push({name, index: -1}); continue;}
    if (!name.startsWith(base + '.')) continue;
    const suffix = name.slice(base.length + 1);
    if (!/^\d+$/.test(suffix)) continue;
    indexed.push({name, index: Number(suffix)});
  }
  return indexed.sort((a, b) => b.index - a.index).map(s => s.name);
}

// parseFlight decodes the segments' text in the order segmentOrder gave,
// keeping every well-formed row and recording a gap for each corrupt line
// or sequence discontinuity (retention rotated a segment away, or a write
// was lost).
export function parseFlight(segments: readonly string[]): Flight {
  const rows: FlightRow[] = [];
  const gaps: FlightGap[] = [];
  let previous = 0;
  let havePrevious = false;
  let line = 0;
  for (const text of segments) {
    for (const raw of text.split('\n')) {
      line++;
      if (raw.trim() === '') continue;
      let decoded: unknown;
      try {decoded = JSON.parse(raw);} catch {gaps.push({reason: 'Incomplete or corrupt record', before: 0, after: previous, line}); continue;}
      if (!isObject(decoded)) {gaps.push({reason: 'Incomplete or corrupt record', before: 0, after: previous, line}); continue;}
      const sequence = num(decoded.sequence);
      const wallTime = num(decoded.wall_time);
      const kind = str(decoded.kind);
      if (sequence === null || wallTime === null || kind === null || !isObject(decoded.context) || !isObject(decoded.payload)) {
        gaps.push({reason: 'Incomplete or corrupt record', before: 0, after: previous, line});
        continue;
      }
      if ((!havePrevious && sequence !== 1) || (havePrevious && sequence !== previous + 1)) gaps.push({reason: 'Retention or sequence discontinuity', before: sequence, after: previous, line});
      previous = sequence;
      havePrevious = true;
      rows.push({kind, sequence, wallTime, context: decoded.context, payload: decoded.payload});
    }
  }
  return {rows, gaps};
}

// replyOf decodes the ProtoJSON reply a recorded games_call_tool response
// carries (`result.payload`), or null for a truncated row, an error, a
// non-tool response or an undecodable payload.
export function replyOf(row: FlightRow): Record<string, unknown> | null {
  if (row.kind !== 'native_response') return null;
  const result = row.payload.result;
  if (!isObject(result)) return null;
  const payload = result.payload;
  if (typeof payload !== 'string' || payload.length > 4 * 1024 * 1024) return null;
  try {const reply: unknown = JSON.parse(payload); return isObject(reply) ? reply : null;} catch {return null;}
}

// requestOf decodes the ProtoJSON request string a recorded games_call_tool
// request carries (`arguments.arguments.request`), or null.
export function requestOf(row: FlightRow): Record<string, unknown> | null {
  if (row.kind !== 'native_request') return null;
  const outer = row.payload.arguments;
  if (!isObject(outer) || !isObject(outer.arguments) || typeof outer.arguments.request !== 'string') return null;
  try {const request: unknown = JSON.parse(outer.arguments.request); return isObject(request) ? request : null;} catch {return null;}
}

// nativeToolOf is the inner rimgovernor/* method of a games_call_tool row
// (request or response), else the wrapper tool name.
export function nativeToolOf(row: FlightRow): string {
  const native = str(row.payload.native_tool);
  if (native) return native;
  const args = row.payload.arguments;
  if (isObject(args)) {const inner = str(args.tool); if (inner) return inner;}
  return str(row.payload.tool) ?? '';
}

// findEvents returns every object in an "events" array anywhere in a decoded
// reply, in document order: a clock_read_events page or the events section
// of a bundle.
export function findEvents(value: unknown, out: Record<string, unknown>[] = [], depth = 0): Record<string, unknown>[] {
  if (depth > 12) return out;
  if (Array.isArray(value)) {for (const item of value) findEvents(item, out, depth + 1); return out;}
  if (!isObject(value)) return out;
  for (const key of Object.keys(value).sort()) {
    const inner = value[key];
    if (key === 'events' && Array.isArray(inner)) {for (const item of inner) if (isObject(item)) out.push(item); continue;}
    findEvents(inner, out, depth + 1);
  }
  return out;
}

export type ClockSample = {tick: number | null; paused: boolean | null};

// replyClock reads the observation tick and the actual-paused flag out of a
// decoded reply the way `bridge.replyClock` does: the tick at
// <outcome>.context.tick, the paused state from a status body (top level,
// a bundle's clockStatus section, or a control receipt's applied status).
export function replyClock(reply: Record<string, unknown>): ClockSample {
  let tick: number | null = null;
  let paused: boolean | null = null;
  for (const outcome of Object.values(reply)) {
    if (!isObject(outcome)) continue;
    if (isObject(outcome.context)) {const t = num(outcome.context.tick); if (t !== null) tick = t;}
    const own = statusPaused(outcome);
    if (own !== null) paused = own;
    if (isObject(outcome.clockStatus)) {const p = statusPaused(outcome.clockStatus); if (p !== null) paused = p;}
    if (isObject(outcome.applied) && isObject(outcome.applied.status)) {const p = statusPaused(outcome.applied.status); if (p !== null) paused = p;}
  }
  return {tick, paused};
}

// statusPaused mirrors bridge.statusPaused: the actual-paused flag when the
// status carries one, else its running/stopped state.
function statusPaused(body: Record<string, unknown>): boolean | null {
  if (typeof body.actualPaused === 'boolean') return body.actualPaused;
  if (isObject(body.running)) return false;
  if (isObject(body.stopped)) return true;
  return null;
}
