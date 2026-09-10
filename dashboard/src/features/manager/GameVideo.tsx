import { useEffect, useRef, useState, type ReactNode } from "react";

type Props = { session?: string; viewer: string; enabled: boolean; snapshot: string; children?: ReactNode };
const metric = (value: unknown): number | null => typeof value === "number" && Number.isFinite(value) && value >= 0 ? value : null;

export default function GameVideo({ session, viewer, enabled, snapshot, children }: Props) {
  const video = useRef<HTMLVideoElement>(null);
  const [live, setLive] = useState(false);
  const [frozen, setFrozen] = useState<{ session?: string; url: string } | null>(null);
  const [attempt, setAttempt] = useState(0);
  const failures = useRef(0);
  const [stats, setStats] = useState<{ fps: number | null; dropped: number | null; jitterMs: number | null } | null>(null);
  useEffect(() => setFrozen(null), [session, snapshot]);
  useEffect(() => { failures.current = 0; }, [session]);
  const [visible, setVisible] = useState(!document.hidden);
  useEffect(() => {
    const change = () => setVisible(!document.hidden);
    document.addEventListener("visibilitychange", change);
    return () => document.removeEventListener("visibilitychange", change);
  }, []);
  useEffect(() => {
    setLive(false);
    setStats(null);
    if (!enabled || !visible || !session || !globalThis.RTCPeerConnection) return;
    let stopped = false;
    let lastFrame = performance.now();
    let callback = 0;
    let previousTime = -1;
    let started = false;
    const element = video.current!;
    const pc = new RTCPeerConnection({ iceServers: [] });
    const abort = new AbortController();
    const connectionId = crypto.randomUUID();
    let closeSent = false;
    let retry: ReturnType<typeof setTimeout> | undefined;
    let iceTimeout: ReturnType<typeof setTimeout> | undefined;
    let statsBusy = false;
    let previousStats: { frames: number; at: number } | undefined;
    let retainedFrame: HTMLCanvasElement | undefined;
    let retainedAt = -Infinity;
    const release = () => {
      if (closeSent) return;
      closeSent = true;
      fetch("/api/video/close", {
        method: "POST", keepalive: true,
        headers: { "X-RimBot": "1", "Content-Type": "application/json" },
        body: JSON.stringify({ session_id: session, viewer, connection_id: connectionId }),
      }).catch(() => {});
    };
    const sampleFrame = () => {
      if (performance.now() - retainedAt < 500) return;
      if (element.readyState < 2 || !element.videoWidth) return;
      const canvas = retainedFrame || document.createElement("canvas");
      canvas.width = element.videoWidth;
      canvas.height = element.videoHeight;
      const context = canvas.getContext("2d");
      if (!context) return;
      try {
        context.drawImage(element, 0, 0);
        retainedFrame = canvas;
        retainedAt = performance.now();
      } catch { /* Cleanup still closes the peer if the last frame cannot be copied. */ }
    };
    const retain = () => {
      // A closed media track can already be black. Retain the last presented sample.
      try {
        if (retainedFrame) setFrozen({ session, url: retainedFrame.toDataURL("image/jpeg", 0.85) });
      } catch { /* Keep the existing snapshot if image serialization is unavailable. */ }
    };
    const fail = () => {
      if (stopped) return;
      retain();
      stopped = true;
      setLive(false);
      abort.abort();
      pc.close();
      release();
      clearTimeout(iceTimeout);
      retry = setTimeout(() => setAttempt((value) => value + 1), Math.min(30000, 3000 * 2 ** Math.min(failures.current++, 4)));
    };
    const presented = () => {
      if (stopped) return;
      lastFrame = performance.now();
      setLive(true);
      started = true;
      failures.current = 0;
      sampleFrame();
      callback = element.requestVideoFrameCallback(presented);
    };
    pc.ontrack = ({ track }) => {
      if (stopped) { track.stop(); return; }
      element.srcObject = new MediaStream([track]);
      element.play().catch(fail);
      if (element.requestVideoFrameCallback) callback = element.requestVideoFrameCallback(presented);
    };
    element.onloadeddata = () => {
      if (!stopped) { lastFrame = performance.now(); started = true; setLive(true); }
    };
    pc.onconnectionstatechange = () => {
      if (["failed", "disconnected", "closed"].includes(pc.connectionState)) fail();
    };
    const timer = setInterval(() => {
      if (stopped) return;
      if (!element.requestVideoFrameCallback && element.readyState >= 2 && element.currentTime !== previousTime) {
        previousTime = element.currentTime;
        lastFrame = performance.now();
        setLive(true);
        started = true;
        sampleFrame();
      }
      if (performance.now() - lastFrame > (started ? 2500 : 10000)) fail();
    }, 500);
    const statsTimer = setInterval(async () => {
      if (stopped || statsBusy || !pc.getStats) return;
      statsBusy = true;
      try {
        const reports = await pc.getStats();
        if (stopped) return;
        reports.forEach((report) => {
          if (report.type !== "inbound-rtp" || report.kind !== "video") return;
          const frames = metric(report.framesDecoded);
          const elapsed = previousStats ? report.timestamp - previousStats.at : 0;
          const fps = elapsed > 0 && frames !== null ? metric((frames - previousStats!.frames) * 1000 / elapsed) : metric(report.framesPerSecond);
          if (frames !== null) previousStats = { frames, at: report.timestamp };
          setStats({ fps, dropped: metric(report.framesDropped),
            jitterMs: report.jitterBufferEmittedCount ? metric(1000 * report.jitterBufferDelay / report.jitterBufferEmittedCount) : null });
        });
      } catch { /* Statistics are advisory; frame callbacks own the stall watchdog. */ }
      finally { statsBusy = false; }
    }, 1000);
    const connect = async () => {
      pc.addTransceiver("video", { direction: "recvonly" });
      await pc.setLocalDescription(await pc.createOffer());
      // Complete host candidates in one bounded HTTP exchange; no external STUN/TURN.
      if (pc.iceGatheringState !== "complete") await new Promise<void>((resolve, reject) => {
        iceTimeout = setTimeout(() => reject(Error("ICE gathering timed out")), 4000);
        pc.onicegatheringstatechange = () => {
          if (pc.iceGatheringState === "complete") { clearTimeout(iceTimeout); resolve(); }
        };
      });
      if (stopped) return;
      const response = await fetch("/api/video/offer", {
        method: "POST",
        headers: { "X-RimBot": "1", "Content-Type": "application/json" },
        signal: abort.signal,
        body: JSON.stringify({ session_id: session, viewer, connection_id: connectionId, sdp: pc.localDescription!.sdp }),
      });
      if (!response.ok) throw Error("Continuous video unavailable");
      const answer = await response.json();
      if (!stopped) await pc.setRemoteDescription(answer);
    };
    connect().catch(fail);
    return () => {
      retain();
      stopped = true;
      abort.abort();
      clearInterval(timer);
      clearInterval(statsTimer);
      clearTimeout(iceTimeout);
      clearTimeout(retry);
      if (callback) element.cancelVideoFrameCallback(callback);
      pc.close();
      release();
      element.onloadeddata = null;
      element.srcObject = null;
    };
  }, [session, viewer, enabled, visible, attempt]);
  const still = frozen?.session === session ? frozen?.url : "";
  return <>
    <video ref={video} autoPlay muted playsInline aria-label="Live RimWorld colony"
      style={{ display: live ? "block" : "none", width: "100%", height: "100%", objectFit: "contain" }} />
    {!live && (still || snapshot) && <img src={still || snapshot} alt="Current RimWorld colony" />}
    {!live && !still && !snapshot && children}
    <span className="video-transport" role="status" title={stats ? `Decoded frame rate: ${stats.fps?.toFixed(1) ?? "unavailable"}. Frames dropped: ${stats.dropped ?? "unavailable"}. Average jitter buffer: ${stats.jitterMs?.toFixed(1) ?? "unavailable"} ms. These are browser delivery statistics, not capture-to-display latency.` : undefined}>
      {live ? `Live video${stats?.fps != null ? ` · ${stats.fps.toFixed(0)} fps` : ""}` : enabled ? "Snapshots · connecting video" : "Video paused"}
    </span>
  </>;
}
