// The controller owns admission and rates; this decoder only validates its view.
export type FoodDecision = 'Open' | 'Hold' | 'Close';
export type FoodChannel = {kind: string; id: string; decision: FoodDecision; reason: string; deliveredPerDay: number};
export type PetShortfall = {id: string; label: string | null; runwayDays: number; nutritionPerDay: number};
export type FoodPlan = {portfolio: FoodChannel[]; unknown: FoodChannel[]; deliveredPerDay: number; demandPerDay: number; gapPerDay: number; explain: string; petShortfalls: PetShortfall[]};
export type FoodPlanStatus = {tick: number | null; plan: FoodPlan | null};
function object(v: unknown): Record<string, unknown> {if (typeof v !== 'object' || v === null || Array.isArray(v)) throw Error('Invalid food plan'); return v as Record<string, unknown>;}
function text(v: unknown): string {if (typeof v !== 'string') throw Error('Invalid food plan text'); return v;}
function number(v: unknown, signed = false): number {if (typeof v !== 'number' || !Number.isFinite(v) || !signed && v < 0) throw Error('Invalid food plan number'); return v;}
function rows(v: unknown): FoodChannel[] {
  if (!Array.isArray(v) || v.length > 4096) throw Error('Invalid food channels');
  const result = v.map((item: unknown): FoodChannel => {
    const r = object(item);
    if (r.decision !== 'Open' && r.decision !== 'Hold' && r.decision !== 'Close') throw Error('Invalid food decision');
    return {kind: text(r.kind), id: text(r.id), decision: r.decision, reason: text(r.reason), deliveredPerDay: number(r.deliveredPerDay)};
  });
  if (new Set(result.map(r => JSON.stringify([r.kind, r.id]))).size !== result.length) throw Error('Duplicate food channel');
  return result;
}
// petShortfalls (#708) is absent from servers older than the field; read that as none.
function pets(v: unknown): PetShortfall[] {
  if (v === undefined) return [];
  if (!Array.isArray(v) || v.length > 256) throw Error('Invalid pet shortfalls');
  return v.map((item: unknown): PetShortfall => {const r = object(item); return {id: text(r.id), label: r.label === undefined || r.label === null ? null : text(r.label), runwayDays: number(r.runwayDays), nutritionPerDay: number(r.nutritionPerDay)};});
}
export function readFoodPlanStatus(raw: unknown): FoodPlanStatus {
  const v = object(raw);
  if (v.foodPlan === null && v.foodPlanTick === null) return {tick: null, plan: null};
  const tick = number(v.foodPlanTick);
  if (!Number.isSafeInteger(tick)) throw Error('Invalid food plan tick');
  const p = object(v.foodPlan);
  return {tick, plan: {portfolio: rows(p.portfolio), unknown: rows(p.unknown), deliveredPerDay: number(p.deliveredPerDay), demandPerDay: number(p.demandPerDay), gapPerDay: number(p.gapPerDay, true), explain: text(p.explain), petShortfalls: pets(p.petShortfalls)}};
}
export async function fetchFoodPlan(signal: AbortSignal): Promise<FoodPlanStatus> {
  const response = await fetch('/api/player/colony', {method: 'GET', cache: 'no-store', credentials: 'same-origin', signal});
  if (!response.ok) throw Error(`Food plan unavailable (${response.status})`);
  return readFoodPlanStatus(await response.json());
}
