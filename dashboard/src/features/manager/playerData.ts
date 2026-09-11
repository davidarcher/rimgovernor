export type World = {colonyId: string; mapId: number; loadToken: string};
export type Building = {defName: string; stuff: string; x: number; z: number; rotation: 'north' | 'east' | 'south' | 'west'};
export type SubmissionRequest = {requestId: string; expected: World; building: Building};
export type Submission = SubmissionRequest & {planId: string; actionId: string; revision: string};
export type DraftRequest = {requestId: string; expected: World; draft: {pawnId: string}};
export type DraftSubmission = DraftRequest & {planId: string; actionId: string; revision: string};
export type AcquireRequest = {requestId: string; expected: World; planId: string; revision: string; expectedDirection: string};
export type ManualRequest = {requestId: string; expected: World};
export type Generation = {colony: string; map: number; load: string; direction: string; plan: string; revision: string; native: string};
export type PlayerState = {enabled: boolean; observationKnown: boolean; generation: Generation | null};
export type Failure = {code: string; detail: string};
export type ControlRecord = {requestId: string; kind: 'acquire' | 'manual'; expected: World; planId: string | null; revision: string; expectedDirection: string; direction: string; phase: 'pending' | 'granted' | 'disabled' | 'refused' | 'uncertain'; nativeGeneration: string};
export type ControlReply = {record: ControlRecord | null; state: PlayerState; error: Failure | null};

function isObject(value: unknown): value is Record<string, unknown> {return typeof value === 'object' && value !== null && !Array.isArray(value);}
function object(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (!isObject(value) || Object.keys(value).length !== keys.length || keys.some(key => !Object.hasOwn(value, key))) throw Error('Invalid player response fields');
  return value;
}
function text(value: unknown): string {if (typeof value !== 'string' || [...value].length > 4096) throw Error('Invalid response text'); return value;}
function id(value: unknown): string {const result = text(value); if (!result.trim() || result.includes('\0') || new TextEncoder().encode(result).length > 256 || /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(result)) throw Error('Invalid identifier'); return result;}
function bool(value: unknown): boolean {if (typeof value !== 'boolean') throw Error('Invalid flag'); return value;}
function integer(value: unknown): number {if (typeof value !== 'number' || !Number.isInteger(value) || value < 0 || value > 2147483647) throw Error('Invalid map coordinate'); return value;}
function decimal(value: unknown): string {const result = text(value); if (!/^(0|[1-9][0-9]{0,19})$/.test(result) || BigInt(result) > 18446744073709551615n) throw Error('Invalid uint64 value'); return result;}
function positive(value: unknown): string {const result = decimal(value); if (result === '0') throw Error('Expected positive revision or direction'); return result;}
function choice<T extends string>(value: unknown, values: readonly T[]): T {const result = values.find(item => item === value); if (result === undefined) throw Error('Unknown player response variant'); return result;}
export function readWorld(value: unknown): World {const v = object(value, ['colonyId', 'mapId', 'loadToken']); return {colonyId: id(v.colonyId), mapId: integer(v.mapId), loadToken: id(v.loadToken)};}
export function sameWorld(a: World, b: World): boolean {return a.colonyId === b.colonyId && a.mapId === b.mapId && a.loadToken === b.loadToken;}
export function readBuilding(value: unknown): Building {
  const v = object(value, ['defName', 'stuff', 'x', 'z', 'rotation']);
  return {defName: id(v.defName), stuff: v.stuff === '' ? '' : id(v.stuff), x: integer(v.x), z: integer(v.z), rotation: choice(v.rotation, ['north', 'east', 'south', 'west'])};
}
export function readSubmission(value: unknown): Submission {
  const v = object(value, ['requestId', 'expected', 'building', 'planId', 'actionId', 'revision']);
  return {requestId: id(v.requestId), expected: readWorld(v.expected), building: readBuilding(v.building), planId: id(v.planId), actionId: id(v.actionId), revision: positive(v.revision)};
}
function readGeneration(value: unknown): Generation {
  const v = object(value, ['colony', 'map', 'load', 'direction', 'plan', 'revision', 'native']);
  return {colony: id(v.colony), map: integer(v.map), load: id(v.load), direction: decimal(v.direction), plan: id(v.plan), revision: decimal(v.revision), native: decimal(v.native)};
}
export function readPlayerState(value: unknown): PlayerState {
  const v = object(value, ['enabled', 'observationKnown', 'generation']);
  const state = {enabled: bool(v.enabled), observationKnown: bool(v.observationKnown), generation: v.generation === null ? null : readGeneration(v.generation)};
  if (state.observationKnown !== (state.generation !== null) || state.enabled && (!state.generation || [state.generation.direction, state.generation.revision, state.generation.native].includes('0'))) throw Error('Invalid current permission');
  return state;
}
function readControlRecord(value: unknown): ControlRecord {
  const v = object(value, ['requestId', 'kind', 'expected', 'planId', 'revision', 'expectedDirection', 'direction', 'phase', 'nativeGeneration']);
  const record: ControlRecord = {requestId: id(v.requestId), kind: choice(v.kind, ['acquire', 'manual']), expected: readWorld(v.expected), planId: v.planId === null ? null : id(v.planId), revision: decimal(v.revision), expectedDirection: decimal(v.expectedDirection), direction: positive(v.direction), phase: choice(v.phase, ['pending', 'granted', 'disabled', 'refused', 'uncertain']), nativeGeneration: decimal(v.nativeGeneration)};
  if (record.kind === 'manual' ? record.planId !== null || record.revision !== '0' || record.expectedDirection !== '0' : record.planId === null || record.revision === '0') throw Error('Invalid control intent');
  if (record.phase === 'granted' ? record.kind !== 'acquire' || record.nativeGeneration === '0' : record.nativeGeneration !== '0') throw Error('Invalid historical grant');
  if (record.phase === 'disabled' && record.kind !== 'manual') throw Error('Invalid Manual result');
  return record;
}
function readFailure(value: unknown): Failure {const v = object(value, ['code', 'detail']); return {code: id(v.code), detail: text(v.detail)};}
export function readControl(value: unknown): ControlReply {const v = object(value, ['record', 'state', 'error']); return {record: v.record === null ? null : readControlRecord(v.record), state: readPlayerState(v.state), error: v.error === null ? null : readFailure(v.error)};}

export class PlayerHTTPError extends Error {constructor(public status: number, message: string, public code: string | null = null) {super(message);}}
// Only explicit, validated server rejections prove that this POST was not admitted.
export function definiteRejection(error: unknown): boolean {
  return error instanceof PlayerHTTPError && (error.status === 400 && error.code === 'invalid_request' || error.status === 403 && error.code === 'player_auth' || error.status === 409 && ['conflict', 'capacity'].includes(error.code ?? ''));
}
async function request(path: string, signal?: AbortSignal, token?: string, body?: SubmissionRequest | DraftRequest | AcquireRequest | ManualRequest): Promise<{response: Response; value: unknown}> {
  const response = await fetch(path, {method: body ? 'POST' : 'GET', cache: 'no-store', credentials: 'same-origin', signal,
    ...(body ? {headers: {'Content-Type': 'application/json', 'X-RimGovernor-Player': token ?? ''}, body: JSON.stringify(body)} : {})});
  const value: unknown = await response.json();
  return {response, value};
}
function failed(response: Response, value: unknown): never {let detail = `Player operation unavailable (${response.status})`, code: string | null = null; try {const failure = readFailure(value); detail = failure.detail; code = failure.code;} catch { /* Malformed evidence does not prove non-admission. */ } throw new PlayerHTTPError(response.status, detail, code);}
export async function readPlayerSession(signal?: AbortSignal): Promise<string | null> {
  const {response, value} = await request('/api/player/session', signal);
  if (response.status === 404) return null;
  if (!response.ok) failed(response, value);
  const v = object(value, ['token', 'mode']); if (v.mode !== 'explicit-player') throw Error('Unknown player mode'); return id(v.token);
}
export async function submitBuilding(token: string, body: SubmissionRequest, signal?: AbortSignal): Promise<Submission> {
  const {response, value} = await request('/api/buildings/plans', signal, token, body); if (!response.ok) failed(response, value); return readSubmission(value);
}
export async function readSubmissionResult(requestId: string, signal?: AbortSignal): Promise<Submission> {
  const {response, value} = await request(`/api/buildings/submission?requestId=${encodeURIComponent(requestId)}`, signal); if (!response.ok) failed(response, value); return readSubmission(value);
}
async function controlRequest(path: string, signal?: AbortSignal, token?: string, body?: AcquireRequest | ManualRequest): Promise<ControlReply> {
  const {response, value} = await request(path, signal, token, body);
  // Error statuses can carry durable pending/uncertain evidence and actual state.
  if (!response.ok && !(isObject(value) && Object.hasOwn(value, 'state'))) failed(response, value);
  const reply = readControl(value);
  if (body && reply.record === null && reply.error) {
    const rejection = new PlayerHTTPError(response.status, reply.error.detail, reply.error.code);
    if (definiteRejection(rejection)) throw rejection;
  }
  return reply;
}
export function readCurrentControl(signal?: AbortSignal): Promise<ControlReply> {return controlRequest('/api/player/control', signal);}
export function readControlResult(requestId: string, signal?: AbortSignal): Promise<ControlReply> {return controlRequest(`/api/player/control?requestId=${encodeURIComponent(requestId)}`, signal);}
export function acquirePlan(token: string, body: AcquireRequest, signal?: AbortSignal): Promise<ControlReply> {return controlRequest('/api/player/control/acquire', signal, token, body);}
export function manualPlayer(token: string, body: ManualRequest, signal?: AbortSignal): Promise<ControlReply> {return controlRequest('/api/player/control/manual', signal, token, body);}

export function readDraft(value: unknown): {pawnId: string} {const v = object(value, ['pawnId']); return {pawnId: id(v.pawnId)};}
export function readDraftSubmission(value: unknown): DraftSubmission {
  const v = object(value, ['requestId', 'expected', 'draft', 'planId', 'actionId', 'revision']);
  return {requestId: id(v.requestId), expected: readWorld(v.expected), draft: readDraft(v.draft), planId: id(v.planId), actionId: id(v.actionId), revision: positive(v.revision)};
}
export async function submitDraft(token: string, body: DraftRequest, signal?: AbortSignal): Promise<DraftSubmission> {
  const {response, value} = await request('/api/drafts/plans', signal, token, body); if (!response.ok) failed(response, value); return readDraftSubmission(value);
}
export async function readDraftResult(requestId: string, signal?: AbortSignal): Promise<DraftSubmission> {
  const {response, value} = await request(`/api/drafts/submission?requestId=${encodeURIComponent(requestId)}`, signal); if (!response.ok) failed(response, value); return readDraftSubmission(value);
}
