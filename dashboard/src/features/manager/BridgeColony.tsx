import { useEffect, useRef, useState } from "react";
import People, { type PeopleObservation } from "./People";
import Notebook, { type Memory } from "./Notebook";
import ProjectList, { type Project } from "./ProjectList";
import CommittedPlan, { type Committed } from "./CommittedPlan";
import Autopilot, {
  ColonyReadings,
  nextWork,
  type AutopilotSettings,
} from "./Autopilot";
import "./Manager.css";
import "./BridgeColony.css";
import "./Outpost.css";
import FieldGuide, { Muffalo } from "./FieldGuide";
import { readable, humanize } from "./labels";
import GameControls from "./GameControls";
import Throughput from "./Throughput";
import GameVideo from "./GameVideo";
import VisualReviews, { type VisualReview } from "./VisualReviews";
type Message = { id: number; kind: string; text: string; at: number };
type State = {
  visualReviews?: VisualReview[];
  cinematic?: boolean;
  clockSupervisor?: { requestedSpeed?: string; stopReason?: string };
  autopilotSettings?: AutopilotSettings;
  chatModel?: string;
  cameraError?: string;
  cameraCapturedAt?: number;
  memories?: Memory[];
  headless?: boolean;
  currentPlan?: Committed;
  modelRoles?: Record<
    string,
    {
      calls: number;
      elapsed_seconds: number;
      input_tokens: number;
      output_tokens: number;
      consultations_used: number;
    }
  >;
  projects?: Project[];
  sessionId: string;
  connected: boolean;
  mode: string;
  mood: string;
  cameraVersion: number;
  goals: { long: string; short: string };
  feed: Message[];
  status: { label: string };
  game: { tick?: number; wallTs?: number; paused: boolean; stale: boolean };
  counters: {
    planner_tools?: number;
    tools: number;
    actions: number;
    model_calls: number;
  };
  observation?: PeopleObservation | null;
};
export default function BridgeColony() {
  const [s, setState] = useState<State | null>(null),
    [error, setError] = useState(""),
    [text, setText] = useState(""),
    [sending, setSending] = useState(false),
    [view, setView] = useState(
      [
        "colony",
        "autopilot",
        "projects",
        "people",
        "notebook",
        "activity",
      ].includes(location.hash.slice(1))
        ? location.hash.slice(1)
        : "colony",
    ),
    [events, setEvents] = useState<Message[]>([]),
    [camera, setCamera] = useState("");
  const [videoPlaying, setVideoPlaying] = useState(true);
  const [expanded, setExpanded] = useState(false);
  useEffect(() => {
    const changed = () => setExpanded(!!document.fullscreenElement);
    document.addEventListener("fullscreenchange", changed);
    return () => document.removeEventListener("fullscreenchange", changed);
  }, []);
  const [gameUpdates, setGameUpdates] = useState(false);
  const [refreshError, setRefreshError] = useState(""),
    [imageError, setImageError] = useState(false);
  const viewer = useRef(crypto.randomUUID());
  const videoRevision = useRef(0);
  useEffect(() => {
    const heartbeat = () =>
      fetch("/api/video", {
        method: "POST",
        headers: { "X-RimBot": "1", "Content-Type": "application/json" },
        body: JSON.stringify({
          viewer: viewer.current,
          revision: ++videoRevision.current,
          playing: videoPlaying && view === "colony" && !document.hidden,
        }),
        keepalive: true,
      }).catch(() => {});
    heartbeat();
    const timer = setInterval(heartbeat, 3000);
    document.addEventListener("visibilitychange", heartbeat);
    return () => {
      clearInterval(timer);
      document.removeEventListener("visibilitychange", heartbeat);
      fetch("/api/video", {
        method: "POST",
        headers: { "X-RimBot": "1", "Content-Type": "application/json" },
        body: JSON.stringify({ viewer: viewer.current, playing: false, revision: ++videoRevision.current }),
        keepalive: true,
      }).catch(() => {});
    };
  }, [videoPlaying, view]);
  const [modeBusy, setModeBusy] = useState(false);
  const followChat = useRef(true);
  const input = useRef<HTMLTextAreaElement>(null),
    chat = useRef<HTMLDivElement>(null),
    surface = useRef<HTMLElement>(null);
  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    const poll = async () => {
      try {
        const r = await fetch("/api/state");
        if (!r.ok) throw Error(`Connection failed (${r.status})`);
        const data = await r.json();
        if (!stopped) {
          setState(data);
          setRefreshError("");
        }
      } catch (e) {
        if (!stopped) setRefreshError(String(e));
      }
      if (!stopped) timer = setTimeout(poll, 1500);
    };
    poll();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, []);
  useEffect(() => {
    const change = () =>
      setView(
        [
          "colony",
          "autopilot",
          "projects",
          "people",
          "notebook",
          "activity",
        ].includes(location.hash.slice(1))
          ? location.hash.slice(1)
          : "colony",
      );
    window.addEventListener("hashchange", change);
    return () => window.removeEventListener("hashchange", change);
  }, []);
  useEffect(() => {
    setCamera("");
    setEvents([]);
  }, [s?.sessionId]);
  useEffect(() => {
    if (
      !videoPlaying ||
      view !== "colony" ||
      document.hidden ||
      !s?.cameraVersion
    )
      return;
    let active = true;
    const url = `/api/camera?v=${s.cameraVersion}`;
    const image = new Image();
    image.onload = () => {
      if (active) {
        setCamera(url);
        setImageError(false);
      }
    };
    image.onerror = () => {
      if (active) setImageError(true);
    };
    image.src = url;
    return () => {
      active = false;
    };
  }, [s?.cameraVersion, videoPlaying, view]);
  useEffect(() => {
    if (followChat.current)
      chat.current?.scrollTo?.(0, chat.current.scrollHeight);
  }, [s?.feed.at(-1)?.id, view]);
  useEffect(() => {
    if (!["projects", "activity"].includes(view)) return;
    let active = true;
    let timer: ReturnType<typeof setTimeout>;
    const poll = async () => {
      try {
        const r = await fetch("/api/diagnostics");
        if (!r.ok) throw Error("Activity unavailable");
        const data = await r.json();
        if (active) setEvents(data.events);
      } catch (e) {
        if (active) setError(String(e));
      }
      if (active) timer = setTimeout(poll, 3000);
    };
    poll();
    return () => {
      active = false;
      clearTimeout(timer);
    };
  }, [view, s?.sessionId]);
  async function post(path: string, body: unknown) {
    setError("");
    const r = await fetch("/api/" + path, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-RimBot": "1" },
      body: JSON.stringify(body),
    });
    if (!r.ok) {
      const data = await r.json();
      throw Error(data.detail || `Request failed (${r.status})`);
    }
  }
  async function changeMode(mode: string) {
    if (modeBusy) return;
    setModeBusy(true);
    try {
      await post("control", { mode });
    } catch (e) {
      setError(String(e));
    } finally {
      setModeBusy(false);
    }
  }
  async function send() {
    if (!text.trim() || sending) return;
    const submitted = text;
    setSending(true);
    try {
      await post("chat", { text: submitted });
      setText((draft) => (draft === submitted ? "" : draft));
      input.current?.focus();
    } catch (e) {
      setError(String(e));
    } finally {
      setSending(false);
    }
  }
  const conversation =
    s?.feed.filter(
      (m) =>
        gameUpdates ||
        m.kind === "human" ||
        (m.kind === "summary" &&
          !Object.keys(s.currentPlan?.colonyGoals || {}).some((id) =>
            m.text.startsWith(id + ":"),
          )),
    ) || [];
  const journal = Array.from(
    new Map([...(s?.feed || []), ...events].map((e) => [e.id, e])).values(),
  )
    .filter((e) =>
      [
        "summary",
        "blocker",
        "clock_event",
        "player_clock",
        "control",
        "project",
      ].includes(e.kind),
    )
    .sort((a, b) => b.id - a.id)
    .slice(0, 30);
  return (
    <div className="mgr-shell bridge-shell">
      <header className="mgr-top">
        <a href="#" className="mgr-brand">
          <Muffalo />
          <div>
            OUTPOST<small>A RIMWORLD FIELD STATION</small>
          </div>
        </a>
        <div className="mgr-inline station-status">
          <span className="mgr-connection">
            {s?.connected ? "Colony connected" : "Connecting…"}
          </span>
          <div className="mgr-switch">
            {["manual", "automate"].map((mode) => (
              <button
                key={mode}
                disabled={!s?.connected || !!refreshError || modeBusy}
                aria-pressed={s?.mode === mode}
                onClick={() => changeMode(mode)}
              >
                {mode === "manual" ? "Manual" : "Automate"}
              </button>
            ))}
          </div>
        </div>
      </header>
      <nav className="colony-nav" aria-label="Colony navigation">
        {[
          ["colony", "Watch"],
          ["autopilot", "Priorities"],
          ["projects", "Work"],
          ["people", "Colony"],
        ].map(([id, label]) => (
          <a
            key={id}
            href={id === "colony" ? "#" : "#" + id}
            aria-current={
              (view === "activity"
                ? "projects"
                : view === "notebook"
                  ? "people"
                  : view) === id
                ? "page"
                : undefined
            }
          >
            <small>
              0{["colony", "autopilot", "projects", "people"].indexOf(id) + 1}
            </small>
            {label}
          </a>
        ))}
        <span className="nav-caption">
          LOCAL INTELLIGENCE · NATIVE SIMULATION
        </span>
      </nav>
      {(error || refreshError) && (
        <div role="alert" className="mgr-refresh-error">
          {error || refreshError}
          {refreshError && " · Showing the last good colony state."}
        </div>
      )}
      <main className="bridge-main">
        <div className="watch-layout" hidden={view !== "colony"}>
          <section className="bridge-stage mgr-card" ref={surface}>
            <div className="mgr-card-title">
              <div>
                <span className="mgr-eyebrow">FIELD CAMERA</span>
                <h2>Your colony, unfolding.</h2>
              </div>
              <div className="mgr-inline">
                <span>
                  {s?.game.stale || refreshError
                    ? "Connection interrupted"
                    : s?.game.paused
                      ? "Paused"
                      : "Playing"}
                </span>
                <button onClick={() => setVideoPlaying((v) => !v)}>
                  {videoPlaying ? "Pause video" : "Play video"}
                </button>
                <button
                  onClick={() =>
                    (document.fullscreenElement
                      ? document.exitFullscreen()
                      : surface.current?.requestFullscreen())
                      ?.catch((e) => setError(String(e)))
                  }
                >
                  {expanded ? "Exit fullscreen" : "Expand"}
                </button>
              </div>
            </div>
            <div className="bridge-image">
              <GameVideo session={s?.sessionId} viewer={viewer.current}
                enabled={videoPlaying && view === "colony" && !!s?.connected && !s?.headless}
                snapshot={camera}>
              {!camera && (
                <div className="camera-empty">
                  <Muffalo large />
                  <span className="mgr-eyebrow">NO SIGNAL / FIELD CAMERA</span>
                  <h3>The frontier is coming into view.</h3>
                  <p>
                    {s?.headless
                      ? "Headless test · rendering is disabled"
                      : "Waiting for the first game snapshot…"}
                  </p>
                </div>
              )}
              </GameVideo>
            </div>
            <GameControls
              key={s?.sessionId}
              sessionId={s?.sessionId || ""}
              connected={!!s?.connected}
              stale={!!refreshError || !!s?.game.stale}
              paused={s?.game.paused}
              headless={s?.headless}
              following={s?.cinematic}
              speed={s?.clockSupervisor?.requestedSpeed}
              pawns={s?.observation?.pawns}
              onError={setError}
            />
            <div className="bridge-summary">
              <Throughput
                sessionId={s?.sessionId || ""}
                tick={s?.game.tick}
                at={s?.game.wallTs}
                stale={!!refreshError || !s?.connected}
              />
              <span className="mgr-eyebrow">ON THE HORIZON</span>
              <p>
                {readable(
                  s?.goals.short || nextWork(s?.currentPlan, s?.mode),
                  s?.currentPlan,
                )}
              </p>
              <small className="mgr-muted">
                {s?.headless
                  ? "Native state and controls remain available. Restart without -Headless to watch the colony."
                  : "Game snapshots refresh every few seconds."}
              </small>
              {(imageError || s?.cameraError) && (
                <p role="status">
                  Live view delayed. Retrying while keeping the last frame.
                </p>
              )}
              <ColonyReadings plan={s?.currentPlan} />
              <a href="#autopilot">Why this comes next ↗</a>
            </div>
          </section>
          <section className="mgr-card mgr-conversation bridge-chat">
            <div className="mgr-card-title">
              <div>
                <span className="mgr-eyebrow">OPEN CHANNEL</span>
                <h2>Colony dispatch</h2>
              </div>
              <span className="mgr-pill">
                {s?.mood === "thinking" ? "Working" : "Standing by"}
              </span>
            </div>
            <div
              className={
                "mgr-thinking " + (s?.mood === "thinking" ? "busy" : "")
              }
              role="status"
            >
              <i />
              <div>
                <b>{s?.status.label || "Connecting"}</b>
              </div>
            </div>
            <p className="bridge-chat-model">
              Local chat{s?.chatModel ? ` · ${s.chatModel}` : ""} · Autopilot
              operates without model calls.
            </p>
            <label className="chat-update-toggle">
              <input
                type="checkbox"
                checked={gameUpdates}
                onChange={(e) => setGameUpdates(e.target.checked)}
              />{" "}
              Include game and controller updates
            </label>
            <div
              className="mgr-chat"
              ref={chat}
              onScroll={(e) => {
                const el = e.currentTarget;
                followChat.current =
                  el.scrollHeight - el.scrollTop - el.clientHeight < 60;
              }}
            >
              {s && conversation.length ? (
                conversation.map((m) => (
                  <div
                    className={
                      "mgr-message " +
                      (m.kind === "human" ? "player" : "assistant")
                    }
                    key={m.id}
                  >
                    <div>
                      {m.kind === "human"
                        ? "You"
                        : m.kind === "clock_event"
                          ? "Game"
                          : "Colony manager"}
                    </div>
                    <p>
                      {m.kind === "human"
                        ? m.text
                        : readable(m.text, s.currentPlan, s.observation?.pawns)}
                    </p>
                    {m.kind !== "human" &&
                      readable(m.text, s.currentPlan, s.observation?.pawns) !==
                        m.text && (
                        <details className="technical">
                          <summary>Diagnostic detail</summary>
                          <pre>{m.text}</pre>
                        </details>
                      )}
                  </div>
                ))
              ) : (
                <div className="mgr-welcome">
                  <span className="dispatch-mark">✳</span>
                  <h3>
                    A little direction.
                    <br />A better chance.
                  </h3>
                  <p>
                    Ask about the colony, set a target, or tell your people what
                    matters next.
                  </p>
                  <div className="chat-suggestions">
                    {[
                      "What needs our attention?",
                      "Explain the current priorities",
                      "What is blocking construction?",
                    ].map((q) => (
                      <button
                        key={q}
                        onClick={() => {
                          setText(q);
                          input.current?.focus();
                        }}
                      >
                        {q} ↗
                      </button>
                    ))}
                  </div>
                </div>
              )}
            </div>
            <form
              className="mgr-composer"
              onSubmit={(e) => {
                e.preventDefault();
                send();
              }}
            >
              <textarea
                ref={input}
                aria-label="Message the colony manager"
                value={text}
                onChange={(e) => setText(e.target.value)}
                rows={3}
                maxLength={4000}
                placeholder="Ask, plan, or change direction…"
                onKeyDown={(e) => {
                  if (
                    e.key === "Enter" &&
                    !e.shiftKey &&
                    !e.nativeEvent.isComposing
                  ) {
                    e.preventDefault();
                    send();
                  }
                }}
              />
              <div>
                <span>Enter to send · Shift + Enter for a new line</span>
                <button
                  disabled={!text.trim() || sending || !s?.connected}
                  className="mgr-primary"
                >
                  Send ↗
                </button>
              </div>
            </form>
          </section>
        </div>
        <div className="page-stack" hidden={view !== "autopilot"}>
          <FieldGuide plan={s?.currentPlan} />
          <Autopilot
            key={s?.sessionId}
            settings={s?.autopilotSettings}
            plan={s?.currentPlan}
            sessionId={s?.sessionId || ""}
            connected={!!s?.connected && !refreshError}
            mode={s?.mode || "manual"}
            onSaved={(settings) =>
              setState((current) =>
                current ? { ...current, autopilotSettings: settings } : current,
              )
            }
          />
        </div>
        <div
          className="page-stack"
          hidden={!["people", "notebook"].includes(view)}
        >
          <div className="section-heading">
            <div>
              <p className="mgr-eyebrow">THE PEOPLE WHO MAKE IT POSSIBLE</p>
              <h1>Life at the outpost.</h1>
            </div>
            <div className="mgr-inline">
              <a href="#people">Colonists</a>
              <a href="#notebook">Field notes ↗</a>
            </div>
          </div>
          <div hidden={view !== "people"}>
            <People
              key={s?.sessionId}
              observation={s?.observation}
              stale={!s?.connected || !!s?.game.stale || !!refreshError}
            />
          </div>
          <div hidden={view !== "notebook"}>
            <Notebook
              key={s?.sessionId}
              notes={s?.memories || []}
              sessionId={s?.sessionId || ""}
              onChange={() =>
                fetch("/api/state")
                  .then((r) => r.json())
                  .then(setState)
                  .catch((e) => setError(String(e)))
              }
            />
          </div>
        </div>
        <div
          className="page-stack"
          hidden={!["projects", "activity"].includes(view)}
        >
          <section className="mgr-card bridge-detail">
            <p className="mgr-eyebrow">ORDERS, LABOR & OBSERVED OUTCOMES</p>
            <h1>Work in motion.</h1>
            <p>
              {readable(
                s?.goals.long ||
                  "Follow the intent, the work, and what the game has verified.",
                s?.currentPlan,
              )}
            </p>
            <CommittedPlan
              plan={s?.currentPlan}
              onCancel={(id) => {
                fetch(`/api/plan/steps/${encodeURIComponent(id)}`, {
                  method: "DELETE",
                  headers: { "X-RimBot": "1" },
                })
                  .then(async (r) => {
                    if (!r.ok) throw Error((await r.json()).detail);
                  })
                  .catch((e) => setError(String(e)));
              }}
            />
            <ProjectList
              plan={s?.currentPlan}
              projects={s?.projects || []}
              onChange={() => {
                fetch("/api/state")
                  .then((r) => r.json())
                  .then(setState)
                  .catch((e) => setError(String(e)));
              }}
            />
          </section>
          <section className="mgr-card bridge-detail">
            <p className="mgr-eyebrow">FIELD JOURNAL</p>
            <h2>Recent activity</h2>
            <VisualReviews reviews={s?.visualReviews || []} />
            {journal.length === 0 && (
              <p className="empty-state">
                Updates will appear here as the colony operates.
              </p>
            )}
            {journal.map((e) => (
              <article className="journal-entry" key={e.id}>
                <span className="mgr-eyebrow">
                  {humanize(e.kind)} ·{" "}
                  {new Date(e.at * 1000).toLocaleTimeString([], {
                    hour: "2-digit",
                    minute: "2-digit",
                  })}
                </span>
                <p>{readable(e.text, s?.currentPlan, s?.observation?.pawns)}</p>
              </article>
            ))}
            <details className="technical">
              <summary>
                Diagnostics · IDs, native receipts & model usage
              </summary>
              <p>
                {s?.counters.tools || 0} native calls ·{" "}
                {s?.counters.actions || 0} orders ·{" "}
                {s?.counters.model_calls || 0} model calls
              </p>
              <details>
                <summary>Model usage</summary>
                <pre>{JSON.stringify(s?.modelRoles, null, 2)}</pre>
              </details>
              <details>
                <summary>Complete controller state</summary>
                <pre>{JSON.stringify(s?.currentPlan, null, 2)}</pre>
              </details>
              <details>
                <summary>Raw event ledger</summary>
                <pre>{JSON.stringify(events, null, 2)}</pre>
              </details>
            </details>
          </section>
        </div>
      </main>
      <footer className="outpost-footer">
        <span>OUTPOST / SURVIVAL IS A SHARED PROJECT</span>
        <span>RimWorld owns the simulation. You set the direction.</span>
      </footer>
    </div>
  );
}
