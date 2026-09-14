export type PawnImageFrame = {pawnId: string; view: 'portrait' | 'follow'; width: number; height: number; encoding: string; captureMethod: string; capturedUnixMs: number; readbackMs: number; data: Uint8Array};

function isObject(value: unknown): value is Record<string, unknown> {return typeof value === 'object' && value !== null && !Array.isArray(value);}
function object(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (!isObject(value) || keys.some(key => !Object.hasOwn(value, key))) throw Error('Invalid pawn image response fields');
  return value;
}
function text(value: unknown): string {if (typeof value !== 'string' || value.length > 4096) throw Error('Invalid pawn image text'); return value;}
function uint(value: unknown): number {if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) throw Error('Invalid pawn image number'); return value;}
function view(value: unknown): 'portrait' | 'follow' {if (value !== 'portrait' && value !== 'follow') throw Error('Unknown pawn image view'); return value;}
function bytes(value: unknown): Uint8Array {
  const encoded = text(value);
  const binary = atob(encoded);
  const result = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) result[i] = binary.charCodeAt(i);
  return result;
}

export class PawnImageHTTPError extends Error {constructor(public status: number, message: string) {super(message);}}

// The server encodes the captured frame (usually PNG) as base64 inside the JSON envelope.
export async function fetchPawnImage(token: string, pawnId: string, requestedView: 'portrait' | 'follow', signal?: AbortSignal): Promise<PawnImageFrame> {
  const response = await fetch('/api/presentation/pawn-image', {method: 'POST', cache: 'no-store', credentials: 'same-origin', signal,
    headers: {'Content-Type': 'application/json', 'X-RimGovernor-Player': token}, body: JSON.stringify({pawnId, view: requestedView})});
  const value: unknown = await response.json();
  if (!response.ok) {
    let detail = `Pawn image unavailable (${response.status})`;
    try {const failure = object(value, ['code', 'detail']); detail = text(failure.detail);} catch { /* keep the bounded fallback when error evidence is malformed */ }
    throw new PawnImageHTTPError(response.status, detail);
  }
  const root = object(value, ['pawnId', 'view', 'frame']);
  const frame = object(root.frame, ['width', 'height', 'encoding', 'captureMethod', 'capturedUnixMs', 'readbackMs', 'data']);
  return {pawnId: text(root.pawnId), view: view(root.view), width: uint(frame.width), height: uint(frame.height), encoding: text(frame.encoding), captureMethod: text(frame.captureMethod), capturedUnixMs: uint(frame.capturedUnixMs), readbackMs: uint(frame.readbackMs), data: bytes(frame.data)};
}

export function pawnImageObjectURL(frame: PawnImageFrame): string {
  const type = frame.encoding === 'MEDIA_ENCODING_PNG' ? 'image/png' : frame.encoding === 'MEDIA_ENCODING_JPEG' ? 'image/jpeg' : 'application/octet-stream';
  return URL.createObjectURL(new Blob([new Uint8Array(frame.data)], {type}));
}
