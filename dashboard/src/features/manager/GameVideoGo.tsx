import {useEffect, useRef, useState} from 'react';
import {decodeVideoFrameMessage, demandRendering, leaseVideo, mintVideoTicket, videoEncodingNames, VideoHTTPError, type VideoFrame, type VideoSource} from './videoStreamData';

type Status = 'idle' | 'leasing' | 'connecting' | 'streaming' | 'unsupported' | 'unavailable' | 'error';

// Draws one decoded frame onto the canvas. PNG/JPEG frames decode through the
// browser's image pipeline; raw pixel frames are blitted by hand because the
// canvas API only accepts top-down RGBA ImageData.
async function blitFrame(ctx: CanvasRenderingContext2D, frame: VideoFrame): Promise<void> {
  const {width, height} = frame;
  if (width === 0 || height === 0) return;
  if (frame.encoding === 1 || frame.encoding === 2) {
    const bitmap = await createImageBitmap(new Blob([new Uint8Array(frame.data)], {type: frame.encoding === 1 ? 'image/png' : 'image/jpeg'}));
    ctx.canvas.width = width; ctx.canvas.height = height;
    ctx.drawImage(bitmap, 0, 0, width, height);
    bitmap.close();
    return;
  }
  if (frame.encoding === 4 || frame.encoding === 5) {
    if (frame.data.length < width * height * 4) throw Error('Video frame payload is shorter than its declared dimensions');
    ctx.canvas.width = width; ctx.canvas.height = height;
    const image = ctx.createImageData(width, height);
    for (let row = 0; row < height; row++) {
      const sourceRow = frame.encoding === 4 ? height - 1 - row : row; // RGBA32_BOTTOM_UP stores rows bottom-first
      const srcStart = sourceRow * width * 4, dstStart = row * width * 4;
      for (let x = 0; x < width; x++) {
        const s = srcStart + x * 4, d = dstStart + x * 4;
        if (frame.encoding === 5) { // BGRA32_TOP_DOWN -> RGBA
          image.data[d] = frame.data[s + 2]; image.data[d + 1] = frame.data[s + 1]; image.data[d + 2] = frame.data[s]; image.data[d + 3] = frame.data[s + 3];
        } else {
          image.data[d] = frame.data[s]; image.data[d + 1] = frame.data[s + 1]; image.data[d + 2] = frame.data[s + 2]; image.data[d + 3] = frame.data[s + 3];
        }
      }
    }
    ctx.putImageData(image, 0, 0);
    return;
  }
  throw Error(`Unsupported video encoding: ${videoEncodingNames[frame.encoding] ?? frame.encoding}`);
}

// One live feed of one source. The screen feed is the colony camera; a pawn
// or map source is a second camera the game renders for this tile alone.
// Each tile owns its lease (renewed every 10s under the same source, which
// extends the same buffer) and a WebSocket bound to that lease's sourceId.
export function VideoFeed({token, active, source, label, className}: {token: string | null; active: boolean; source: VideoSource; label: string; className?: string}) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [status, setStatus] = useState<Status>('idle');
  const [message, setMessage] = useState('');
  const [frames, setFrames] = useState(0);
  const sourceKey = JSON.stringify(source);

  useEffect(() => {
    if (!token || !active) {setStatus('idle'); setFrames(0); return;}
    const spec = JSON.parse(sourceKey) as VideoSource;
    let stopped = false, attempt = 0;
    let socket: WebSocket | null = null;
    let renewTimer: ReturnType<typeof setInterval> | undefined;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;
    const lifetime = new AbortController();
    const lastSequence = {current: -1n};
    // The native lease may republish under a new id (e.g. after a load); the
    // renewal keeps this current and the next connection follows it.
    const sourceId = {current: ''};

    const scheduleRetry = () => {
      if (stopped) return;
      attempt++;
      const delay = Math.min(30000, 1000 * 2 ** Math.min(attempt, 5));
      retryTimer = setTimeout(() => void connectOnce(), delay);
    };
    const connectOnce = async () => {
      if (stopped) return;
      // No source to connect to (the pawn is off the map); a renewal that
      // finds it again reconnects.
      if (!sourceId.current) return;
      try {
        setStatus('connecting');
        const ticket = await mintVideoTicket(token, lifetime.signal, sourceId.current);
        if (stopped) return;
        const url = new URL('/api/presentation/video-stream', location.href);
        url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
        url.searchParams.set('ticket', ticket.ticket);
        const ws = new WebSocket(url);
        ws.binaryType = 'arraybuffer';
        socket = ws;
        ws.onopen = () => {attempt = 0; if (!stopped) {setStatus('streaming'); setMessage('');}};
        ws.onmessage = event => {
          if (stopped || !(event.data instanceof ArrayBuffer)) return;
          try {
            const frame = decodeVideoFrameMessage(event.data);
            if (frame.sequence <= lastSequence.current) return; // stale or duplicate frame
            lastSequence.current = frame.sequence;
            const canvas = canvasRef.current;
            const ctx = canvas?.getContext('2d');
            if (ctx) void blitFrame(ctx, frame).catch(reason => setMessage(reason instanceof Error ? reason.message : 'Unable to draw video frame'));
            setFrames(count => count + 1);
          } catch (reason) {setMessage(reason instanceof Error ? reason.message : 'Unable to decode video frame');}
        };
        ws.onclose = () => {socket = null; if (!stopped) scheduleRetry();};
        ws.onerror = () => ws.close();
      } catch (reason) {
        if (stopped) return;
        if (reason instanceof VideoHTTPError && reason.status === 404) {setStatus('unsupported'); setMessage(reason.message); return;}
        setStatus('error'); setMessage(reason instanceof Error ? reason.message : 'Video connection unavailable');
        scheduleRetry();
      }
    };
    const start = async () => {
      try {
        setStatus('leasing');
        const lease = await leaseVideo(token, 15, spec, lifetime.signal);
        if (stopped) return;
        if (!lease.supported) {setStatus('unsupported'); setMessage('Live video is not supported by this game session.'); return;}
        const unavailable = (state: {active: boolean; unavailable?: string}) => {
          sourceId.current = '';
          setStatus('unavailable'); setMessage(state.unavailable ?? 'The source is not available right now.');
          socket?.close();
        };
        if (lease.active) sourceId.current = lease.sourceId; else unavailable(lease);
        await demandRendering(token, 15, lifetime.signal).catch(() => { /* best-effort; the stream still attempts to connect */ });
        renewTimer = setInterval(() => {
          void leaseVideo(token, 15, spec, lifetime.signal).then(renewed => {
            if (stopped) return;
            if (!renewed.active) {unavailable(renewed); return;}
            // The source may have been re-created (after a load, or the pawn
            // returned): follow the new id on a fresh connection.
            if (renewed.sourceId !== sourceId.current) {
              const reconnect = !sourceId.current;
              sourceId.current = renewed.sourceId;
              if (reconnect) void connectOnce(); else socket?.close();
            }
          }).catch(() => { /* a lapsed lease surfaces as a socket close, which retries */ });
          void demandRendering(token, 15, lifetime.signal).catch(() => { /* best-effort */ });
        }, 10000);
        await connectOnce();
      } catch (reason) {
        if (stopped) return;
        setStatus('error'); setMessage(reason instanceof Error ? reason.message : 'Video lease unavailable');
        scheduleRetry();
      }
    };
    void start();
    return () => {
      stopped = true;
      lifetime.abort();
      if (retryTimer) clearTimeout(retryTimer);
      if (renewTimer) clearInterval(renewTimer);
      socket?.close();
      if (sourceId.current) void leaseVideo(token, 0, spec, undefined, sourceId.current).catch(() => { /* best-effort release on teardown */ });
    };
  }, [token, active, sourceKey]);

  return <div className={className ?? 'game-video'}>
    <canvas ref={canvasRef} className="game-video-canvas" aria-label={label}/>
    {status !== 'streaming' && <p className="game-video-status" role="status">
      {status === 'idle' ? 'Video paused.'
        : status === 'leasing' ? 'Requesting a video lease…'
        : status === 'connecting' ? 'Connecting to the video stream…'
        : status === 'unsupported' ? `Live video unavailable. ${message}`
        : status === 'unavailable' ? `${message} Waiting�`
        : `Video connection issue. ${message} Retrying…`}
    </p>}
    {status === 'streaming' && frames === 0 && <p className="game-video-status" role="status">Connected — waiting for the first frame…</p>}
  </div>;
}

export default function GameVideoGo({token, active}: {token: string | null; active: boolean}) {
  return <VideoFeed token={token} active={active} source={{kind: 'screen'}} label="Live colony camera"/>;
}

// The whole map at a glance, rendered by the game a few times a second.
export function MapOverviewGo({token, active}: {token: string | null; active: boolean}) {
  return <VideoFeed token={token} active={active} source={{kind: 'map', height: 800, framesPerSecond: 4}} label="Live map overview" className="game-video game-video-map"/>;
}

// A small camera following one colonist.
export function PawnFeedGo({token, active, pawnId, name}: {token: string | null; active: boolean; pawnId: string; name: string}) {
  return <figure className="pawn-feed">
    <VideoFeed token={token} active={active} source={{kind: 'pawn', pawnId, width: 320, height: 200, framesPerSecond: 15}} label={`Live feed following ${name}`} className="game-video game-video-feed"/>
    <figcaption>{name}</figcaption>
  </figure>;
}
