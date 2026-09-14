export type VideoState = {supported: boolean; active: boolean; sourceId: string; remainingLeaseMs: number; capturedFrames: number; framesPerSecond: number; pixelFormat: string; captureMethod: string};
export type RenderStatus = {supported: boolean; suspended: boolean; windowVisible: boolean; remainingLeaseMs: number};
export type VideoTicket = {ticket: string; expiresMs: number};

function isObject(value: unknown): value is Record<string, unknown> {return typeof value === 'object' && value !== null && !Array.isArray(value);}
function object(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (!isObject(value) || keys.some(key => !Object.hasOwn(value, key))) throw Error('Invalid video response fields');
  return value;
}
function text(value: unknown): string {if (typeof value !== 'string' || value.length > 4096) throw Error('Invalid video text'); return value;}
function bool(value: unknown): boolean {if (typeof value !== 'boolean') throw Error('Invalid video flag'); return value;}
function uint(value: unknown): number {if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) throw Error('Invalid video number'); return value;}

export class VideoHTTPError extends Error {constructor(public status: number, message: string) {super(message);}}

async function post<T>(path: string, token: string, body: unknown, read: (value: unknown) => T, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, {method: 'POST', cache: 'no-store', credentials: 'same-origin', signal,
    headers: {'Content-Type': 'application/json', 'X-RimGovernor-Player': token}, body: JSON.stringify(body)});
  const value: unknown = await response.json();
  if (!response.ok) {
    let detail = `Video operation unavailable (${response.status})`;
    try {const failure = object(value, ['code', 'detail']); detail = text(failure.detail);} catch { /* keep the bounded fallback when error evidence is malformed */ }
    throw new VideoHTTPError(response.status, detail);
  }
  return read(value);
}

function readVideoState(value: unknown): VideoState {
  const v = object(value, ['supported', 'active', 'sourceId', 'remainingLeaseMs', 'capturedFrames', 'framesPerSecond', 'pixelFormat', 'captureMethod']);
  return {supported: bool(v.supported), active: bool(v.active), sourceId: text(v.sourceId), remainingLeaseMs: uint(v.remainingLeaseMs), capturedFrames: uint(v.capturedFrames), framesPerSecond: uint(v.framesPerSecond), pixelFormat: text(v.pixelFormat), captureMethod: text(v.captureMethod)};
}
function readRenderStatus(value: unknown): RenderStatus {
  const v = object(value, ['supported', 'suspended', 'windowVisible', 'remainingLeaseMs']);
  return {supported: bool(v.supported), suspended: bool(v.suspended), windowVisible: bool(v.windowVisible), remainingLeaseMs: uint(v.remainingLeaseMs)};
}
function readTicket(value: unknown): VideoTicket {
  const v = object(value, ['ticket', 'expiresMs']);
  return {ticket: text(v.ticket), expiresMs: uint(v.expiresMs)};
}

// leaseSeconds 0 stops an active lease; the server requires it on every call (0-15).
export function leaseVideo(token: string, leaseSeconds: number, signal?: AbortSignal): Promise<VideoState> {
  return post('/api/presentation/video-lease', token, {leaseSeconds}, readVideoState, signal);
}
// leaseSeconds is required on every call (0-30); forces the native window to render so frames are capturable.
export function demandRendering(token: string, leaseSeconds: number, signal?: AbortSignal): Promise<RenderStatus> {
  return post('/api/presentation/render-demand', token, {leaseSeconds}, readRenderStatus, signal);
}
// A ticket is single-use and expires ~5s after minting; connect the WebSocket immediately after minting one.
export function mintVideoTicket(token: string, signal?: AbortSignal): Promise<VideoTicket> {
  return post('/api/presentation/video-stream/ticket', token, {}, readTicket, signal);
}

// Matches rimgovernor.presentation.v1.MediaEncoding.
export const videoEncodingNames: Record<number, string> = {0: 'unspecified', 1: 'png', 2: 'jpeg', 3: 'h264', 4: 'rgba32_bottom_up', 5: 'bgra32_top_down'};

export type VideoFrame = {sequence: bigint; width: number; height: number; encoding: number; captureMethod: number; capturedUnixMs: bigint; readbackMs: number; data: Uint8Array};
const frameHeaderSize = 34;
// Wire format: sequence u64, width u32, height u32, encoding u8, captureMethod u8, capturedUnixMs i64, readbackMs f64 (all little-endian), then raw payload.
export function decodeVideoFrameMessage(buffer: ArrayBuffer): VideoFrame {
  if (buffer.byteLength < frameHeaderSize) throw Error('Video frame message is shorter than its header');
  const view = new DataView(buffer);
  return {
    sequence: view.getBigUint64(0, true),
    width: view.getUint32(8, true),
    height: view.getUint32(12, true),
    encoding: view.getUint8(16),
    captureMethod: view.getUint8(17),
    capturedUnixMs: view.getBigInt64(18, true),
    readbackMs: view.getFloat64(26, true),
    data: new Uint8Array(buffer, frameHeaderSize),
  };
}
