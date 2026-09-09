import { useState } from "react";
import type { Committed } from "./CommittedPlan";
import { goalName, priorities, readable, humanize } from "./labels";

export function Muffalo({ large = false }: { large?: boolean }) {
  return (
    <svg
      className={large ? "muffalo large" : "muffalo"}
      viewBox="0 0 160 110"
      fill="none"
      aria-hidden="true"
    >
      <path
        d="M24 65C12 42 36 17 63 23C84 9 120 23 126 47L139 56L135 80L118 82L111 101H99L96 82H55L49 101H36L34 78Z"
        fill="currentColor"
      />
      <path
        d="M115 48C99 42 98 63 107 72M122 43C120 22 142 25 140 13C153 35 137 47 129 48M46 31L39 46L54 40L51 56L69 45L67 60L81 46L89 57L94 42"
        stroke="var(--panel)"
        strokeWidth="5"
        strokeLinejoin="round"
      />
      <path
        d="M124 62H127"
        stroke="var(--panel)"
        strokeWidth="4"
        strokeLinecap="round"
      />
      <path
        d="M25 62L14 73"
        stroke="currentColor"
        strokeWidth="7"
        strokeLinecap="round"
      />
    </svg>
  );
}

export default function FieldGuide({ plan }: { plan?: Committed }) {
  const [showCompleted, setShowCompleted] = useState(false);
  const goals = Object.entries(plan?.colonyGoals || {})
    .filter(([, g]) => !g.cancelled)
    .sort((a, b) => a[1].priority_class - b[1].priority_class);
  return (
    <section className="field-guide mgr-card">
      <p className="mgr-eyebrow">INSIDE THE CONTROLLER</p>
      <h2>Survival comes first.</h2>
      <p>
        Needs are evaluated in priority order. Emergencies can suspend routine
        work. A selected method sends orders; only observed game outcomes
        establish completion.
      </p>
      <div className="priority-ladder">
        {priorities.map((label, i) => (
          <div key={label}>
            <span className="ladder-number">0{i + 1}</span>
            <b>{label}</b>
            <small>
              {
                goals.filter(
                  ([, g]) => g.priority_class === i && g.status !== "complete",
                ).length
              }{" "}
              open
            </small>
          </div>
        ))}
      </div>
      <p className="mgr-muted">
        These are priority classes, not probability scores. Expand a goal to
        inspect its selected method and the evidence behind it.
      </p>
      <label className="completed-toggle">
        <input
          type="checkbox"
          checked={showCompleted}
          onChange={(e) => setShowCompleted(e.target.checked)}
        />{" "}
        Include {goals.filter(([, g]) => g.status === "complete").length}{" "}
        verified goals
      </label>
      {goals
        .filter(([, g]) => showCompleted || g.status !== "complete")
        .map(([id, g]) => (
          <details className="decision-card" key={id}>
            <summary>
              <span className={"decision-dot " + g.status} />
              <b>{goalName(id)}</b>
              <span className="mgr-pill">{humanize(g.status)}</span>
            </summary>
            <div className="decision-body">
              <p>
                {readable(g.reason || "Waiting for observed progress.", plan)}
              </p>
              <dl>
                <dt>Priority</dt>
                <dd>{priorities[g.priority_class]}</dd>
                <dt>Selected method</dt>
                <dd>
                  {g.method
                    ? humanize(g.method.replace(/-[a-f0-9]{8,}$/i, ""))
                    : "No method selected"}
                </dd>
                <dt>Requested by</dt>
                <dd>
                  {g.source === "PLAYER"
                    ? "You"
                    : g.source === "LLM_ADVISOR"
                      ? "Adviser"
                      : "Deterministic controller"}
                </dd>
              </dl>
              {plan?.steps
                .filter((s) => s.goal_id === id)
                .map((s) => (
                  <p key={s.id}>
                    <b>{readable(s.title, plan)}</b> · {humanize(s.state)}
                    <br />
                    {readable(s.failure?.detail || s.completion, plan)}
                  </p>
                ))}
              <details className="technical">
                <summary>Diagnostic evidence</summary>
                <pre>{JSON.stringify(g, null, 2)}</pre>
              </details>
            </div>
          </details>
        ))}
      {!goals.length && (
        <p className="empty-state">
          The first colony review will populate this field guide.
        </p>
      )}
    </section>
  );
}
