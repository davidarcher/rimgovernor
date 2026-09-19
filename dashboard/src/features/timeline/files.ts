// Turns the files of one case output directory (as a directory picker or a
// drop hands them over, with `webkitRelativePath` under the picked
// directory) into CaseFiles: the root recording with `service/` as launch
// 1, each `service-N/` with its own recording as launch N, `result.json`,
// and the harness's `NNNN-*.json` evidence.
import {parseFlight, segmentOrder, type Flight} from './flight';
import type {CaseFiles, LaunchFiles} from './model';

export type NamedFile = {path: string; text: () => Promise<string>};

const MAX_FLIGHT_BYTES = 256 * 1024 * 1024;

// caseRoot finds the shallowest directory among the paths that holds a
// result.json or flight.jsonl: the picked directory itself, or the one
// case inside a run directory the picker was pointed at.
export function caseRoot(paths: readonly string[]): string {
  const roots = new Set<string>();
  for (const p of paths) {
    const parts = p.split('/');
    const at = parts.findIndex(part => part === 'result.json' || part === 'flight.jsonl');
    if (at >= 0) roots.add(parts.slice(0, at).join('/'));
  }
  if (roots.size === 0) return '';
  return [...roots].sort((a, b) => a.length - b.length)[0];
}

export async function readCase(files: readonly NamedFile[]): Promise<{files: CaseFiles; root: string; skipped: string[]}> {
  const root = caseRoot(files.map(f => f.path));
  const prefix = root === '' ? '' : root + '/';
  const skipped: string[] = [];
  const own = files.filter(f => f.path.startsWith(prefix)).map(f => ({path: f.path.slice(prefix.length), text: f.text}));
  const byPath = new Map(own.map(f => [f.path, f]));
  const json = async (path: string): Promise<unknown> => {
    const file = byPath.get(path);
    if (file === undefined) return null;
    try {return JSON.parse(await file.text()) as unknown;} catch {skipped.push(`${path}: not JSON`); return null;}
  };
  const flight = async (dir: string): Promise<Flight | null> => {
    const base = 'flight.jsonl';
    const names = own.map(f => f.path).filter(p => p.startsWith(dir) && !p.slice(dir.length).includes('/')).map(p => p.slice(dir.length));
    const ordered = segmentOrder(base, names);
    if (ordered.length === 0) return null;
    const texts: string[] = [];
    let bytes = 0;
    for (const name of ordered) {
      const text = await byPath.get(dir + name)?.text() ?? '';
      bytes += text.length;
      if (bytes > MAX_FLIGHT_BYTES) {skipped.push(`${dir}${name}: recording exceeds ${MAX_FLIGHT_BYTES >> 20} MiB`); break;}
      texts.push(text);
    }
    return parseFlight(texts);
  };
  const http = async (dir: string) => {
    const rows: {name: string; body: unknown}[] = [];
    for (const f of own) {
      if (!f.path.startsWith(dir) || !/^http-\d+\.json$/.test(f.path.slice(dir.length))) continue;
      rows.push({name: f.path.slice(dir.length), body: await json(f.path)});
    }
    return rows.sort((a, b) => a.name.localeCompare(b.name));
  };
  const launches: LaunchFiles[] = [];
  const rootFlight = await flight('');
  const serviceDirs = [...new Set(own.map(f => /^(service(?:-\d+)?)\//.exec(f.path)?.[1]).filter((d): d is string => d !== undefined))].sort((a, b) => launchIndex(a) - launchIndex(b));
  for (const dir of serviceDirs) {
    const index = launchIndex(dir);
    const recording = index === 1 ? rootFlight : await flight(dir + '/');
    const stderr = await byPath.get(dir + '/stderr.log')?.text() ?? null;
    if (recording === null && stderr === null) continue;
    launches.push({name: dir, flight: recording ?? {rows: [], gaps: []}, stderr, http: await http(dir + '/')});
  }
  if (launches.length === 0 && rootFlight !== null) launches.push({name: 'flight.jsonl', flight: rootFlight, stderr: null, http: []});
  const evidence: {name: string; body: unknown}[] = [];
  for (const f of own) if (/^\d{4}-[^/]+\.json$/.test(f.path)) evidence.push({name: f.path, body: await json(f.path)});
  return {files: {result: await json('result.json'), launches, evidence: evidence.sort((a, b) => a.name.localeCompare(b.name))}, root, skipped};
}

function launchIndex(dir: string): number {
  const m = /^service-(\d+)$/.exec(dir);
  return m === null ? 1 : Number(m[1]);
}
