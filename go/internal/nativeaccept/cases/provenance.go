package cases

import (
	"fmt"
	"strings"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// ProvenanceKey is the report block Provenance fills.
const ProvenanceKey = "provenance"

// Execution is how much of a case a result covers (#617). A full run drove
// the case from its declared precondition; the other three opened on
// something an earlier run left behind and therefore prove less.
type Execution string

const (
	// ExecutionFull ran the declared Start and every stage itself.
	ExecutionFull Execution = "full"
	// ExecutionCachedPrecondition opened on a stage bundle: the staging
	// block's own behaviour was not exercised, only the behaviour after it.
	ExecutionCachedPrecondition Execution = "cached-precondition"
	// ExecutionResumedSuffix opened on a checkpoint from a failed run: only
	// behaviour past the checkpoint's offset was exercised.
	ExecutionResumedSuffix Execution = "resumed-suffix"
	// ExecutionDev is an `acceptance dev` iteration over a pinned bundle:
	// an edit loop, never evidence.
	ExecutionDev Execution = "dev-iteration"
	// ExecutionPostmortemOnly reloaded a failed bundle and ran only the
	// case's Postmortem reads: it proves the assertion, not the scenario.
	ExecutionPostmortemOnly Execution = "postmortem-only"
)

// Provenance stamps on report the derived answer to "what does this result
// prove?": which segment of the case ran, what the installed build had to
// provide for it to start, and whether the case got its own process. Every
// field is derived from c and from what the run already recorded, so the
// block adds no second catalog to maintain beside the registry.
//
// It deliberately does not restate the registry: the claim is the case's
// Scope (already report["scope"]), the declared precondition is
// report["start"], and the bundle a reused run opened on is
// report["resumed_from"]/["staged_from"]. What is derived here is the
// judgement those fields support.
func Provenance(c Case, report na.Report) map[string]any {
	execution, proves := classify(c, report)
	block := map[string]any{
		"execution": string(execution),
		"proves":    proves,
		"isolation": string(isolationOf(c)),
	}
	if reason := isolationReason(c); reason != "" {
		block["isolation_reason"] = reason
	}
	if ops := c.FixtureOps(); len(ops) > 0 {
		// The native dependency: without these ops registered by the
		// installed package the case cannot reach its precondition at all.
		block["native_ops"] = ops
	}
	if c.Serve != nil && c.Serve.Families != nil {
		// The routine families the controller was restricted to: everything
		// outside this list was disabled for the run, so the result says
		// nothing about it.
		block["routine_families"] = c.Serve.Families
	}
	if _, restored := report["resumed_from"]; restored {
		block["world"] = "restored-save"
	} else if _, restored := report["staged_from"]; restored {
		block["world"] = "restored-save"
	} else {
		block["world"] = "fresh"
	}
	// A fresh world is not a fresh process: the runner leaves the game at
	// the main menu for the next case unless the case opted out.
	kept, _ := report["keep"].(bool)
	block["process"] = "reused-after-run"
	if !kept {
		block["process"] = "not-kept-after-run"
	}
	report[ProvenanceKey] = block
	return block
}

// classify decides the run's Execution and the one-line statement of what
// segment it covers. The order matters: a postmortem-only run over a
// resumed bundle is a postmortem-only run.
func classify(c Case, report na.Report) (Execution, string) {
	if only, _ := report["postmortem_only"].(bool); only {
		return ExecutionPostmortemOnly, "the case's Postmortem reads over a bundle an earlier run left; the scenario that produced it was not rerun"
	}
	if dev, _ := report["dev"].(bool); dev {
		return ExecutionDev, "an edit iteration over a pinned bundle; not evidence for any segment"
	}
	if from, ok := na.AsMap(report["resumed_from"]); ok {
		return ExecutionResumedSuffix, fmt.Sprintf("behaviour past %s only; everything before it came from the checkpoint", offsetOf(from))
	}
	if from, ok := na.AsMap(report["staged_from"]); ok {
		stage := na.AsString(from["stage"])
		if stage == "" {
			stage = "the cached stage"
		}
		return ExecutionCachedPrecondition, fmt.Sprintf("behaviour after stage %q only; the staging blocks that produced the precondition were not rerun (%s)", stage, strings.Join(stagesUpTo(c, stage), " -> "))
	}
	return ExecutionFull, "the whole case from its declared precondition"
}

// offsetOf describes where a resumed bundle picked up, by game tick when
// the checkpoint recorded one and by run offset otherwise.
func offsetOf(from map[string]any) string {
	if tick, ok := from["tick"].(float64); ok && tick > 0 {
		return fmt.Sprintf("tick %d", int64(tick))
	}
	if label := na.AsString(from["label"]); label != "" {
		return label
	}
	if ms, ok := from["offset_ms"].(float64); ok {
		return fmt.Sprintf("t+%.0fs", ms/1000)
	}
	return "the checkpoint"
}

// stagesUpTo names the case's stages the bundle stands in for, in declared
// order through stage.
func stagesUpTo(c Case, stage string) []string {
	var skipped []string
	for _, name := range c.Stages {
		skipped = append(skipped, name)
		if name == stage {
			break
		}
	}
	if len(skipped) == 0 {
		return []string{"no stage of that name is declared"}
	}
	return skipped
}

// Isolation is the process the case got.
type Isolation string

const (
	// IsolationFresh means the process ends with the case: nothing after it
	// inherits native static state it touched.
	IsolationFresh Isolation = "own-process"
	// IsolationShared means the case ran on whatever process the previous
	// case kept, and leaves it for the next. A map reload is not a process
	// reset, so an assertion about initialization needs IsolationFresh.
	IsolationShared Isolation = "shared-process"
)

func isolationOf(c Case) Isolation {
	if c.NoKeep || len(c.Expansions) > 0 {
		return IsolationFresh
	}
	if _, owned := c.Start.(Owned); owned {
		return IsolationFresh
	}
	return IsolationShared
}

// isolationReason says why the case needs its own process, for the cases
// that declared it.
func isolationReason(c Case) string {
	if _, owned := c.Start.(Owned); owned {
		return "the case drives the process lifecycle itself (Owned)"
	}
	switch {
	case len(c.Expansions) > 0:
		return "the case pins a DLC profile, which a kept process cannot change"
	case c.NoKeep:
		return "the case declares NoKeep: it restarts, faults or retires the game, or touches process-scoped static state"
	}
	return ""
}
