import { useEffect, useRef, useState } from "react";

export default function GameControls({
  sessionId,
  connected,
  stale,
  paused,
  headless,
  following,
  speed,
  pawns = [],
  onError,
}: {
  sessionId: string;
  connected: boolean;
  stale: boolean;
  paused?: boolean;
  headless?: boolean;
  following?: boolean;
  speed?: string;
  pawns?: { thing_id: string; name: string }[];
  onError: (error: string) => void;
}) {
  const [busy, setBusy] = useState(false),
    [notice, setNotice] = useState("");
  const pending = useRef(false);
  const [viewer] = useState(() => crypto.randomUUID());
  const [lease, setLease] = useState("");
  const accepting = useRef(true);
  useEffect(() => {
    accepting.current = !document.hidden;
    const focus = () => { accepting.current = !document.hidden; };
    const blur = () => { accepting.current = false; };
    window.addEventListener("focus", focus);
    window.addEventListener("blur", blur);
    document.addEventListener("visibilitychange", focus);
    return () => {
      accepting.current = false;
      window.removeEventListener("focus", focus);
      window.removeEventListener("blur", blur);
      document.removeEventListener("visibilitychange", focus);
    };
  }, []);
  useEffect(() => {
    if (!lease) return;
    let stopped = false;
    const body = JSON.stringify({ session_id: sessionId, viewer_id: viewer, lease_id: lease });
    const send = (path: string) => fetch("/api/input/" + path, {
      method: "POST", keepalive: true,
      headers: { "Content-Type": "application/json", "X-RimBot": "1" }, body,
    });
    const release = () => {
      if (stopped) return;
      stopped = true;
      setLease("");
      void send("release").catch(() => {});
    };
    const timer = window.setInterval(() => {
      if (stopped) return;
      void send("heartbeat").then(r => {
        if (!r.ok && !stopped) release();
      }).catch(() => { if (!stopped) release(); });
    }, 4000);
    const visibility = () => { if (document.hidden) release(); };
    window.addEventListener("blur", release);
    document.addEventListener("visibilitychange", visibility);
    return () => {
      window.clearInterval(timer);
      window.removeEventListener("blur", release);
      document.removeEventListener("visibilitychange", visibility);
      release();
    };
  }, [lease, sessionId, viewer]);
  async function act(path: string, body: object) {
    if (pending.current) return;
    pending.current = true;
    setBusy(true);
    setNotice("");
    onError("");
    try {
      const r = await fetch("/api/" + path, {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-RimBot": "1" },
        body: JSON.stringify({ session_id: sessionId,
          ...(lease || path.startsWith("input/") ? { viewer_id: viewer, lease_id: lease } : {}), ...body }),
      });
      const data = await r.json();
      if (!r.ok)
        throw Error(
          typeof data.detail === "string"
            ? data.detail
            : "Control unavailable. Refresh and retry.",
        );
      if (path === "input/take") {
        if (!accepting.current) {
          void fetch("/api/input/release", { method: "POST", keepalive: true,
            headers: { "Content-Type": "application/json", "X-RimBot": "1" },
            body: JSON.stringify({ session_id: sessionId, viewer_id: viewer, lease_id: data.lease_id }),
          }).catch(() => {});
          return;
        }
        setLease(data.lease_id);
      }
      if (path === "input/release") setLease("");
      setNotice(
        path.startsWith("input/")
          ? path === "input/take" ? "Player control acquired. Game paused."
            : path === "input/select" ? "Native selection confirmed." : "Player control released."
          : path === "time"
          ? "Game control is now manual. Native state updates below."
          : path === "camera/navigate"
            ? "Camera state read back. Action follow is off."
            : "Camera preference updated.",
      );
    } catch (e) {
      onError(String(e));
    } finally {
      pending.current = false;
      setBusy(false);
    }
  }
  const disabled = !connected || stale || busy;
  return (
    <div className="game-controls">
      <div className="mgr-switch" role="group" aria-label="Player control">
        <button disabled={disabled || !!lease} onClick={() => act("input/take", {})}>
          {lease ? "You have control" : "Take control"}
        </button>
        {lease && <>
          <button disabled={disabled} onClick={() => act("input/release", { resume: false })}>Release control</button>
          <button disabled={disabled} onClick={() => act("input/release", { resume: true })}>Resume automation</button>
        </>}
      </div>
      {lease && <label>Select in game{" "}
        <select aria-label="Select colonist in game" value="" disabled={disabled || headless}
          onChange={e => { if (e.target.value) void act("input/select", { pawn_id: e.target.value }); }}>
          <option value="">Choose a colonist</option>
          {pawns.map(p => <option key={p.thing_id} value={p.thing_id}>{p.name}</option>)}
        </select>
        <button disabled={disabled || headless} onClick={() => act("input/select", { pawn_id: "" })}>Clear selection</button>
      </label>}
      <div className="clock-controls">
        <span className="mgr-eyebrow">GAME TIME</span>
        <div className="mgr-switch" role="group" aria-label="Native game time">
          {[
            ["Paused", "Ⅱ", "Pause game"],
            ["Normal", "▶", "Play game at normal speed"],
            ["Fast", "▶▶", "Play game at fast speed"],
            ["Superfast", "▶▶▶", "Play game at superfast speed"],
          ].map(([value, glyph, label]) => (
            <button
              key={value}
              aria-label={label}
              title={label}
              disabled={disabled}
              aria-pressed={
                value === "Paused"
                  ? paused === true
                  : paused === false && speed === value
              }
              onClick={() => act("time", { speed: value })}
            >
              {glyph}
            </button>
          ))}
        </div>
        <span className="clock-state">
          {stale
            ? "Last known state"
            : paused === undefined
              ? "Waiting for clock"
              : paused
                ? "Paused"
                : speed || "Running"}
        </span>
      </div>
      <button
        className="follow-control"
        disabled={disabled || headless}
        aria-pressed={!!following}
        title="Move the native camera to supported orders. Adds about 1.5 seconds of cinematic lead per supported write. Turn off for maximum throughput and your own framing."
        onClick={() => act("camera/follow", { following: !following })}
      >
        <span aria-hidden="true">◎</span> Follow actions{" "}
        <small>{following ? "ON" : "OFF"}</small>
      </button>
      <div className="mgr-switch" role="group" aria-label="Camera navigation">
        {[["left", "←", "Pan camera left"], ["up", "↑", "Pan camera up"],
          ["down", "↓", "Pan camera down"], ["right", "→", "Pan camera right"],
          ["in", "+", "Zoom camera in"], ["out", "−", "Zoom camera out"]].map(([action, glyph, label]) => (
          <button key={action} aria-label={label} title={label}
            disabled={disabled || headless}
            onClick={() => act("camera/navigate", { action })}>{glyph}</button>
        ))}
      </div>
      <p className="control-help">
        Taking control pauses the game. Leaving this view releases control without resuming automation.{" "}
        Time buttons switch to Manual. Native danger stops still apply.{" "}
        {headless
          ? "Action follow requires a rendered game."
          : "Pan and zoom turn off action follow. Game time and automation stay as set."}
      </p>
      {notice && (
        <span className="sr-only" role="status">
          {notice}
        </span>
      )}
    </div>
  );
}
