import { useEffect, useRef, useState, type ReactNode } from "react";

export type PlayerOwner = { viewer: string; lease: string; direct: boolean };
type Frame = { source: string; frame: number; width: number; height: number; captured: number; session: string; encoding?: string; selection?: number };
type Gesture = { kind: string; x: number; y: number; button?: number; key?: string; delta?: number; view: Frame };
type Props = { session?: string; viewer: string; enabled: boolean; snapshot: string; children?: ReactNode;
  owner?: PlayerOwner | null; onError?: (message: string) => void };

function afterPaint(callback: () => void) {
  requestAnimationFrame(() => {
    // A posted task runs after this rendering update without consuming another
    // display interval merely to acknowledge the frame already drawn.
    const task = new MessageChannel();
    task.port1.onmessage = () => { task.port1.close(); task.port2.close(); callback(); };
    task.port2.postMessage(null);
  });
}

export function imagePoint(rect: { left: number; top: number; width: number; height: number }, width: number, height: number, x: number, y: number) {
  const scale = Math.min(rect.width / width, rect.height / height);
  if (!(scale > 0)) return null;
  const px = (x - rect.left - (rect.width - width * scale) / 2) / scale;
  const py = (y - rect.top - (rect.height - height * scale) / 2) / scale;
  return px >= 0 && py >= 0 && px < width && py < height ? { x: Math.floor(px), y: Math.floor(py) } : null;
}

export default function GameVideo({ session, viewer, enabled, snapshot, children, owner, onError }: Props) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const displayed = useRef<Frame | null>(null);
  const [live, setLive] = useState(false), [hasFrame, setHasFrame] = useState(false);
  const [visible, setVisible] = useState(!document.hidden), [attempt, setAttempt] = useState(0);
  const [fps, setFps] = useState<number | null>(null);
  const failures = useRef(0), queue = useRef<Gesture[]>([]), working = useRef(false), order = useRef(0);
  const hardwareFailed = useRef(false);
  const inputSample = useRef<{ at: number; selection: number } | null>(null);
  const [encoding, setEncoding] = useState('');
  const activeOwner = useRef(owner), held = useRef(new Set<string>()), lastPointer = useRef({ x: 0, y: 0 });
  const cancelledLease = useRef<string | undefined>(undefined);
  activeOwner.current = owner?.lease === cancelledLease.current ? null : owner;
  useEffect(() => { order.current = 0; queue.current = []; held.current.clear(); }, [owner?.lease]);
  useEffect(() => { displayed.current = null; inputSample.current = null; setHasFrame(false); failures.current = 0; }, [session]);
  useEffect(() => { if (!live) setHasFrame(false); }, [snapshot]);
  useEffect(() => {
    const change = () => setVisible(!document.hidden);
    document.addEventListener("visibilitychange", change);
    return () => document.removeEventListener("visibilitychange", change);
  }, []);

  function cancel(message?: string) {
    inputSample.current = null;
    queue.current = []; held.current.clear();
    const current = activeOwner.current;
    cancelledLease.current = current?.lease;
    if (message) onError?.(message);
    activeOwner.current = null;
    if (current) void fetch("/api/input/release", { method: "POST", keepalive: true,
      headers: { "X-RimBot": "1", "Content-Type": "application/json" },
      body: JSON.stringify({ session_id: session, viewer_id: current.viewer, lease_id: current.lease }),
    }).catch(() => {});
  }
  async function pump() {
    if (working.current) return;
    working.current = true;
    const lease = activeOwner.current?.lease;
    try {
      while (queue.current.length && activeOwner.current?.lease === lease) {
        const event = queue.current.shift()!;
        const current = activeOwner.current!;
        if (Date.now() / 1000 - event.view.captured > .6) throw Error("The view changed before input could be sent. Take control again.");
        const { view, ...input } = event;
        if (input.kind === 'down' && input.button === 0 && view.selection !== undefined) {
          inputSample.current = { at: performance.now(), selection: view.selection };
        }
        const response = await fetch("/api/input/event", { method: "POST",
          headers: { "X-RimBot": "1", "Content-Type": "application/json" },
          body: JSON.stringify({ session_id: session, viewer_id: current.viewer, lease_id: lease,
            source: view.source, frame: view.frame, order: ++order.current, ...input }),
        });
        if (!response.ok) {
          const result = await response.json();
          throw Error(typeof result.detail === "string" ? result.detail : "Input was not confirmed; inspect the view.");
        }
      }
    } catch (error) { if (activeOwner.current?.lease === lease) cancel(String(error)); }
    finally { working.current = false; if (queue.current.length) void pump(); }
  }
  function send(kind: string, point = lastPointer.current, extra: Partial<Gesture> = {}) {
    const view = displayed.current;
    if (!enabled || !visible || !live || !activeOwner.current?.direct || !view || view.session !== session) return;
    if (Date.now() / 1000 - view.captured > .6) { cancel("Video is delayed. Take control again when the view is live."); return; }
    if (kind === "move" && queue.current.at(-1)?.kind === "move") queue.current.pop();
    if (queue.current.length >= 16) { cancel("Input could not keep up. Held input was released; take control again."); return; }
    queue.current.push({ kind, ...point, ...extra, view });
    void pump();
  }
  function modifiers(event: { shiftKey: boolean; ctrlKey: boolean; altKey: boolean }) {
    for (const [key, down] of [["ShiftLeft", event.shiftKey], ["ControlLeft", event.ctrlKey], ["AltLeft", event.altKey]] as const) {
      if (down && !held.current.has(key)) { held.current.add(key); send("keyDown", undefined, { key }); }
      if (!down && held.current.has(key)) { held.current.delete(key); send("keyUp", undefined, { key }); }
    }
  }
  function point(event: { clientX: number; clientY: number }) {
    const view = displayed.current;
    return view && canvas.current ? imagePoint(canvas.current.getBoundingClientRect(), view.width, view.height, event.clientX, event.clientY) : null;
  }
  useEffect(() => {
    const element = canvas.current!;
    const wheel = (event: WheelEvent) => {
      if (!owner?.direct || !live) return;
      event.preventDefault();
      const at = point(event);
      if (at && event.deltaY) { modifiers(event); send("wheel", at, { delta: event.deltaY < 0 ? -1 : 1 }); }
    };
    element.addEventListener("wheel", wheel, { passive: false });
    return () => element.removeEventListener("wheel", wheel);
  }, [owner, live, session, enabled, visible]);

  useEffect(() => {
    setLive(false); setFps(null);
    if (!enabled || !visible || !session || !globalThis.WebSocket) return;
    let stopped = false, last = performance.now(), count = 0, since = performance.now();
    let retry: ReturnType<typeof setTimeout> | undefined;
    const query = new URLSearchParams({ session_id: session, viewer, connection_id: crypto.randomUUID() });
    if ('VideoDecoder' in globalThis && !hardwareFailed.current) query.set('hardware', 'true');
    let decoder: VideoDecoder | undefined, decoderConfig = '';
    let decoded: ((frame: VideoFrame) => void) | undefined;
    let decodeFailure: ((error: Error) => void) | undefined;
    const socket = new WebSocket(`${location.protocol === "https:" ? "wss:" : "ws:"}//${location.host}/api/video/frames?${query}`, "rimbot-view-v1");
    socket.binaryType = "arraybuffer";
    const fail = () => {
      if (stopped) return;
      stopped = true; setLive(false); socket.close();
      queue.current = [];
      retry = setTimeout(() => setAttempt(n => n + 1), Math.min(30000, 3000 * 2 ** Math.min(failures.current++, 4)));
    };
    socket.onerror = fail; socket.onclose = fail;
    socket.onmessage = async event => {
      let bitmap: ImageBitmap | VideoFrame | undefined;
      try {
        const bytes = event.data as ArrayBuffer;
        const size = new DataView(bytes).getUint32(0, true);
        if (size > 2048 || size + 4 >= bytes.byteLength) throw Error("Invalid video frame");
        const frame = JSON.parse(new TextDecoder().decode(new Uint8Array(bytes, 4, size))) as Frame;
        if (frame.session !== session || !(frame.width > 0 && frame.width <= 3840 && frame.height > 0 && frame.height <= 2160)
            || Date.now() / 1000 - frame.captured > .75) throw Error("Stale video frame");
        if (frame.encoding === 'h264') {
          const data = new Uint8Array(bytes, 4 + size);
          let codec = '';
          for (let i = 0; i + 6 < data.length; i++) {
            if (data[i] === 0 && data[i + 1] === 0 && data[i + 2] === 1 && (data[i + 3] & 31) === 7) {
              codec = 'avc1.' + [...data.slice(i + 4, i + 7)].map(n => n.toString(16).padStart(2, '0')).join('');
              break;
            }
          }
          if (!codec) throw Error('Missing independent video configuration');
          const configuration = `${codec}:${frame.width}:${frame.height}`;
          if (configuration !== decoderConfig) {
            decoder?.close();
            decoder = new VideoDecoder({ output: value => { if (decoded) decoded(value); else value.close(); },
              error: error => { hardwareFailed.current = true; decodeFailure?.(error); fail(); } });
            decoder.configure({ codec, codedWidth: frame.width, codedHeight: frame.height, optimizeForLatency: true });
            decoderConfig = configuration;
          }
          bitmap = await new Promise<VideoFrame>((resolve, reject) => {
            decoded = resolve; decodeFailure = reject;
            decoder!.decode(new EncodedVideoChunk({ type: 'key', timestamp: Math.round(frame.captured * 1e6), data }));
          });
          decoded = undefined; decodeFailure = undefined;
        } else bitmap = await createImageBitmap(new Blob([bytes.slice(4 + size)], { type: "image/jpeg" }));
        if (stopped) return;
        const element = canvas.current!;
        if (element.width !== frame.width || element.height !== frame.height) {
          element.width = frame.width; element.height = frame.height;
        }
        element.getContext("2d")!.drawImage(bitmap, 0, 0);
        displayed.current = frame;
        afterPaint(() => {
          if (stopped) return;
          const sample = inputSample.current;
          const selectionEffectMs = sample && frame.selection !== undefined && frame.selection !== sample.selection
            && performance.now() - sample.at <= 2000 ? performance.now() - sample.at : undefined;
          if (selectionEffectMs !== undefined || (sample && performance.now() - sample.at > 2000)) inputSample.current = null;
          socket.send(JSON.stringify({ frame: frame.frame, displayed: Date.now() / 1000, selectionEffectMs }));
          last = performance.now(); count++;
          if (last - since >= 1000) { setFps(count * 1000 / (last - since)); count = 0; since = last; }
          setLive(true); setHasFrame(true); failures.current = 0;
          setEncoding(frame.encoding === 'h264' ? ' · GPU video' : '');
        });
      } catch { if (decoder) hardwareFailed.current = true; fail(); }
      finally { bitmap?.close(); }
    };
    const watchdog = setInterval(() => { if (performance.now() - last > 2500) fail(); }, 250);
    return () => { stopped = true; clearInterval(watchdog); clearTimeout(retry); socket.close(); decoder?.close(); decodeFailure?.(Error('View closed')); queue.current = []; };
  }, [session, viewer, enabled, visible, attempt]);

  return <>
    <canvas ref={canvas} aria-label="Live RimWorld colony" tabIndex={owner?.direct ? 0 : -1}
      style={{ display: hasFrame ? "block" : "none", width: "100%", height: "100%", objectFit: "contain", touchAction: "none", cursor: owner?.direct && live ? "crosshair" : "default" }}
      onContextMenu={event => { if (owner?.direct) event.preventDefault(); }}
      onPointerDown={event => {
        if (!owner?.direct || !live) return;
        const at = point(event); if (!at) return;
        event.preventDefault(); event.currentTarget.focus({ preventScroll: true }); event.currentTarget.setPointerCapture(event.pointerId);
        held.current.add(`button:${event.button}`);
        lastPointer.current = at; modifiers(event); send("down", at, { button: event.button });
      }}
      onPointerMove={event => { const at = point(event); if (at) { lastPointer.current = at; send("move", at); } }}
      onPointerUp={event => {
        if (!owner?.direct) return;
        held.current.delete(`button:${event.button}`);
        const at = point(event);
        if (at) send("up", at, { button: event.button }); else cancel("Drag left the image; held input was released.");
        if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
      }}
      onPointerCancel={() => cancel()}
      onBlur={() => { if (held.current.size) cancel(); }}
      onKeyDown={event => { if (!owner?.direct || !live) return; event.preventDefault(); if (!event.repeat) { held.current.add(event.code); send("keyDown", undefined, { key: event.code }); } }}
      onKeyUp={event => { if (!owner?.direct) return; event.preventDefault(); held.current.delete(event.code); send("keyUp", undefined, { key: event.code }); }} />
    {!hasFrame && snapshot && <img src={snapshot} alt="Current RimWorld colony" />}
    {!hasFrame && !snapshot && children}
    <span className="video-transport" role="status">
      {live ? `Live video${fps !== null ? ` · ${fps.toFixed(0)} fps` : ""}${encoding}${owner?.direct ? " · Player control" : ""}` : enabled ? "Snapshots · connecting video" : "Video paused"}
    </span>
  </>;
}
