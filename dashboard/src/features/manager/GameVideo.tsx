import { useEffect, useRef, useState, type ReactNode } from "react";

type Props = { session?: string; viewer: string; enabled: boolean; snapshot: string; children?: ReactNode };

export default function GameVideo({ session, viewer, enabled, snapshot, children }: Props) {
  const video = useRef<HTMLVideoElement>(null);
  const [live, setLive] = useState(false);
  const [frozen, setFrozen] = useState("");
  useEffect(() => setFrozen(""), [session, snapshot]);
  const [visible, setVisible] = useState(!document.hidden);
  useEffect(() => {
    const change = () => setVisible(!document.hidden);
    document.addEventListener("visibilitychange", change);
    return () => document.removeEventListener("visibilitychange", change);
  }, []);
  useEffect(() => {
    setLive(false);
    if (!enabled || !visible || !session || !globalThis.RTCPeerConnection) return;
    let stopped = false;
    let lastFrame = performance.now();
    let callback = 0;
    let previousTime = -1;
    let started = false;
    const element = video.current!;
    const pc = new RTCPeerConnection({ iceServers: [] });
    const abort = new AbortController();
    const retain = () => {
      if (element.readyState < 2 || !element.videoWidth) return;
      const canvas = document.createElement("canvas");
      canvas.width = element.videoWidth;
      canvas.height = element.videoHeight;
      const context = canvas.getContext("2d");
      if (!context) return;
      context.drawImage(element, 0, 0);
      setFrozen(canvas.toDataURL("image/jpeg", 0.85));
    };
    const fail = () => {
      if (stopped) return;
      retain();
      stopped = true;
      setLive(false);
      abort.abort();
      pc.close();
    };
    const presented = () => {
      if (stopped) return;
      lastFrame = performance.now();
      setLive(true);
      started = true;
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
      }
      if (performance.now() - lastFrame > (started ? 2500 : 10000)) fail();
    }, 500);
    const connect = async () => {
      pc.addTransceiver("video", { direction: "recvonly" });
      await pc.setLocalDescription(await pc.createOffer());
      // Complete host candidates in one bounded HTTP exchange; no external STUN/TURN.
      if (pc.iceGatheringState !== "complete") await new Promise<void>((resolve, reject) => {
        const timeout = setTimeout(() => reject(Error("ICE gathering timed out")), 4000);
        pc.onicegatheringstatechange = () => {
          if (pc.iceGatheringState === "complete") { clearTimeout(timeout); resolve(); }
        };
      });
      if (stopped) return;
      const response = await fetch("/api/video/offer", {
        method: "POST",
        headers: { "X-RimBot": "1", "Content-Type": "application/json" },
        signal: abort.signal,
        body: JSON.stringify({ session_id: session, viewer, sdp: pc.localDescription!.sdp }),
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
      if (callback) element.cancelVideoFrameCallback(callback);
      pc.close();
      element.onloadeddata = null;
      element.srcObject = null;
    };
  }, [session, viewer, enabled, visible]);
  return <>
    <video ref={video} autoPlay muted playsInline aria-label="Live RimWorld colony"
      style={{ display: live ? "block" : "none", width: "100%", height: "100%", objectFit: "contain" }} />
    {!live && (frozen || snapshot) && <img src={frozen || snapshot} alt="Current RimWorld colony" />}
    {!live && !frozen && !snapshot && children}
    <span className="video-transport" role="status">{live ? "Live video" : enabled ? "Snapshots · continuous video unavailable" : "Video paused"}</span>
  </>;
}
