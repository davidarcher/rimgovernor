import { useState } from "react";
import { readable, humanize } from "./labels";
export type Committed = {
  revision: number;
  rationale: string;
  goals: string[];
  constraints: string[];
  risks: string[];
  controller?: {
    status?: string;
    execution_hold?: string;
    facts?: {
      foodRunwayDays?: number;
      resources?: Record<string, number>;
      indoorSleepingCapacity?: number;
      colonists?: number;
      sleepingTemperatureMin?: number;
    };
    criteria?: Record<string, boolean>;
    resource_policy?: Record<string, { spending: string; reserve: number }>;
  };
  colonyGoals?: Record<
    string,
    {
      status: string;
      priority_class: number;
      source: string;
      method: string;
      reason: string;
      cancelled: boolean;
    }
  >;
  steps: {
    id: string;
    title: string;
    priority: number;
    action: string;
    completion: string;
    state: string;
    issued: number;
    source?: string;
    goal_id?: string;
    failure?: { detail: string } | null;
  }[];
};

const sourceName = (source?: string) =>
  source === "PLAYER"
    ? "Your request"
    : source === "LLM_ADVISOR"
      ? "Adviser suggestion"
      : "Colony controller";
const finished = (state: string) => ["complete", "cancelled"].includes(state);
export default function CommittedPlan({
  plan,
  onCancel,
}: {
  plan?: Committed;
  onCancel: (id: string) => void;
}) {
  const [history, setHistory] = useState(false);
  if (!plan)
    return (
      <p className="empty-state">
        The work ledger will appear after the first colony review. Give a
        direction in chat to create an explicit request.
      </p>
    );
  const steps = plan.steps.filter((s) => history || !finished(s.state));
  return (
    <section className="work-ledger">
      <div className="section-heading">
        <h2>Colony commitments</h2>
        <label className="completed-toggle">
          <input
            type="checkbox"
            checked={history}
            onChange={(e) => setHistory(e.target.checked)}
          />{" "}
          Include finished work
        </label>
      </div>
      <p className="mgr-muted">
        Orders and observed outcomes share one ledger. Completed orders do not
        by themselves establish a safe, functioning colony.
      </p>
      {plan.controller?.execution_hold && (
        <p className="autopilot-notice" role="status">
          {readable(plan.controller.execution_hold, plan)}
        </p>
      )}
      <div className="work-stats">
        <span>
          <b>
            {
              plan.steps.filter(
                (s) => !finished(s.state) && s.state !== "blocked",
              ).length
            }
          </b>{" "}
          in progress
        </span>
        <span>
          <b>{plan.steps.filter((s) => s.state === "blocked").length}</b>{" "}
          blocked
        </span>
        <span>
          <b>{plan.steps.filter((s) => s.state === "complete").length}</b>{" "}
          verified orders
        </span>
        <a href="#autopilot">Inspect priorities ↗</a>
      </div>
      {!steps.length && (
        <p className="empty-state">
          No unfinished orders. The controller may be monitoring needs or
          waiting for your direction.
        </p>
      )}
      {steps.map((s) => (
        <article key={s.id} className="work-order">
          <header>
            <div>
              <span className="mgr-eyebrow">{sourceName(s.source)}</span>
              <h3>{readable(s.title, plan)}</h3>
            </div>
            <span className="mgr-pill">{humanize(s.state)}</span>
            {!finished(s.state) && (
              <button onClick={() => onCancel(s.id)}>Cancel</button>
            )}
          </header>
          <p>{readable(s.failure?.detail || s.completion, plan)}</p>
          <details className="technical">
            <summary>Order diagnostics</summary>
            <pre>{JSON.stringify(s, null, 2)}</pre>
          </details>
        </article>
      ))}
      {(plan.constraints.length > 0 || plan.risks.length > 0) && (
        <details className="technical">
          <summary>Constraints and risks</summary>
          {[...plan.constraints, ...plan.risks].map((r, i) => (
            <p key={i}>{readable(r, plan)}</p>
          ))}
        </details>
      )}
    </section>
  );
}
