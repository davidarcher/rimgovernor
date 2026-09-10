import { useRef, useState } from "react";

export default function GameControls({
  sessionId,
  connected,
  stale,
  paused,
  headless,
  following,
  speed,
  onError,
}: {
  sessionId: string;
  connected: boolean;
  stale: boolean;
  paused?: boolean;
  headless?: boolean;
  following?: boolean;
  speed?: string;
  onError: (error: string) => void;
}) {
  const [busy, setBusy] = useState(false),
    [notice, setNotice] = useState("");
  const pending = useRef(false);
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
        body: JSON.stringify({ session_id: sessionId, ...body }),
      });
      const data = await r.json();
      if (!r.ok)
        throw Error(
          typeof data.detail === "string"
            ? data.detail
            : "Control unavailable. Refresh and retry.",
        );
      setNotice(
        path === "time"
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
