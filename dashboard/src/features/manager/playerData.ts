export type World = {colonyId: string; mapId: number; loadToken: string};
export type Building = {defName: string; stuff: string; x: number; z: number; rotation: 'north' | 'east' | 'south' | 'west'};
export type SubmissionRequest = {requestId: string; expected: World; building: Building};
export type Submission = SubmissionRequest & {planId: string; actionId: string; revision: string};
export type ControlRequest = {requestId: string; expected: World};
export type Generation = {colony: string; map: number; load: string; plan: string; revision: string; native: string};
export type PlayerState = {enabled: boolean; observationKnown: boolean; generation: Generation | null};
export type Failure = {code: string; detail: string};
export type ControlKind = 'resume' | 'pause';
export type ControlRecord = {requestId: string; kind: ControlKind; expected: World; phase: 'pending' | 'running' | 'paused' | 'refused' | 'uncertain'; nativeGeneration: string};
export type ControlReply = {record: ControlRecord | null; state: PlayerState; error: Failure | null};

function isObject(value: unknown): value is Record<string, unknown> {return typeof value === 'object' && value !== null && !Array.isArray(value);}
function object(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (!isObject(value) || Object.keys(value).length !== keys.length || keys.some(key => !Object.hasOwn(value, key))) throw Error('Invalid player response fields');
  return value;
}
function text(value: unknown): string {if (typeof value !== 'string' || [...value].length > 16384) throw Error('Invalid response text'); return value;}
function id(value: unknown): string {const result = text(value); if (!result.trim() || result.includes('\0') || new TextEncoder().encode(result).length > 256 || /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(result)) throw Error('Invalid identifier'); return result;}
function bool(value: unknown): boolean {if (typeof value !== 'boolean') throw Error('Invalid flag'); return value;}
function integer(value: unknown): number {if (typeof value !== 'number' || !Number.isInteger(value) || value < 0 || value > 2147483647) throw Error('Invalid map coordinate'); return value;}
function decimal(value: unknown): string {const result = text(value); if (!/^(0|[1-9][0-9]{0,19})$/.test(result) || BigInt(result) > 18446744073709551615n) throw Error('Invalid uint64 value'); return result;}
function positive(value: unknown): string {const result = decimal(value); if (result === '0') throw Error('Expected positive revision'); return result;}
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
  const v = object(value, ['colony', 'map', 'load', 'plan', 'revision', 'native']);
  return {colony: id(v.colony), map: integer(v.map), load: id(v.load), plan: id(v.plan), revision: decimal(v.revision), native: decimal(v.native)};
}
export function readPlayerState(value: unknown): PlayerState {
  const v = object(value, ['enabled', 'observationKnown', 'generation']);
  const state = {enabled: bool(v.enabled), observationKnown: bool(v.observationKnown), generation: v.generation === null ? null : readGeneration(v.generation)};
  if (state.observationKnown !== (state.generation !== null) || state.enabled && (!state.generation || [state.generation.revision, state.generation.native].includes('0'))) throw Error('Invalid current permission');
  return state;
}
function readControlRecord(value: unknown): ControlRecord {
  const v = object(value, ['requestId', 'kind', 'expected', 'phase', 'nativeGeneration']);
  const record: ControlRecord = {requestId: id(v.requestId), kind: choice(v.kind, ['resume', 'pause']), expected: readWorld(v.expected), phase: choice(v.phase, ['pending', 'running', 'paused', 'refused', 'uncertain']), nativeGeneration: decimal(v.nativeGeneration)};
  if (record.phase === 'running' ? record.kind !== 'resume' || record.nativeGeneration === '0' : record.nativeGeneration !== '0') throw Error('Invalid historical grant');
  if (record.phase === 'paused' && record.kind !== 'pause') throw Error('Invalid pause result');
  return record;
}
function readFailure(value: unknown): Failure {const v = object(value, ['code', 'detail']); return {code: id(v.code), detail: text(v.detail)};}
export function readControl(value: unknown): ControlReply {const v = object(value, ['record', 'state', 'error']); return {record: v.record === null ? null : readControlRecord(v.record), state: readPlayerState(v.state), error: v.error === null ? null : readFailure(v.error)};}

export class PlayerHTTPError extends Error {constructor(public status: number, message: string, public code: string | null = null) {super(message);}}
// Only explicit, validated server rejections prove that this POST was not admitted.
export function definiteRejection(error: unknown): boolean {
  return error instanceof PlayerHTTPError && (error.status === 400 && error.code === 'invalid_request' || error.status === 403 && error.code === 'player_auth' || error.status === 409 && ['conflict', 'capacity'].includes(error.code ?? ''));
}
async function request(path: string, signal?: AbortSignal, token?: string, body?: SubmissionRequest | ControlRequest | ClockAcknowledgement | ChatRequest): Promise<{response: Response; value: unknown}> {
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
async function controlRequest(path: string, signal?: AbortSignal, token?: string, body?: ControlRequest): Promise<ControlReply> {
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
// One author: resume runs the bot (routine methods and any submitted guidance) under the world's root plan; pause stops it.
export function resumeControl(token: string, body: ControlRequest, signal?: AbortSignal): Promise<ControlReply> {return controlRequest('/api/player/control/resume', signal, token, body);}
export function pauseControl(token: string, body: ControlRequest, signal?: AbortSignal): Promise<ControlReply> {return controlRequest('/api/player/control/pause', signal, token, body);}

export function readDraft(value: unknown): {pawnId: string} {const v = object(value, ['pawnId']); return {pawnId: id(v.pawnId)};}

export type ChatRequest = {requestId: string; expected: World; message: string};
export type ChatGoal = {goalId: string; source: string; status: string; need: string; priority: number; revision: string};
export type ChatGuidance =
  | {kind: 'activate_goal' | 'cancel_goal'; goal: ChatGoal}
  | {kind: 'set_population_policy'; populationPolicy: {maximum: number; foodDays: number}}
  | {kind: 'set_expedition_policy'; expeditionPolicy: Record<string, number | boolean>}
  | {kind: 'set_population_decision'; populationDecision: {pawn: string; decision: string}}
  | {kind: 'set_resource_policy'; resourcePolicy: {resource: string; reserve: number; spending: string}};
export type ChatReply = {requestId: string; expected: World; explanation: string; guidance: ChatGuidance | null};
function number(value: unknown): number {if (typeof value !== 'number' || !Number.isFinite(value)) throw Error('Invalid number'); return value;}
function readChatGoal(value: unknown): ChatGoal {
  const v = object(value, ['goalId', 'source', 'status', 'need', 'priority', 'epoch', 'revision', 'tick']);
  return {goalId: id(v.goalId), source: id(v.source), status: id(v.status), need: id(v.need), priority: integer(v.priority), revision: decimal(v.revision)};
}
function readChatGuidance(value: unknown): ChatGuidance {
  if (!isObject(value) || typeof value.kind !== 'string') throw Error('Invalid chat guidance');
  switch (value.kind) {
    case 'activate_goal': case 'cancel_goal': return {kind: value.kind, goal: readChatGoal(object(value, ['kind', 'goal']).goal)};
    case 'set_population_policy': {const p = object(object(value, ['kind', 'populationPolicy']).populationPolicy, ['maximum', 'foodDays']); return {kind: value.kind, populationPolicy: {maximum: integer(p.maximum), foodDays: number(p.foodDays)}};}
    case 'set_expedition_policy': {
      const p = object(value, ['kind', 'expeditionPolicy']).expeditionPolicy;
      if (!isObject(p) || Object.values(p).some(item => typeof item !== 'number' && typeof item !== 'boolean')) throw Error('Invalid expedition policy');
      return {kind: value.kind, expeditionPolicy: p as Record<string, number | boolean>};
    }
    case 'set_population_decision': {const p = object(object(value, ['kind', 'populationDecision']).populationDecision, ['pawn', 'decision']); return {kind: value.kind, populationDecision: {pawn: id(p.pawn), decision: id(p.decision)}};}
    case 'set_resource_policy': {const p = object(object(value, ['kind', 'resourcePolicy']).resourcePolicy, ['resource', 'reserve', 'spending']); return {kind: value.kind, resourcePolicy: {resource: id(p.resource), reserve: integer(p.reserve), spending: id(p.spending)}};}
    default: throw Error('Unknown chat guidance kind');
  }
}
export function readChatReply(value: unknown): ChatReply {
  const v = object(value, ['requestId', 'expected', 'explanation', 'guidance']);
  return {requestId: id(v.requestId), expected: readWorld(v.expected), explanation: text(v.explanation), guidance: v.guidance === null ? null : readChatGuidance(v.guidance)};
}
export class ChatDisabledError extends Error {}
export async function submitChat(token: string, body: ChatRequest, signal?: AbortSignal): Promise<ChatReply> {
  const {response, value} = await request('/api/chat', signal, token, body);
  if (response.status === 501) throw new ChatDisabledError('Chat is not enabled on this controller');
  if (!response.ok) failed(response, value);
  return readChatReply(value);
}

export type ClockAcknowledgement = {requestId: string; expectedRevision: string; throughCursor: string};
export type ClockReview = {revision: string; inboxCursor: string; reviewedCursor: string; acknowledgedCursor: string; holds: {kind: 'interruption' | 'gap'; fromCursor: string; throughCursor: string}[]};
export function decodeClockReview(value: unknown): ClockReview {
  const v = object(value, ['revision', 'inboxCursor', 'reviewedCursor', 'acknowledgedCursor', 'holds']);
  if (!Array.isArray(v.holds) || v.holds.length > 8192) throw Error('Invalid clock holds');
  const cursor = (value: unknown) => {const n = decimal(value); if (BigInt(n) > 9223372036854775807n) throw Error('Invalid clock cursor'); return n;};
  const result: ClockReview = {revision: decimal(v.revision), inboxCursor: cursor(v.inboxCursor), reviewedCursor: cursor(v.reviewedCursor), acknowledgedCursor: cursor(v.acknowledgedCursor), holds: v.holds.map(item => {
    const hold = object(item, ['kind', 'fromCursor', 'throughCursor']);
    return {kind: choice(hold.kind, ['interruption', 'gap']), fromCursor: cursor(hold.fromCursor), throughCursor: cursor(hold.throughCursor)};
  })};
  if (BigInt(result.acknowledgedCursor) > BigInt(result.reviewedCursor) || BigInt(result.reviewedCursor) > BigInt(result.inboxCursor) || result.holds.some(h => BigInt(h.fromCursor) > BigInt(h.throughCursor) || BigInt(h.throughCursor) > BigInt(result.reviewedCursor))) throw Error('Inconsistent clock review');
  return result;
}
export async function readClockReview(signal?: AbortSignal): Promise<ClockReview | null> {
  const {response, value} = await request('/api/player/clock', signal);
  if (response.status === 404) return null;
  if (!response.ok) failed(response, value);
  return decodeClockReview(value);
}
export async function acknowledgeClock(token: string, body: ClockAcknowledgement, signal?: AbortSignal): Promise<ClockReview> {
  const {response, value} = await request('/api/player/clock/acknowledge', signal, token, body);
  if (!response.ok) failed(response, value);
  return decodeClockReview(value);
}