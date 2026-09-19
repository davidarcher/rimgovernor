// Read-only view of /api/telemetry/events and /api/telemetry/metrics (#299):
// the flight-recorder rows serve keeps under the profile and the live
// metrics block. Context and payload are the recorder's free-form objects;
// the readers type the envelope and leave those as records.

export type Json = Record<string, unknown>;
export type TelemetryEvent = {
  sequence: number | null; run: string; wallTime: number; kind: string; context: Json; payload: Json;
  // Gap fields, set on a "recording_gap" row (sequence null).
  reason: string; before: number; after: number;
  // Lower-cased text of the context and payload, for substring filters.
  text: string;
};
export type TelemetryPage = {events: TelemetryEvent[]; nextSince: number; lastSequence: number; more: boolean};
export type Authority = {colony: string; map: number; load: string; plan: string; revision: string; native: string};
export type TelemetryMetrics = {tick: number | null; tps: number; authority: Authority | null; lastStepMs: number; run: string; metrics: Record<string, number>};

export class TelemetryHTTPError extends Error {constructor(public status: number, detail: string) {super(detail);}}

function isObject(v: unknown): v is Json {return typeof v === 'object' && v !== null && !Array.isArray(v);}
function record(v: unknown, what: string): Json {if (!isObject(v)) throw Error(`Invalid telemetry ${what}`); return v;}
function text(v: unknown, what: string): string {if (v === undefined) return ''; if (typeof v !== 'string' || v.includes('\0')) throw Error(`Invalid telemetry ${what}`); return v;}
function finite(v: unknown, what: string): number {if (typeof v !== 'number' || !Number.isFinite(v)) throw Error(`Invalid telemetry ${what}`); return v;}
function sequence(v: unknown, what: string): number {const n = finite(v, what); if (!Number.isInteger(n) || n < 0 || n > Number.MAX_SAFE_INTEGER) throw Error(`Invalid telemetry ${what}`); return n;}
function optionalSequence(v: unknown, what: string): number {return v === undefined ? 0 : sequence(v, what);}
function bool(v: unknown, what: string): boolean {if (typeof v !== 'boolean') throw Error(`Invalid telemetry ${what}`); return v;}
function optionalRecord(v: unknown, what: string): Json {return v === undefined || v === null ? {} : record(v, what);}

const searchTextLimit = 16384;
function searchText(context: Json, payload: Json): string {
  let out = '';
  try {out = JSON.stringify(context) + ' ' + JSON.stringify(payload);} catch {out = '';}
  return out.slice(0, searchTextLimit).toLowerCase();
}

export function readTelemetryEvent(value: unknown): TelemetryEvent {
  const v = record(value, 'event');
  const context = optionalRecord(v.context, 'event context'), payload = optionalRecord(v.payload, 'event payload');
  const event: TelemetryEvent = {
    sequence: v.sequence === null || v.sequence === undefined ? null : sequence(v.sequence, 'event sequence'),
    run: text(v.run, 'event run'), wallTime: finite(v.wall_time, 'event wall time'), kind: text(v.kind, 'event kind'), context, payload,
    reason: text(v.reason, 'gap reason'), before: optionalSequence(v.before, 'gap before'), after: optionalSequence(v.after, 'gap after'),
    text: searchText(context, payload),
  };
  if (!event.kind) throw Error('Invalid telemetry event kind');
  if (event.sequence === null && event.kind !== 'recording_gap') throw Error('Telemetry event without a sequence');
  return event;
}

export function readTelemetryPage(value: unknown): TelemetryPage {
  const v = record(value, 'page');
  if (!Array.isArray(v.events) || v.events.length > 1000) throw Error('Invalid telemetry page');
  return {events: v.events.map(readTelemetryEvent), nextSince: sequence(v.next_since, 'next_since'), lastSequence: sequence(v.last_sequence, 'last_sequence'), more: bool(v.more, 'more')};
}

function readAuthority(value: unknown): Authority | null {
  if (value === null || value === undefined) return null;
  const v = record(value, 'authority');
  return {colony: text(v.colony, 'authority colony'), map: finite(v.map, 'authority map'), load: text(v.load, 'authority load'), plan: text(v.plan, 'authority plan'), revision: text(v.revision, 'authority revision'), native: text(v.native, 'authority native')};
}

export function readTelemetryMetrics(value: unknown): TelemetryMetrics {
  const v = record(value, 'metrics');
  const metrics: Record<string, number> = {};
  for (const [name, raw] of Object.entries(record(v.metrics, 'metrics block'))) metrics[name] = finite(raw, `metric ${name}`);
  return {
    tick: v.tick === null || v.tick === undefined ? null : sequence(v.tick, 'tick'), tps: finite(v.tps, 'tps'), authority: readAuthority(v.authority),
    lastStepMs: finite(v.last_step_ms, 'last_step_ms'), run: text(v.run, 'run'), metrics,
  };
}

async function get(path: string, signal: AbortSignal): Promise<unknown> {
  const response = await fetch(path, {signal});
  if (!response.ok) {
    let detail = `Telemetry unavailable (${response.status})`;
    try {const body: unknown = await response.json(); if (isObject(body) && typeof body.detail === 'string') detail = body.detail;} catch { /* keep the status text */ }
    throw new TelemetryHTTPError(response.status, detail);
  }
  return response.json();
}

export type EventsQuery = {since?: number; kinds?: readonly string[]; limit?: number};
export async function fetchTelemetryEvents(query: EventsQuery, signal: AbortSignal): Promise<TelemetryPage> {
  const params = new URLSearchParams();
  if (query.since !== undefined && query.since > 0) params.set('since', String(query.since));
  if (query.kinds !== undefined && query.kinds.length > 0) params.set('kind', query.kinds.join(','));
  if (query.limit !== undefined) params.set('limit', String(query.limit));
  const search = params.toString();
  return readTelemetryPage(await get('/api/telemetry/events' + (search ? '?' + search : ''), signal));
}
export async function fetchTelemetryMetrics(signal: AbortSignal): Promise<TelemetryMetrics> {
  return readTelemetryMetrics(await get('/api/telemetry/metrics', signal));
}

// traceIdOf is the row's trace (#298), or '' for a row recorded without one.
export function traceIdOf(event: TelemetryEvent): string {const id = event.context.trace_id; return typeof id === 'string' ? id : '';}
export function spanIdOf(event: TelemetryEvent): string {const id = event.context.span_id; return typeof id === 'string' ? id : '';}
export function parentIdOf(event: TelemetryEvent): string {const id = event.context.parent_id; return typeof id === 'string' ? id : '';}
export function tickOf(event: TelemetryEvent): number | null {const tick = event.context.tick; return typeof tick === 'number' && Number.isFinite(tick) ? tick : null;}
export function messageOf(event: TelemetryEvent): string {const msg = event.payload.msg; return typeof msg === 'string' ? msg : '';}
// toolOf is the inner rimgovernor/* method a row concerns: a reply names it
// as native_tool, a request only carries the GABS wrapper and its arguments.
export function toolOf(payload: Json): string {
  const native = payload.native_tool;
  if (typeof native === 'string' && native) return native;
  const tool = payload.tool;
  const args = payload.arguments;
  if (tool === 'games_call_tool' && isObject(args) && typeof args.tool === 'string' && args.tool) return args.tool;
  return typeof tool === 'string' ? tool : '';
}
// scalarText renders a payload value for a summary line; objects as JSON.
export function scalarText(value: unknown): string {
  if (value === null || value === undefined) return 'null';
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean' || typeof value === 'bigint') return String(value);
  try {return JSON.stringify(value) ?? '';} catch {return '…';}
}
