// The raid-threat slice of /api/player/colony (#395): the raid points the
// storyteller would draw now and the wealth split behind them. Every figure
// is null when the native census could not observe it.
export type ThreatStatus = {tick: number; raidPoints: number | null; wealthTotal: number | null; wealthItems: number | null; wealthBuildings: number | null; wealthPawns: number | null; shrines: ShrineStatus[] | null};
// One ancient shrine (#456): a casket group, sealed until breached; guards
// are unknown while sealed and count as alive until seen dead.
export type ShrineStatus = {id: string; sealed: boolean; inHome: boolean; caskets: number; filledCaskets: number; guardsKnown: boolean; guardsAlive: boolean; breachWalls: number};

export class ThreatHTTPError extends Error {constructor(public status: number, message: string) {super(message);}}

function figure(value: unknown, field: string): number | null {
  if (value === null) return null;
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) throw Error(`Invalid colony status ${field}`);
  return value;
}
function count(value: unknown, field: string): number {
  if (typeof value !== 'number' || !Number.isInteger(value) || value < 0) throw Error(`Invalid colony status ${field}`);
  return value;
}
function flag(value: unknown, field: string): boolean {
  if (typeof value !== 'boolean') throw Error(`Invalid colony status ${field}`);
  return value;
}
function readShrines(raw: unknown): ShrineStatus[] | null {
  if (raw === null || raw === undefined) return null;
  if (!Array.isArray(raw)) throw Error('Invalid colony status shrines');
  return raw.map((row: unknown) => {
    if (typeof row !== 'object' || row === null) throw Error('Invalid colony status shrines');
    const v = row as Record<string, unknown>;
    if (typeof v.id !== 'string' || v.id === '') throw Error('Invalid colony status shrines');
    return {id: v.id, sealed: flag(v.sealed, 'sealed'), inHome: flag(v.inHome, 'inHome'), caskets: count(v.caskets, 'caskets'), filledCaskets: count(v.filledCaskets, 'filledCaskets'),
      guardsKnown: flag(v.guardsKnown, 'guardsKnown'), guardsAlive: flag(v.guardsAlive, 'guardsAlive'), breachWalls: count(v.breachWalls, 'breachWalls')};
  });
}
export function readThreatStatus(raw: unknown): ThreatStatus {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) throw Error('Invalid colony status');
  const v = raw as Record<string, unknown>;
  if (typeof v.tick !== 'number' || !Number.isInteger(v.tick) || v.tick < 0) throw Error('Invalid colony status tick');
  return {tick: v.tick, raidPoints: figure(v.raidPoints, 'raidPoints'), wealthTotal: figure(v.wealthTotal, 'wealthTotal'),
    wealthItems: figure(v.wealthItems, 'wealthItems'), wealthBuildings: figure(v.wealthBuildings, 'wealthBuildings'), wealthPawns: figure(v.wealthPawns, 'wealthPawns'), shrines: readShrines(v.shrines)};
}
export async function fetchThreatStatus(signal?: AbortSignal): Promise<ThreatStatus> {
  const response = await fetch('/api/player/colony', {method: 'GET', cache: 'no-store', credentials: 'same-origin', signal});
  const value: unknown = await response.json();
  if (!response.ok) {
    const detail = typeof value === 'object' && value !== null && typeof (value as Record<string, unknown>).detail === 'string' ? (value as Record<string, string>).detail : `Colony status unavailable (${response.status})`;
    throw new ThreatHTTPError(response.status, detail);
  }
  return readThreatStatus(value);
}
