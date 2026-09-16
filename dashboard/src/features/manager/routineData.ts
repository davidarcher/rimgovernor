// Read-only view of /api/routines: the composed routine runtime and the
// development ranking its last review recorded. Nulls are unknown facts.
export const developmentReasons = ['', 'cancelled', 'adviser', 'emergency', 'startup_survival', 'blocked', 'existing_commitment', 'workers_unknown', 'no_workers', 'deficit_unknown', 'capacity_committed', 'method_unavailable', 'labor_unavailable', 'risk_deferred'] as const;
export type DevelopmentReason = typeof developmentReasons[number];
export type DevelopmentRow = {goal: string; score: number; deficit: number | null; risk: number | null; waitingSince: number; selected: boolean; committed: boolean; reason: DevelopmentReason; bottleneck: string};
export type LaborRow = {work: string; free: number};
export type Development = {tick: number; workers: number | null; labor: LaborRow[]; capacity: number; committed: string[]; rows: DevelopmentRow[]};
export type RoutineStatus = {reviewsEnabled: boolean; methodsEnabled: boolean; activeFamilies: string[]; lastReviewTick: number | null; development: Development | null};

function isObject(v: unknown): v is Record<string, unknown> {return typeof v === 'object' && v !== null && !Array.isArray(v);}
function object(v: unknown, keys: readonly string[]): Record<string, unknown> {if (!isObject(v) || Object.keys(v).length !== keys.length || keys.some(key => !Object.hasOwn(v, key))) throw Error('Invalid routine fields'); return v;}
function text(v: unknown): string {if (typeof v !== 'string' || [...v].length > 256 || v.includes('\0')) throw Error('Invalid routine text'); return v;}
function id(v: unknown): string {const s = text(v); if (!s.trim()) throw Error('Invalid routine identifier'); return s;}
function bool(v: unknown): boolean {if (typeof v !== 'boolean') throw Error('Invalid routine flag'); return v;}
function finite(v: unknown): number {if (typeof v !== 'number' || !Number.isFinite(v)) throw Error('Invalid routine number'); return v;}
function count(v: unknown): number {const n = finite(v); if (!Number.isInteger(n) || n < 0 || n > 1000000) throw Error('Invalid routine count'); return n;}
function tick(v: unknown): number {const n = finite(v); if (!Number.isInteger(n) || n < 0 || n > Number.MAX_SAFE_INTEGER) throw Error('Invalid routine tick'); return n;}
function unit(v: unknown): number {const n = finite(v); if (n < 0 || n > 1) throw Error('Invalid routine fraction'); return n;}
function nullable<T>(v: unknown, read: (value: unknown) => T): T | null {return v === null ? null : read(v);}
function list(v: unknown, limit: number): unknown[] {if (!Array.isArray(v) || v.length > limit) throw Error('Routine collection limit exceeded'); return v;}
function reason(v: unknown): DevelopmentReason {const r = developmentReasons.find(item => item === v); if (r === undefined) throw Error('Unknown development reason'); return r;}

export function readDevelopment(value: unknown): Development {
  const v = object(value, ['tick', 'workers', 'labor', 'capacity', 'committed', 'rows']);
  const labor = list(v.labor, 64).map(item => {const l = object(item, ['work', 'free']); return {work: id(l.work), free: count(l.free)};});
  const rows = list(v.rows, 256).map((item): DevelopmentRow => {
    const r = object(item, ['goal', 'score', 'deficit', 'risk', 'waitingSince', 'selected', 'committed', 'reason', 'bottleneck']);
    const row = {goal: id(r.goal), score: finite(r.score), deficit: nullable(r.deficit, unit), risk: nullable(r.risk, unit), waitingSince: tick(r.waitingSince), selected: bool(r.selected), committed: bool(r.committed), reason: reason(r.reason), bottleneck: text(r.bottleneck)};
    if (row.score < 0 || row.selected && (row.committed || row.reason !== '') || row.bottleneck !== '' && row.reason !== 'labor_unavailable') throw Error('Inconsistent development row');
    return row;
  });
  const result = {tick: tick(v.tick), workers: nullable(v.workers, count), labor, capacity: count(v.capacity), committed: list(v.committed, 256).map(id), rows};
  if (new Set(rows.map(r => r.goal)).size !== rows.length || rows.filter(r => r.selected).length + result.committed.length > result.capacity) throw Error('Inconsistent development admission');
  return result;
}
export function readRoutineStatus(value: unknown): RoutineStatus {
  const v = object(value, ['reviewsEnabled', 'methodsEnabled', 'activeFamilies', 'lastReviewTick', 'development']);
  return {reviewsEnabled: bool(v.reviewsEnabled), methodsEnabled: bool(v.methodsEnabled), activeFamilies: list(v.activeFamilies, 256).map(id), lastReviewTick: nullable(v.lastReviewTick, tick), development: nullable(v.development, readDevelopment)};
}
export class RoutineHTTPError extends Error {constructor(public status: number, detail: string) {super(detail);}}
export async function fetchRoutineStatus(signal: AbortSignal): Promise<RoutineStatus> {
  const response = await fetch('/api/routines', {method: 'GET', cache: 'no-store', credentials: 'same-origin', signal});
  if (!response.ok) {let detail = `Routine diagnostics unavailable (${response.status})`; try {const error = object(await response.json(), ['code', 'detail']); id(error.code); detail = text(error.detail);} catch { /* Retain local diagnostic for malformed errors. */ } throw new RoutineHTTPError(response.status, detail);}
  const source = await response.text(); if (new TextEncoder().encode(source).length > 1048576) throw Error('Routine response exceeds size bound'); return readRoutineStatus(JSON.parse(source) as unknown);
}
