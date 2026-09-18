export type PresentationWorld = {colonyId: string; mapId: number; loadToken: string};
export type PresentationContext = {identity: PresentationWorld; tick: string; nativeGeneration: string | null};
export type Listing = {totalCount: number | null; returnedCount: number | null; complete: boolean | null; truncated: boolean | null};
type Cell = {x: number | null; z: number | null};
export type Camera = {context: PresentationContext; mapPosition: Cell | null; rootSize: number | null; zoomRootSize: number | null; minimumRootSize: number | null; maximumRootSize: number | null; nativeZoomRange: string | null; zoomExtensionEnabled: boolean | null; viewRect: {minX: number | null; minZ: number | null; maxX: number | null; maxZ: number | null} | null};
export type SelectedObject = {id: string | null; nativeKind: string | null; nativeType: string | null; label: string | null; defName: string | null; mapId: number | null; position: Cell | null; inspectLabel: string | null; inspectText: string | null};
export type Selection = {context: PresentationContext; fingerprint: string | null; selectedObjects: SelectedObject[]; listing: Listing | null; visibleGizmoCount: number | null};
export type Named = {defName: string | null; label: string | null};
export type DossierSkill = Named & {level: number | null; passion: string | null; disabled: boolean};
export type DossierHediff = Named & {partLabel: string | null; severityLabel: string | null; visible: boolean; bad: boolean | null};
export type DossierThought = {label: string | null; count: number | null; moodOffsetTotal: number | null};
// The observation PawnState the roster attaches; ProtoJSON keys the dashboard
// does not show are ignored rather than rejected.
export type Dossier = {
  needs: {food: number | null; rest: number | null; mood: number | null; joy: number | null; hungerCategory: string | null; breakRisk: string | null};
  health: {summaryFraction: number | null; needsTend: boolean | null; bleeding: boolean | null; pain: number | null; hediffs: DossierHediff[]};
  gear: {weapons: Named[]; apparel: Named[]};
  biography: {biologicalAgeYears: number | null; childhood: Named | null; adulthood: Named | null; skills: DossierSkill[]; traits: Array<Named & {degree: number | null}>};
  thoughts: DossierThought[];
  job: string | null; mentalState: string | null; downed: boolean; drafted: boolean; inBed: boolean;
};
export type Colonist = {pawnId: string | null; name: string | null; mapId: number | null; spawned: boolean | null; position: Cell | null; dossier: Dossier | null};
export type Roster = {context: PresentationContext; colonists: Colonist[]; listing: Listing | null};
function isObject(v: unknown): v is Record<string, unknown> {return typeof v === 'object' && v !== null && !Array.isArray(v);}
function object(v: unknown, keys: string[]): Record<string, unknown> {if (!isObject(v) || Object.keys(v).some(key => !keys.includes(key))) throw Error('Invalid presentation fields'); return v;}
function text(v: unknown): string {if (typeof v !== 'string' || /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(v)) throw Error('Invalid presentation text'); return v;}
function id(v: unknown): string {const s = text(v); if (!s.trim() || s.includes('\0') || new TextEncoder().encode(s).length > 256) throw Error('Invalid presentation identity'); return s;}
function fingerprint(v: unknown): string {const s = text(v); if (new TextEncoder().encode(s).length > 4096) throw Error('Oversized presentation fingerprint'); return s;}
function finite(v: unknown): number {if (typeof v !== 'number' || !Number.isFinite(v)) throw Error('Invalid presentation number'); return v;}
function integer(v: unknown): number {const n = finite(v); if (!Number.isInteger(n) || n < -2147483648 || n > 2147483647) throw Error('Invalid int32'); return n;}
function mapID(v: unknown): number {const n = integer(v); if (n < 0) throw Error('Invalid map'); return n;}
function uint(v: unknown): number {const n = finite(v); if (!Number.isInteger(n) || n < 0 || n > 4294967295) throw Error('Invalid uint32'); return n;}
function bool(v: unknown): boolean {if (typeof v !== 'boolean') throw Error('Invalid presentation flag'); return v;}
function decimal(v: unknown, max = 18446744073709551615n): string {const s = text(v); if (!/^(0|[1-9][0-9]{0,19})$/.test(s) || BigInt(s) > max) throw Error('Invalid presentation integer'); return s;}
function optional<T>(v: unknown, read: (value: unknown) => T): T | null {return v === undefined || v === null ? null : read(v);}
function array<T>(v: unknown, max: number, read: (value: unknown) => T): T[] {if (v === undefined || v === null) return []; if (!Array.isArray(v) || v.length > max) throw Error('Invalid presentation collection'); return v.map(read);}
function context(v: unknown): PresentationContext {const c = object(v, ['identity', 'tick', 'nativeGeneration']), w = object(c.identity, ['colonyId', 'mapId', 'loadToken']); return {identity: {colonyId: id(w.colonyId), mapId: mapID(w.mapId), loadToken: id(w.loadToken)}, tick: decimal(c.tick, 9223372036854775807n), nativeGeneration: optional(c.nativeGeneration, decimal)};}
function cell(v: unknown, reader = mapID): Cell {const c = object(v, ['x', 'z']); return {x: optional(c.x, reader), z: optional(c.z, reader)};}
function listing(v: unknown, count: number): Listing {const l = object(v, ['totalCount', 'returnedCount', 'complete', 'truncated']); const result = {totalCount: optional(l.totalCount, uint), returnedCount: optional(l.returnedCount, uint), complete: optional(l.complete, bool), truncated: optional(l.truncated, bool)};
  if (result.returnedCount !== null && result.returnedCount !== count || result.totalCount !== null && result.totalCount < count || result.complete && (result.truncated || result.totalCount !== null && result.totalCount !== count) || result.truncated && result.totalCount !== null && result.totalCount <= count) throw Error('Inconsistent presentation listing'); return result;}
function unique(ids: Array<string | null>) {const known = ids.filter(value => value !== null); if (new Set(known).size !== known.length) throw Error('Duplicate presentation identity');}
function reply(value: unknown, key: string): unknown {const v = object(value, [key]); if (!(key in v)) throw Error('Missing presentation result'); return v[key];}
export function readCamera(value: unknown): Camera {
  const c = object(reply(value, 'camera'), ['context', 'mapPosition', 'rootSize', 'zoomRootSize', 'minimumRootSize', 'maximumRootSize', 'nativeZoomRange', 'zoomExtensionEnabled', 'viewRect']);
  const result: Camera = {context: context(c.context), mapPosition: optional(c.mapPosition, v => cell(v, finite)), rootSize: optional(c.rootSize, finite), zoomRootSize: optional(c.zoomRootSize, finite), minimumRootSize: optional(c.minimumRootSize, finite), maximumRootSize: optional(c.maximumRootSize, finite), nativeZoomRange: optional(c.nativeZoomRange, text), zoomExtensionEnabled: optional(c.zoomExtensionEnabled, bool), viewRect: optional(c.viewRect, value => {const r = object(value, ['minX', 'minZ', 'maxX', 'maxZ']); return {minX: optional(r.minX, integer), minZ: optional(r.minZ, integer), maxX: optional(r.maxX, integer), maxZ: optional(r.maxZ, integer)};})};
  if ([result.rootSize, result.zoomRootSize, result.minimumRootSize, result.maximumRootSize].some(v => v !== null && v < 0) || result.minimumRootSize !== null && result.maximumRootSize !== null && result.minimumRootSize > result.maximumRootSize) throw Error('Invalid camera zoom bounds');
  const r = result.viewRect; if (r && (r.minX !== null && r.maxX !== null && r.minX > r.maxX || r.minZ !== null && r.maxZ !== null && r.minZ > r.maxZ)) throw Error('Invalid camera view bounds'); return result;
}
export function readSelection(value: unknown): Selection {
  const s = object(reply(value, 'selection'), ['context', 'fingerprint', 'selectedObjects', 'listing', 'visibleGizmoCount']);
  const selectedObjects = array(s.selectedObjects, 4096, value => {const v = object(value, ['id', 'nativeKind', 'nativeType', 'label', 'defName', 'mapId', 'position', 'inspectLabel', 'inspectText']); return {id: optional(v.id, id), nativeKind: optional(v.nativeKind, text), nativeType: optional(v.nativeType, text), label: optional(v.label, text), defName: optional(v.defName, text), mapId: optional(v.mapId, mapID), position: optional(v.position, cell), inspectLabel: optional(v.inspectLabel, text), inspectText: optional(v.inspectText, text)};});
  unique(selectedObjects.map(v => v.id)); return {context: context(s.context), selectedObjects, fingerprint: optional(s.fingerprint, fingerprint), listing: optional(s.listing, v => listing(v, selectedObjects.length)), visibleGizmoCount: optional(s.visibleGizmoCount, uint)};
}
function loose(v: unknown): Record<string, unknown> {if (!isObject(v)) throw Error('Invalid presentation fields'); return v;}
function fraction(v: unknown): number {const n = finite(v); if (n < 0 || n > 1.5) throw Error('Invalid presentation fraction'); return n;}
function named(v: unknown): Named {const n = loose(v); return {defName: optional(n.defName, text), label: optional(n.label, text)};}
function gear(v: unknown): Named {const g = loose(v); return named(g.thing ?? {});}
export function readDossier(value: unknown, pawnId: string | null): Dossier {
  const d = loose(value), pawn = loose(d.pawn ?? {});
  if (optional(pawn.id, id) !== pawnId) throw Error('Dossier belongs to another colonist');
  const needs = loose(d.needs ?? {}), health = loose(d.health ?? {}), equipment = loose(d.equipment ?? {}), biography = loose(d.biography ?? {}), social = loose(d.social ?? {}), job = loose(d.job ?? {});
  return {
    needs: {food: optional(needs.food, fraction), rest: optional(needs.rest, fraction), mood: optional(needs.mood, fraction), joy: optional(needs.joy, fraction), hungerCategory: optional(needs.hungerCategory, text), breakRisk: optional(needs.breakRisk, text)},
    health: {summaryFraction: optional(health.summaryFraction, fraction), needsTend: optional(health.needsTend, bool), bleeding: optional(health.bleeding, bool), pain: optional(health.pain, finite),
      hediffs: array(health.hediffs, 256, v => {const h = loose(v); return {...named(h.definition ?? {}), partLabel: optional(h.partLabel, text), severityLabel: optional(h.severityLabel, text), visible: optional(h.visible, bool) ?? true, bad: optional(h.bad, bool)};})},
    gear: {weapons: array(equipment.equipped, 256, gear), apparel: array(equipment.apparel, 256, gear)},
    biography: {biologicalAgeYears: optional(biography.biologicalAgeYears, finite), childhood: optional(biography.childhood, named), adulthood: optional(biography.adulthood, named),
      skills: array(biography.skills, 256, v => {const s = loose(v); return {...named(s.definition ?? {}), level: optional(s.level, integer), passion: optional(s.passion, text), disabled: optional(s.disabled, bool) ?? false};}),
      traits: array(biography.traits, 256, v => {const t = loose(v); return {defName: optional(t.defName, text), label: null, degree: optional(t.degree, integer)};})},
    thoughts: array(social.memories, 256, v => {const t = loose(v); return {label: optional(t.label, text), count: optional(t.count, uint), moodOffsetTotal: optional(t.moodOffsetTotal, finite)};}),
    job: optional(job.defName, text), mentalState: optional(d.mentalState, text), downed: optional(d.downed, bool) ?? false, drafted: optional(d.drafted, bool) ?? false, inBed: optional(d.inBed, bool) ?? false,
  };
}
export function readRoster(value: unknown): Roster {
  const s = object(reply(value, 'roster'), ['context', 'colonists', 'listing']), actual = context(s.context);
  const colonists = array(s.colonists, 4096, value => {const v = object(value, ['pawnId', 'name', 'mapId', 'spawned', 'position', 'dossier']); const pawnId = optional(v.pawnId, id); return {pawnId, name: optional(v.name, text), mapId: optional(v.mapId, mapID), spawned: optional(v.spawned, bool), position: optional(v.position, cell), dossier: optional(v.dossier, d => readDossier(d, pawnId))};});
  unique(colonists.map(v => v.pawnId)); if (colonists.some(v => v.mapId !== null && v.mapId !== actual.identity.mapId)) throw Error('Roster belongs to another map'); return {context: actual, colonists, listing: optional(s.listing, v => listing(v, colonists.length))};
}
export function samePresentationWorld(a: PresentationWorld, b: PresentationWorld): boolean {return a.colonyId === b.colonyId && a.mapId === b.mapId && a.loadToken === b.loadToken;}
export class PresentationHTTPError extends Error {constructor(public status: number, detail: string) {super(detail);}}
export async function fetchPresentation<T>(kind: 'camera' | 'selection' | 'colonists', read: (value: unknown) => T, signal: AbortSignal): Promise<T> {
  const response = await fetch(`/api/presentation/${kind}`, {method: 'GET', cache: 'no-store', credentials: 'same-origin', signal});
  if (!response.ok) {let detail = `Presentation unavailable (${response.status})`; try {const error = object(await response.json(), ['code', 'detail']); id(error.code); detail = text(error.detail);} catch { /* Keep a bounded local diagnostic when error evidence is malformed. */ } throw new PresentationHTTPError(response.status, detail);}
  const source = await response.text(); if (new TextEncoder().encode(source).length > 1048576) throw Error('Presentation response exceeds size bound'); const value: unknown = JSON.parse(source); return read(value);
}
