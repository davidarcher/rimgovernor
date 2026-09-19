// One trace as a waterfall, the browser twin of bridge.WriteTraceReport
// (go/internal/bridge/flightrecorder_trace.go): the rows sharing a trace_id
// in sequence order, each native call folded into one line with the phases
// its reply reported, every other row a line of its own.
import {messageOf, parentIdOf, scalarText, spanIdOf, tickOf, toolOf, traceIdOf, type Json, type TelemetryEvent} from './telemetryData';

export type Phases = {gateWaitMs: number; callMs: number; decodeMs: number; totalMs: number; nativeQueueMs: number | null; nativeExecuteMs: number | null; echoed: string};
export type TraceLine = {
  sequence: number; kind: string; offsetMs: number; durationMs: number | null; span: string; depth: number; text: string;
  phases: Phases | null; error: string; attrs: [string, string][];
};
export type Trace = {traceId: string; rows: TelemetryEvent[]; tick: number | null; spanMs: number; root: string; lines: TraceLine[]};

function num(v: unknown): number | null {return typeof v === 'number' && Number.isFinite(v) ? v : null;}
function str(v: unknown): string {return typeof v === 'string' ? v : '';}
function field(timing: Json, key: string): number {return num(timing[key]) ?? 0;}

export function shortId(id: string): string {return id.length > 8 ? id.slice(0, 8) : id;}

// traceRows is the trace's rows in sequence order (the feed holds them
// newest first), or none when nothing under that id is retained.
export function traceRows(events: readonly TelemetryEvent[], traceId: string): TelemetryEvent[] {
  if (!traceId) return [];
  return events.filter(e => e.sequence !== null && traceIdOf(e) === traceId).sort((a, b) => (a.sequence ?? 0) - (b.sequence ?? 0));
}

// traceRoot names the trace by what it was: the last scheduler_step message,
// else the last worker_outcome, else the last non-native row, else the tool.
function traceRoot(rows: TelemetryEvent[]): string {
  for (const kind of ['scheduler_step', 'worker_outcome']) {
    for (let i = rows.length - 1; i >= 0; i--) if (rows[i].kind === kind) {const msg = messageOf(rows[i]); return msg ? `${kind}: ${msg}` : kind;}
  }
  for (let i = rows.length - 1; i >= 0; i--) if (!rows[i].kind.startsWith('native_')) return rows[i].kind;
  for (const row of rows) {const tool = toolOf(row.payload); if (tool && row.kind !== 'native_request') return 'native ' + tool;}
  return rows[rows.length - 1].kind;
}

// traceDepths nests each span under the root by following parent_id; a span
// whose parent is outside the trace sits at depth 0.
function traceDepths(rows: TelemetryEvent[]): Map<string, number> {
  const parents = new Map<string, string>();
  for (const row of rows) {const span = spanIdOf(row); if (span) parents.set(span, parentIdOf(row));}
  const depths = new Map<string, number>();
  for (const span of parents.keys()) {
    let depth = 0;
    for (let cursor = parents.get(span) ?? ''; cursor && depth < 16; cursor = parents.get(cursor) ?? '') {if (!parents.has(cursor)) break; depth++;}
    depths.set(span, depth);
  }
  return depths;
}

function readPhases(timing: unknown, traceId: string, span: string): Phases | null {
  if (typeof timing !== 'object' || timing === null || Array.isArray(timing)) return null;
  const t = timing as Json;
  const echoed = str(t.native_trace);
  return {
    gateWaitMs: field(t, 'gate_wait_ms'), callMs: field(t, 'call_ms'), decodeMs: field(t, 'decode_ms'), totalMs: field(t, 'total_ms'),
    nativeQueueMs: num(t.native_queue_ms), nativeExecuteMs: num(t.native_execute_ms), echoed: echoed && echoed !== `${traceId}/${span}` ? echoed : '',
  };
}

function attrsOf(payload: Json): [string, string][] {
  return Object.keys(payload).filter(key => key !== 'msg').sort().map(key => {
    const rendered = scalarText(payload[key]);
    return [key, rendered.length > 200 ? rendered.slice(0, 200) + '…' : rendered];
  });
}

export function buildTrace(events: readonly TelemetryEvent[], traceId: string): Trace | null {
  const rows = traceRows(events, traceId);
  if (rows.length === 0) return null;
  const replies = new Map<number, TelemetryEvent>();
  for (const row of rows) if (row.kind === 'native_response' || row.kind === 'native_error') {const request = num(row.payload.request); if (request !== null) replies.set(request, row);}
  const depths = traceDepths(rows);
  const origin = rows[0].wallTime;
  let tick: number | null = null;
  for (const row of rows) {tick = tickOf(row); if (tick !== null) break;}
  const lines: TraceLine[] = [];
  for (const row of rows) {
    const span = spanIdOf(row);
    const line: TraceLine = {sequence: row.sequence ?? 0, kind: row.kind, offsetMs: (row.wallTime - origin) * 1000, durationMs: null, span: shortId(span), depth: depths.get(span) ?? 0, text: '', phases: null, error: '', attrs: []};
    switch (row.kind) {
      case 'native_response': case 'native_error': {
        if (num(row.payload.request) !== null) continue;
        line.text = row.kind + ' ' + toolOf(row.payload);
        break;
      }
      case 'native_request': {
        const reply = replies.get(row.sequence ?? -1);
        if (!reply) {line.text = 'native ' + toolOf(row.payload) + '  (no reply recorded)'; break;}
        const wrapper = str(reply.payload.tool);
        line.text = (wrapper && wrapper !== 'games_call_tool' ? wrapper + ' ' : 'native ') + toolOf(reply.payload);
        line.phases = readPhases(reply.payload.timing, traceId, span);
        if (line.phases && num((reply.payload.timing as Json).total_ms) !== null) line.durationMs = line.phases.totalMs;
        if (reply.kind === 'native_error') {line.kind = 'native_error'; line.error = str(reply.payload.error) || 'error';}
        break;
      }
      case 'native_cache_hit': line.text = 'cache hit ' + toolOf(row.payload); break;
      case 'native_decode': continue;
      default: {
        const msg = messageOf(row);
        line.text = msg ? `${row.kind} "${msg}"` : row.kind;
        line.attrs = attrsOf(row.payload);
      }
    }
    lines.push(line);
  }
  return {traceId, rows, tick, spanMs: (rows[rows.length - 1].wallTime - origin) * 1000, root: traceRoot(rows), lines};
}
