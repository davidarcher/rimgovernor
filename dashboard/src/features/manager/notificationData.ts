import type {Listing, PresentationContext} from './presentationData';
export type Notice = {id: string | null; label: string | null; text: string | null; tick: string | null; ageTicks: string | null; ageSeconds: number | null; active: boolean | null; expired: boolean | null; detail: string | null};
export type NoticeSection = {kind: 'observed'; rows: Notice[]; listing: Listing | null} | {kind: 'unavailable'; reason: string; detail: string | null};
export type Notifications = {context: PresentationContext; letters: NoticeSection; messages: NoticeSection; alerts: NoticeSection};
function isObject(v: unknown): v is Record<string, unknown> {return typeof v === 'object' && v !== null && !Array.isArray(v);}
function object(v: unknown, keys: string[]): Record<string, unknown> {if (!isObject(v) || Object.keys(v).some(key => !keys.includes(key))) throw Error('Invalid notification fields'); return v;}
function text(v: unknown): string {if (typeof v !== 'string' || /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(v)) throw Error('Invalid notification text'); return v;}
function id(v: unknown): string {const s = text(v); if (!s.trim() || s.includes('\0') || new TextEncoder().encode(s).length > 256) throw Error('Invalid notification identity'); return s;}
function finite(v: unknown): number {if (typeof v !== 'number' || !Number.isFinite(v)) throw Error('Invalid notification number'); return v;}
function uint(v: unknown, max = 4294967295): number {const n = finite(v); if (!Number.isInteger(n) || n < 0 || n > max) throw Error('Invalid notification integer'); return n;}
function bool(v: unknown): boolean {if (typeof v !== 'boolean') throw Error('Invalid notification flag'); return v;}
function decimal(v: unknown, signed = false): string {const s = text(v); if (!(signed ? /^-?(0|[1-9][0-9]{0,18})$/ : /^(0|[1-9][0-9]{0,19})$/).test(s) || BigInt(s) > (signed ? 9223372036854775807n : 18446744073709551615n) || BigInt(s) < (signed ? -9223372036854775808n : 0n)) throw Error('Invalid notification 64-bit integer'); return s;}
function optional<T>(v: unknown, read: (value: unknown) => T): T | null {return v === undefined || v === null ? null : read(v);}
function rows(v: unknown, limit: number): unknown[] {if (v === undefined || v === null) return []; if (!Array.isArray(v) || v.length > limit) throw Error('Notification collection limit exceeded'); return v;}
function listing(value: unknown, count: number): Listing {const v = object(value, ['totalCount', 'returnedCount', 'complete', 'truncated']); const l = {totalCount: optional(v.totalCount, uint), returnedCount: optional(v.returnedCount, uint), complete: optional(v.complete, bool), truncated: optional(v.truncated, bool)}; if (l.returnedCount !== null && l.returnedCount !== count || l.totalCount !== null && l.totalCount < count || l.complete && (l.truncated || l.totalCount !== null && l.totalCount !== count) || l.truncated && l.totalCount !== null && l.totalCount <= count) throw Error('Inconsistent notification listing'); return l;}
function unavailable(value: unknown): {reason: string; detail: string | null} {const v = object(value, ['reason', 'detail']); const reason = text(v.reason); if (!['NOT_LOADED', 'NOT_OBSERVED', 'UNSUPPORTED', 'READ_FAILED', 'STALE', 'LIMIT_EXCEEDED', 'NOT_REQUESTED', 'NOT_APPLICABLE', 'HIDDEN', 'NATIVE_COMPONENT_MISSING'].some(item => reason === `UNAVAILABLE_REASON_${item}`)) throw Error('Invalid unavailable reason'); const detail = optional(v.detail, text); if (detail !== null && [...detail].length > 4096) throw Error('Oversized unavailable detail'); return {reason, detail};}
export function readNotifications(value: unknown): Notifications {
 const root = object(value, ['notifications']), v = object(root.notifications, ['context', 'letters', 'messages', 'alerts']), c = object(v.context, ['identity', 'tick', 'nativeGeneration']), w = object(c.identity, ['colonyId', 'mapId', 'loadToken']);
 const tick = decimal(c.tick, true); if (BigInt(tick) < 0n) throw Error('Invalid observation tick');
 const context = {identity: {colonyId: id(w.colonyId), mapId: uint(w.mapId, 2147483647), loadToken: id(w.loadToken)}, tick, nativeGeneration: optional(c.nativeGeneration, decimal)};
 let targetCount = 0;
 const target = (value: unknown) => {if (++targetCount > 4096) throw Error('Notification targets exceed bound'); const t = object(value, ['nativeKind', 'id', 'nativeType', 'label', 'defName', 'mapId', 'position', 'worldTileId']); for (const k of ['nativeKind', 'nativeType', 'label', 'defName']) optional(t[k], text); optional(t.id, id); optional(t.worldTileId, id); optional(t.mapId, x => uint(x, 2147483647)); if (t.position !== undefined && t.position !== null) {const p = object(t.position, ['x', 'z']); uint(p.x, 2147483647); uint(p.z, 2147483647);} };
 const targets = (value: unknown) => {if (value === undefined || value === null) return; const t = object(value, ['valid', 'primary', 'targets', 'listing']); optional(t.valid, bool); if (t.primary !== undefined && t.primary !== null) target(t.primary); const r = rows(t.targets, 4096); r.forEach(target); optional(t.listing, l => listing(l, r.length));};
 const section = (value: unknown, kind: 'letters' | 'messages' | 'alerts', limit: number): NoticeSection => {
  const s = object(value, ['observed', 'unavailable']); if (s.unavailable !== undefined && s.unavailable !== null) {if (s.observed !== undefined && s.observed !== null) throw Error('Ambiguous notification section'); return {kind: 'unavailable', ...unavailable(s.unavailable)};}
  const o = object(s.observed, kind === 'alerts' ? [kind, 'listing', 'snapshotFingerprint'] : [kind, 'listing']);
  // Listing belongs to each observed collection even when its optional counts are absent.
  const data = rows(o[kind], limit), seen = new Set<string>();
  optional(o.snapshotFingerprint, value => {const s = text(value); if (new TextEncoder().encode(s).length > 4096) throw Error('Oversized notification fingerprint'); return s;});
  const decoded = data.map((value): Notice => {
   const keys = kind === 'letters' ? ['id', 'nativeType', 'defName', 'label', 'text', 'arrivalTick', 'ageTicks', 'dismissible', 'automaticallyOpens', 'relatedFactionLabel', 'lookTargets', 'choices', 'choicesListing'] : kind === 'messages' ? ['id', 'text', 'messageDefName', 'alpha', 'expired', 'startingTick', 'startingFrame', 'ageTicks', 'hasQuest', 'lookTargets', 'ageSeconds'] : ['id', 'ordinal', 'nativeType', 'nativePriority', 'label', 'explanation', 'jumpToTargetsText', 'active', 'enabledWithActiveExpansions', 'anyCulpritValid', 'targets', 'listing', 'activatable', 'readIssue'];
   const n = object(value, keys), identity = optional(n.id, id); if (identity !== null) {if (seen.has(identity)) throw Error('Duplicate notification identity'); seen.add(identity);}
   for (const k of ['nativeType', 'defName', 'relatedFactionLabel', 'messageDefName', 'nativePriority', 'jumpToTargetsText']) optional(n[k], text);
   for (const k of ['dismissible', 'automaticallyOpens', 'hasQuest', 'enabledWithActiveExpansions', 'anyCulpritValid', 'activatable']) optional(n[k], bool);
   optional(n.alpha, finite); optional(n.ordinal, uint); optional(n.startingFrame, decimal); targets(n.lookTargets);
   if (kind === 'letters') {const choices = rows(n.choices, 1048576), indices = new Set<number>(); for (const value of choices) {const q = object(value, ['index', 'text', 'disabled', 'disabledReason', 'closesDialog', 'hasAction', 'hasLink']); const index = optional(q.index, uint); if (index !== null) {if (index === 0 || indices.has(index)) throw Error('Invalid notification choice identity'); indices.add(index);} optional(q.text, text); optional(q.disabledReason, text); for (const k of ['disabled', 'closesDialog', 'hasAction', 'hasLink']) optional(q[k], bool);} optional(n.choicesListing, l => listing(l, choices.length));}
   if (kind === 'alerts') {const t = rows(n.targets, 4096); t.forEach(target); optional(n.listing, l => listing(l, t.length));}
   const ageTicks = optional(n.ageTicks, x => decimal(x, true)); if (ageTicks !== null && BigInt(ageTicks) < 0n) throw Error('Negative notification tick age');
   const issue = n.readIssue === undefined || n.readIssue === null ? null : unavailable(n.readIssue);
   return {id: identity, label: optional(n.label, text), text: optional(kind === 'alerts' ? n.explanation : n.text, text), tick: optional(kind === 'letters' ? n.arrivalTick : n.startingTick, x => decimal(x, true)), ageTicks, ageSeconds: optional(n.ageSeconds, finite), active: optional(n.active, bool), expired: optional(n.expired, bool), detail: issue === null ? null : issue.detail ?? issue.reason};
  });
  return {kind: 'observed', rows: decoded, listing: optional(o.listing, l => listing(l, decoded.length))};
 };
 return {context, letters: section(v.letters, 'letters', 40), messages: section(v.messages, 'messages', 12), alerts: section(v.alerts, 'alerts', 40)};
}
export class NotificationHTTPError extends Error {constructor(public status: number, detail: string) {super(detail);}}
export async function fetchNotifications(signal: AbortSignal): Promise<Notifications> {
 const response = await fetch('/api/presentation/notifications', {method: 'GET', cache: 'no-store', credentials: 'same-origin', signal});
 if (!response.ok) {let detail = `Notifications unavailable (${response.status})`; try {const error = object(await response.json(), ['code', 'detail']); id(error.code); detail = text(error.detail);} catch { /* Retain local diagnostic for malformed errors. */ } throw new NotificationHTTPError(response.status, detail);}
 const source = await response.text(); if (new TextEncoder().encode(source).length > 1048576) throw Error('Notification response exceeds size bound'); const value: unknown = JSON.parse(source); return readNotifications(value);
}
