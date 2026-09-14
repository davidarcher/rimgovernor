import {useEffect, useRef, useState} from 'react';
import {decodeVideoFrameMessage, demandRendering, leaseVideo, mintVideoTicket, videoEncodingNames, VideoHTTPError, type VideoFrame} from './videoStreamData';

type Status = 'idle' | 'leasing' | 'connecting' | 'streaming' | 'unsupported' | 'error';

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

export default function GameVideoGo({token, active}: {token: string | null; active: boolean}) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [status, setStatus] = useState<Status>('idle');
  const [message, setMessage] = useState('');
  const [frames, setFrames] = useState(0);

  useEffect(() => {
    if (!token || !active) {setStatus('idle'); setFrames(0); return;}
    let stopped = false, attempt = 0;
    let socket: WebSocket | null = null;
    let renewTimer: ReturnType<typeof setInterval> | undefined;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;
    const lifetime = new AbortController();
    const lastSequence = {current: -1n};

    const scheduleRetry = () => {
      if (stopped) return;
      attempt++;
      const delay = Math.min(30000, 1000 * 2 ** Math.min(attempt, 5));
      retryTimer = setTimeout(() => void connectOnce(), delay);
    };
    const connectOnce = async () => {
      if (stopped) return;
      try {
        setStatus('connecting');
        const ticket = await mintVideoTicket(token, lifetime.signal);
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
        const lease = await leaseVideo(token, 15, lifetime.signal);
        if (stopped) return;
        if (!lease.supported) {setStatus('unsupported'); setMessage('Live video is not supported by this game session.'); return;}
        await demandRendering(token, 15, lifetime.signal).catch(() => { /* best-effort; the stream still attempts to connect */ });
        renewTimer = setInterval(() => {
          void leaseVideo(token, 15, lifetime.signal).catch(() => { /* a lapsed lease surfaces as a socket close, which retries */ });
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
      void leaseVideo(token, 0).catch(() => { /* best-effort release on teardown */ });
    };
  }, [token, active]);

  return <div className="game-video">
    <canvas ref={canvasRef} className="game-video-canvas" aria-label="Live colony camera"/>
    {status !== 'streaming' && <p className="game-video-status" role="status">
      {status === 'idle' ? 'Video paused.'
        : status === 'leasing' ? 'Requesting a video lease…'
        : status === 'connecting' ? 'Connecting to the video stream…'
        : status === 'unsupported' ? `Live video unavailable. ${message}`
        : `Video connection issue. ${message} Retrying…`}
    </p>}
    {status === 'streaming' && frames === 0 && <p className="game-video-status" role="status">Connected — waiting for the first frame…</p>}
  </div>;
}
