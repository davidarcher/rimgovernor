import {readDraft} from './playerData';
export type ObservationState = {
  sessionId: string; connected: boolean; mode: 'manual' | 'automate'; status: {label: string};
  identity: {colonyId: string; mapId: number; loadToken: string} | null;
  game: {tick: number | null; paused: boolean | null; observedAt: string | null; stale: boolean};
  activePlanId: string | null;
};
export type BuildingPlan = {id: string; revision: string; actions: BuildingAction[]};
const stages = ['pending', 'prepared', 'dispatched', 'awaiting_observation', 'completed', 'cancelled', 'unsuccessful'] as const;
const effects = ['unknown', 'pending', 'completed', 'absent', 'unsuccessful'] as const;
const unsuccessfulReasons = ['native_failure', 'cancelled', 'interrupted', 'expired', 'target_dead', 'outcome_not_achieved'] as const;
export type UnsuccessfulReason = typeof unsuccessfulReasons[number];
export const cleanupStages = ['awaiting_claim', 'not_acquired', 'required', 'dispatched', 'uncertain', 'released', 'superseded'] as const;
export type ActionProgress = {stage: typeof stages[number]; attempt: string; tick: number; unresolved: boolean; receipt: string | null; effect: typeof effects[number] | null; unsuccessfulReason: UnsuccessfulReason | null; draftCleanup?: {stage: typeof cleanupStages[number]}};
export type BuildingAction = {id: string; progress: ActionProgress} & (
  {kind: 'building'; building: {defName: string; x: number; z: number; rotation: string; stuff: string}} |
  {kind: 'owned_draft'; draft: {pawnId: string}}
);

// JSON responses are untrusted at this boundary; components consume concrete DTOs.
function object(value: unknown): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw Error('Invalid observation response');
  return value as Record<string, unknown>;
}
function text(value: unknown): string {if (typeof value !== 'string' || value.length > 4096) throw Error('Invalid text in response'); return value;}
function bool(value: unknown): boolean {if (typeof value !== 'boolean') throw Error('Invalid flag in response'); return value;}
function integer(value: unknown): number {if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) throw Error('Invalid number in response'); return value;}
function optional<T>(value: unknown, read: (value: unknown) => T): T | null {return value === null ? null : read(value);}
function oneOf<T extends string>(value: unknown, values: readonly T[]): T {const result = text(value); const matched = values.find(candidate => candidate === result); if (matched === undefined) throw Error('Unsupported response value'); return matched;}
function decimal(value: unknown): string {const result = text(value); if (!/^(0|[1-9][0-9]{0,19})$/.test(result)) throw Error('Invalid revision in response'); return result;}
export function readObservation(raw: unknown): ObservationState {
  const root = object(raw), game = object(root.game), status = object(root.status);
  const mode = root.mode;
  if (mode !== 'manual' && mode !== 'automate') throw Error('Unknown control mode');
  const observedAt = optional(game.observedAt, text);
  if (observedAt !== null && !Number.isFinite(Date.parse(observedAt))) throw Error('Invalid observation time');
  return {sessionId: text(root.sessionId), connected: bool(root.connected), mode, status: {label: text(status.label)},
    identity: optional(root.identity, value => {const id = object(value); return {colonyId: text(id.colonyId), mapId: integer(id.mapId), loadToken: text(id.loadToken)};}),
    game: {tick: optional(game.tick, integer), paused: optional(game.paused, bool), observedAt, stale: bool(game.stale)},
    activePlanId: optional(root.activePlanId, text)};
}
export function readPlan(raw: unknown): BuildingPlan {
  const root = object(raw);
  if (!Array.isArray(root.actions) || root.actions.length > 1024) throw Error('Invalid action list');
  const ids = new Set<string>();
  const actions = root.actions.map((value: unknown): BuildingAction => {
    const action = object(value), progress = object(action.progress), id = text(action.id);
    if (!['building', 'owned_draft'].includes(String(action.kind)) || ids.has(id) || Object.keys(action).length !== 4 || !Object.hasOwn(action, action.kind === 'building' ? 'building' : 'draft')) throw Error('Unsupported or repeated action'); ids.add(id);
    const stage = oneOf(progress.stage, stages), effect = optional(progress.effect, v => oneOf(v, effects));
    const unsuccessfulReason = optional(progress.unsuccessfulReason, v => oneOf(v, unsuccessfulReasons));
    const unresolved = bool(progress.unresolved);
    if ((effect === 'unsuccessful') !== (unsuccessfulReason !== null) ||
      (effect === 'unsuccessful' && (unresolved || (stage !== 'unsuccessful' && stage !== 'cancelled'))) ||
      (stage === 'unsuccessful' && effect !== 'unsuccessful')) throw Error('Inconsistent unsuccessful outcome');
    let draftCleanup: ActionProgress['draftCleanup'];
    if (Object.hasOwn(progress, 'draftCleanup')) {
      if (action.kind !== 'owned_draft') throw Error('Building cannot have draft cleanup');
      const cleanup = object(progress.draftCleanup);
      if (Object.keys(cleanup).length !== 1) throw Error('Invalid cleanup fields');
      draftCleanup = {stage: oneOf(cleanup.stage, cleanupStages)};
    }
    const parsed: ActionProgress = {stage, attempt: decimal(progress.attempt), tick: integer(progress.tick), unresolved,
      receipt: optional(progress.receipt, v => oneOf(v, ['accepted', 'refused', 'unknown'])), effect, unsuccessfulReason,
      ...(draftCleanup ? {draftCleanup} : {})};
    if (action.kind === 'owned_draft') return {id, kind: 'owned_draft', draft: readDraft(action.draft), progress: parsed};
    const building = object(action.building);
    return {id, kind: 'building', building: {defName: text(building.defName), x: integer(building.x), z: integer(building.z),
      rotation: oneOf(building.rotation, ['north', 'east', 'south', 'west']), stuff: text(building.stuff)}, progress: parsed};
  });
  return {id: text(root.id), revision: decimal(root.revision), actions};
}
