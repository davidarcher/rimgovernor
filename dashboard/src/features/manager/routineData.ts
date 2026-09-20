// Read-only view of /api/routines: the composed routine runtime and the
// development ranking its last review recorded. Nulls are unknown facts.
export const developmentReasons = ['', 'cancelled', 'adviser', 'emergency', 'startup_survival', 'blocked', 'existing_commitment', 'labor_idle', 'workers_unknown', 'no_workers', 'deficit_unknown', 'capacity_committed', 'method_unavailable', 'labor_unavailable', 'risk_deferred', 'control_disabled'] as const;
export type DevelopmentReason = typeof developmentReasons[number];
export type DevelopmentRow = {goal: string; score: number; deficit: number | null; risk: number | null; waitingSince: number; selected: boolean; committed: boolean; reason: DevelopmentReason; bottleneck: string};
export type LaborRow = {work: string; free: number};
export type Development = {tick: number; workers: number | null; labor: LaborRow[]; capacity: number; committed: string[]; rows: DevelopmentRow[]};
// The roster planner's last recorded report (#448): coverage per work type,
// skills no assignment exercises and each pawn's typed profile as the
// planner scored it. Pawn ids are the native pawn ids the colonist roster
// carries, so the dossier joins on them.
export type CoverageRow = {work: string; demand: number; owners: number; capable: number};
export type DecayingRow = {pawn: string; skill: string; level: number};
export type ProfileTrait = {name: string; degree: number};
export type TraitEffects = {workSpeed: number; learnRate: number; moveSpeed: number; sociable: number; chemicalInterest: number; flags: string[]};
export type ProfileSkill = {name: string; level: number; stored: number; passion: string; disabled: boolean; learnFactor: number};
export type PawnProfile = {pawn: string; age: number; child: boolean; ranged: boolean; traits: ProfileTrait[]; effects: TraitEffects; skills: ProfileSkill[]; incapable: string[]; forbidden: string[]};
export type WorkRoster = {tick: number; coverage: CoverageRow[]; decaying: DecayingRow[]; pawns: PawnProfile[]};
export type SectionStatus = {section: string; family: string; asOf: number; complete: boolean; source: string; storedAt: string};
export type ResourceRunway = {resource: string; tick: number; windowDays: number; thresholdDays: number; reserve: number; stock: number | null; surfaceOre: number | null; consumptionPerDay: number | null; stockDays: number | null; daysLeft: number | null; deficit: boolean | null; target: number};
export type ExtentRegion = {region: number; stage: string; cells: number; origins: string[]; facilities: string[]; activeFacilities: string[]; eligible: boolean; holdReasons: string[]};
export type ExtentEligibility = {known: boolean; reason: string; regions: ExtentRegion[]};
export type GridCell = {x: number; z: number};
// The persisted colony grid (#605): origin and unit axes in map cells, the pitch, the evidence the origin came from and the map bounds the overlay draws it across.
export type ColonyGrid = {origin: GridCell; pitch: number; axes: [GridCell, GridCell]; source: string; bounds: {width: number; height: number}};
// GoalProgress is one active goal's progress record (#629): the method in
// play, the observable it should move, the tick native evidence last moved
// it, the tick the review inspects the blocker, and why it is blocked ('' when
// it is not). Cooldowns key failed situations out until a bounded tick.
export type GoalProgress = {goal: string; method: string; expected: string; lastProgress: number; nextReview: number; blocked: string; cooldowns: {key: string; until: number}[]};
export type RoutineStatus = {reviewsEnabled: boolean; methodsEnabled: boolean; activeFamilies: string[]; lastReviewTick: number | null; development: Development | null; progress: GoalProgress[]; roster: WorkRoster | null; sections: SectionStatus[]; resourceRunways: ResourceRunway[]; resourceReach: {stage: string; reason: string} | null; extentEligibility: ExtentEligibility | null; colonyGrid: ColonyGrid | null};

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
function signed(v: unknown): number {const n = finite(v); if (n < -100 || n > 100) throw Error('Invalid routine offset'); return n;}
function level(v: unknown): number {const n = finite(v); if (!Number.isInteger(n) || n < 0 || n > 20) throw Error('Invalid routine skill level'); return n;}
function years(v: unknown): number {const n = finite(v); if (n < 0 || n > 10000) throw Error('Invalid routine age'); return n;}
export function readRoster(value: unknown): WorkRoster {
  const v = object(value, ['tick', 'coverage', 'decaying', 'pawns']);
  const coverage = list(v.coverage, 256).map(item => {const c = object(item, ['work', 'demand', 'owners', 'capable']); return {work: id(c.work), demand: count(c.demand), owners: count(c.owners), capable: count(c.capable)};});
  const decaying = list(v.decaying, 4096).map(item => {const d = object(item, ['pawn', 'skill', 'level']); return {pawn: id(d.pawn), skill: id(d.skill), level: level(d.level)};});
  const pawns = list(v.pawns, 256).map((item): PawnProfile => {
    const p = object(item, ['pawn', 'age', 'child', 'ranged', 'traits', 'effects', 'skills', 'incapable', 'forbidden']);
    const e = object(p.effects, ['workSpeed', 'learnRate', 'moveSpeed', 'sociable', 'chemicalInterest', 'flags']);
    return {pawn: id(p.pawn), age: years(p.age), child: bool(p.child), ranged: bool(p.ranged),
      traits: list(p.traits, 256).map(t => {const r = object(t, ['name', 'degree']); return {name: id(r.name), degree: signed(r.degree)};}),
      effects: {workSpeed: signed(e.workSpeed), learnRate: signed(e.learnRate), moveSpeed: signed(e.moveSpeed), sociable: signed(e.sociable), chemicalInterest: signed(e.chemicalInterest), flags: list(e.flags, 64).map(id)},
      skills: list(p.skills, 256).map(sk => {const r = object(sk, ['name', 'level', 'stored', 'passion', 'disabled', 'learnFactor']); return {name: id(r.name), level: level(r.level), stored: level(r.stored), passion: text(r.passion), disabled: bool(r.disabled), learnFactor: signed(r.learnFactor)};}),
      incapable: list(p.incapable, 64).map(id), forbidden: list(p.forbidden, 64).map(id)};
  });
  if (new Set(pawns.map(p => p.pawn)).size !== pawns.length || new Set(coverage.map(c => c.work)).size !== coverage.length) throw Error('Inconsistent work roster');
  if (pawns.some(p => p.skills.some(s => s.disabled && s.learnFactor !== 0 || s.learnFactor < 0))) throw Error('Inconsistent profile skill');
  return {tick: tick(v.tick), coverage, decaying, pawns};
}
function readSection(value: unknown): SectionStatus {
  // Stale marks are omitted while nothing is marked; the panel shows none of them.
  const v = object(value, isObject(value) && Object.hasOwn(value, 'stale') ? ['section', 'family', 'asOf', 'complete', 'source', 'storedAt', 'stale'] : ['section', 'family', 'asOf', 'complete', 'source', 'storedAt']);
  return {section: id(v.section), family: text(v.family), asOf: tick(v.asOf), complete: bool(v.complete), source: text(v.source), storedAt: text(v.storedAt)};
}
function nonnegative(v: unknown): number {const n = finite(v); if (n < 0) throw Error('Invalid runway number'); return n;}
function readResourceRunway(value: unknown): ResourceRunway {
  const r = object(value, ['resource', 'tick', 'windowDays', 'thresholdDays', 'reserve', 'stock', 'surfaceOre', 'consumptionPerDay', 'stockDays', 'daysLeft', 'deficit', 'target']);
  return {resource: id(r.resource), tick: tick(r.tick), windowDays: nonnegative(r.windowDays), thresholdDays: nonnegative(r.thresholdDays), reserve: tick(r.reserve), stock: nullable(r.stock, tick), surfaceOre: nullable(r.surfaceOre, tick), consumptionPerDay: nullable(r.consumptionPerDay, nonnegative), stockDays: nullable(r.stockDays, nonnegative), daysLeft: nullable(r.daysLeft, nonnegative), deficit: nullable(r.deficit, bool), target: tick(r.target)};
}
function coordinate(v: unknown): number {const n = finite(v); if (!Number.isInteger(n) || n < -4096 || n > 4096) throw Error('Invalid grid coordinate'); return n;}
function readGridCell(value: unknown): GridCell {const c = object(value, ['x', 'z']); return {x: coordinate(c.x), z: coordinate(c.z)};}
function readColonyGrid(value: unknown): ColonyGrid {
  const g = object(value, ['origin', 'pitch', 'axes', 'source', 'bounds']);
  const axes = list(g.axes, 2).map(readGridCell);
  const bounds = object(g.bounds, ['width', 'height']);
  const grid = {origin: readGridCell(g.origin), pitch: count(g.pitch), axes: [axes[0], axes[1]] as [GridCell, GridCell], source: id(g.source), bounds: {width: count(bounds.width), height: count(bounds.height)}};
  const unit = (a: GridCell) => Math.abs(a.x) + Math.abs(a.z) === 1;
  if (axes.length !== 2 || grid.pitch === 0 || !unit(axes[0]) || !unit(axes[1]) || axes[0].x * axes[1].x + axes[0].z * axes[1].z !== 0) throw Error('Inconsistent colony grid');
  return grid;
}
function readGoalProgress(v: unknown): GoalProgress {
  const p = object(v, ['goal', 'method', 'expected', 'lastProgress', 'nextReview', 'blocked', 'cooldowns']);
  const row = {goal: id(p.goal), method: id(p.method), expected: text(p.expected), lastProgress: tick(p.lastProgress), nextReview: tick(p.nextReview), blocked: text(p.blocked), cooldowns: list(p.cooldowns, 64).map(c => {const d = object(c, ['key', 'until']); return {key: id(d.key), until: tick(d.until)};})};
  if (row.nextReview < row.lastProgress) throw Error('Inconsistent goal progress');
  return row;
}

export function readRoutineStatus(value: unknown): RoutineStatus {
  // Older retained responses predate extent diagnostics, loot holds, the colony grid and goal progress.
  const optional = ['resourceReach', 'extent', 'extentEligibility', 'lootHolds', 'colonyGrid', 'progress'].filter(k => isObject(value) && Object.hasOwn(value, k));
  const v = object(value, ['reviewsEnabled', 'methodsEnabled', 'activeFamilies', 'lastReviewTick', 'development', 'roster', 'sections', 'resourceRunways', ...optional]);
  let resourceReach = null;
  if (v.resourceReach !== undefined) {const r = object(v.resourceReach, ['stage', 'reason']); resourceReach = {stage: id(r.stage), reason: text(r.reason)};}
  if (v.extent !== undefined) {const e = object(v.extent, ['known', 'regions', 'cells']); bool(e.known); count(e.regions); count(e.cells);}
  let extentEligibility = null;
  if (v.extentEligibility !== undefined) {
    const e = object(v.extentEligibility, ['known', 'reason', 'regions']);
    extentEligibility = {known: bool(e.known), reason: text(e.reason), regions: list(e.regions, 65536).map((item): ExtentRegion => {
      const r = object(item, ['region', 'stage', 'cells', 'origins', 'facilities', 'activeFacilities', 'eligible', 'holdReasons']);
      const row = {region: count(r.region), stage: id(r.stage), cells: count(r.cells), origins: list(r.origins, 4).map(id), facilities: list(r.facilities, 65536).map(id), activeFacilities: list(r.activeFacilities, 65536).map(id), eligible: bool(r.eligible), holdReasons: list(r.holdReasons, 16).map(id)};
      if (row.eligible !== (row.holdReasons.length === 0) || row.activeFacilities.some(f => !row.facilities.includes(f))) throw Error('Inconsistent extent eligibility');
      return row;
    })};
  }
  if (v.lootHolds !== undefined) list(v.lootHolds, 65536).forEach(h => object(h, ['thing', 'definition', 'x', 'z', 'reason']));
  const colonyGrid = v.colonyGrid === undefined ? null : nullable(v.colonyGrid, readColonyGrid);
  const progress = v.progress === undefined ? [] : list(v.progress, 512).map(readGoalProgress);
  if (new Set(progress.map(p => p.goal)).size !== progress.length) throw Error('Duplicate goal progress');
  return {reviewsEnabled: bool(v.reviewsEnabled), methodsEnabled: bool(v.methodsEnabled), activeFamilies: list(v.activeFamilies, 256).map(id), lastReviewTick: nullable(v.lastReviewTick, tick), development: nullable(v.development, readDevelopment), progress, roster: nullable(v.roster, readRoster), sections: list(v.sections, 256).map(readSection), resourceRunways: list(v.resourceRunways, 256).map(readResourceRunway), resourceReach, extentEligibility, colonyGrid};
}
export class RoutineHTTPError extends Error {constructor(public status: number, detail: string) {super(detail);}}
export async function fetchRoutineStatus(signal: AbortSignal): Promise<RoutineStatus> {
  const response = await fetch('/api/routines', {method: 'GET', cache: 'no-store', credentials: 'same-origin', signal});
  if (!response.ok) {let detail = `Routine diagnostics unavailable (${response.status})`; try {const error = object(await response.json(), ['code', 'detail']); id(error.code); detail = text(error.detail);} catch { /* Retain local diagnostic for malformed errors. */ } throw new RoutineHTTPError(response.status, detail);}
  const source = await response.text(); if (new TextEncoder().encode(source).length > 1048576) throw Error('Routine response exceeds size bound'); return readRoutineStatus(JSON.parse(source) as unknown);
}
