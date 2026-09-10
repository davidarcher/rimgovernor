import { readable } from "./labels";
import type { Committed } from "./CommittedPlan";
import { useState } from "react";
export type Project = {
  id: string;
  title: string;
  detail: string;
  state: string;
  evidence: string;
  matched_ids: string[];
};
export default function ProjectList({
  projects,
  onChange,
  plan,
}: {
  projects: Project[];
  onChange: () => void;
  plan?: Committed;
}) {
  const [title, setTitle] = useState(""),
    [error, setError] = useState(""),
    [busy, setBusy] = useState(""),
    [history, setHistory] = useState(false);
  async function request(path: string, method: string, body?: unknown) {
    setBusy(method + path);
    try {
      const r = await fetch("/api/projects" + path, {
        method,
        headers: { "X-RimGovernor": "1", "Content-Type": "application/json" },
        body: body ? JSON.stringify(body) : undefined,
      });
      if (!r.ok) {
        const data = await r.json();
        throw Error(data.detail || "Project update failed");
      }
      setError("");
      if (method === "POST") setTitle("");
      onChange();
    } catch (e) {
      setError(String(e));
    } finally {
      setBusy("");
    }
  }
  return (
    <section>
      <h2>Projects</h2>
      <p className="mgr-muted">
        Completion comes from game observations. Cancelling tracking leaves
        existing game orders in place.
      </p>
      <form
        className="bridge-project-form"
        onSubmit={(e) => {
          e.preventDefault();
          request("", "POST", {
            title: title.trim(),
            detail: "Player objective",
            targets: [],
          });
        }}
      >
        <input
          aria-label="New project"
          value={title}
          maxLength={160}
          onChange={(e) => setTitle(e.target.value)}
          placeholder="What should the colony work toward?"
        />
        <button disabled={!title.trim() || !!busy}>Add objective</button>
      </form>
      {error && <p role="alert">{error}</p>}
      <label>
        <input
          type="checkbox"
          checked={history}
          onChange={(e) => setHistory(e.target.checked)}
        />{" "}
        Include finished projects
      </label>
      {projects
        .filter((p) => history || !["cancelled", "complete"].includes(p.state))
        .map((p) => (
          <article className="bridge-project" key={p.id}>
            <header>
              <h3>{readable(p.title, plan)}</h3>
              <span className="mgr-pill">{p.state}</span>
              {!["cancelled", "complete"].includes(p.state) && (
                <button
                  disabled={!!busy}
                  onClick={() =>
                    request("/" + encodeURIComponent(p.id), "DELETE")
                  }
                  aria-label={"Cancel " + p.title}
                >
                  Cancel
                </button>
              )}
            </header>
            <p>{readable(p.detail, plan)}</p>
            <small>
              {readable(p.evidence, plan) || "Awaiting a concrete plan"}
            </small>
          </article>
        ))}
    </section>
  );
}
