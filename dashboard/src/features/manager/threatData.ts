// The raid-threat slice of /api/player/colony (#395): the raid points the
// storyteller would draw now and the wealth split behind them. Every figure
// is null when the native census could not observe it.
export type ThreatStatus = {tick: number; raidPoints: number | null; wealthTotal: number | null; wealthItems: number | null; wealthBuildings: number | null; wealthPawns: number | null};

export class ThreatHTTPError extends Error {constructor(public status: number, message: string) {super(message);}}

function figure(value: unknown, field: string): number | null {
  if (value === null) return null;
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) throw Error(`Invalid colony status ${field}`);
  return value;
}
export function readThreatStatus(raw: unknown): ThreatStatus {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) throw Error('Invalid colony status');
  const v = raw as Record<string, unknown>;
  if (typeof v.tick !== 'number' || !Number.isInteger(v.tick) || v.tick < 0) throw Error('Invalid colony status tick');
  return {tick: v.tick, raidPoints: figure(v.raidPoints, 'raidPoints'), wealthTotal: figure(v.wealthTotal, 'wealthTotal'),
    wealthItems: figure(v.wealthItems, 'wealthItems'), wealthBuildings: figure(v.wealthBuildings, 'wealthBuildings'), wealthPawns: figure(v.wealthPawns, 'wealthPawns')};
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
