// The observed world and the authority generation shared by every panel's
// reading and request types.
export type World = {colonyId: string; mapId: number; loadToken: string};
export type Generation = {colony: string; map: number; load: string; plan: string; revision: string; native: string};
export function sameWorld(a: World, b: World): boolean {return a.colonyId === b.colonyId && a.mapId === b.mapId && a.loadToken === b.loadToken;}
