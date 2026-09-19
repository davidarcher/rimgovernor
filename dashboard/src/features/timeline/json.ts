// Guards for reading recorded JSON (`unknown`) without trusting its shape.
// ProtoJSON writes int64 as strings, so `num` accepts a decimal string too.
export function isObject(v: unknown): v is Record<string, unknown> {return typeof v === 'object' && v !== null && !Array.isArray(v);}
export function str(v: unknown): string | null {return typeof v === 'string' ? v : null;}
export function num(v: unknown): number | null {
  if (typeof v === 'number') return Number.isFinite(v) ? v : null;
  if (typeof v === 'string' && /^-?\d+(\.\d+)?$/.test(v)) {const n = Number(v); return Number.isFinite(n) ? n : null;}
  return null;
}
export function bool(v: unknown): boolean | null {return typeof v === 'boolean' ? v : null;}
export function path(v: unknown, ...keys: string[]): unknown {
  let cur: unknown = v;
  for (const key of keys) {if (!isObject(cur)) return undefined; cur = cur[key];}
  return cur;
}
// compact renders a value as indented JSON, cut to `limit` characters for
// a hover card.
export function compact(v: unknown, limit = 4000): string {
  let text: string;
  try {text = JSON.stringify(v, null, 1) ?? '';} catch {text = String(v);}
  return text.length > limit ? text.slice(0, limit) + '\n…' : text;
}
