package explain

import (
	"errors"
	"fmt"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const plannerKind = "planner_step"

// causeOutcomes are the verdict words a transition row with a cause carries;
// clearOutcomes the two a row with no cause (the concern cleared) carries.
var (
	causeOutcomes = []string{"refused", "waiting", "disabled", "combat_orders", "hold_fallback"}
	clearOutcomes = []string{"admitted", "nothing_to_do"}
)

// Result is what Audit found, kept in the case report.
type Result struct {
	Active      int      `json:"active_concerns"`
	Transitions int      `json:"transition_rows"`
	NonClear    int      `json:"non_clear_transition_rows"`
	Planner     int      `json:"non_clear_planner_steps"`
	Explained   []string `json:"explained"`
	// Silent are active concerns with no row and no refusal or wait anywhere:
	// a concern first seen clear writes no row by design.
	Silent []string `json:"silent"`
}

// Audit holds a colony's rows to the explainability claim. rows is the flight
// reading and transitions the session store's concern_transitions; active maps
// every concern that had a progress record during the window to the planner
// cause filed on it at its last non-clear poll ("" when never filed).
//
// A key is a concern id or, for a planner serving no single concern, the
// planner's own name (the target its transition rows use). A key needs an
// explanation when either independent trail shows a non-clear state for it:
// a planner_step row in the OTLP log ring with verdict refused or waiting, a
// closed cause, that was neither late nor an immediate review (the planner
// ran and its verdict was filed); or, for an active concern, a planner cause
// on its progress record. It is explained when concern_transitions holds at
// least one row for the key with a cause: the first row of a key is always
// non-clear, since a key first seen clear writes none. A key with neither trail
// is silent and passes.
//
// Every transition row must also be closed typed data: a known verdict, a
// cause that policy.Wording names (not its unnamed-reason fallback), a bounded
// printable subject and no free text.
// The run fails when no active concern, no planner refusal or wait, or no
// transition row was seen at all, since nothing would have been checked.
func Audit(rows []bridge.TimelineRecord, transitions []store.ConcernTransition, active map[string]policy.Cause) (Result, error) {
	var problems []error
	res := Result{Active: len(active)}

	need := map[string]string{} // key -> where the non-clear state showed
	for _, r := range rows {
		if r.Kind != plannerKind {
			continue
		}
		verdict, _ := r.Payload["verdict"].(string)
		reason, _ := r.Payload["reason"].(string)
		attrs, _ := r.Payload["attrs"].(map[string]any)
		if verdict != "refused" && verdict != "waiting" || policy.Cause(reason).Validate() != nil {
			continue
		}
		if late, _ := attrs["late"].(bool); late {
			continue
		}
		if scope, _ := attrs["scope"].(string); scope == "immediate" {
			continue
		}
		key, _ := attrs["concern"].(string)
		if key == "" {
			key, _ = r.Payload["target"].(string)
		}
		if key == "" {
			continue
		}
		res.Planner++
		if _, ok := need[key]; !ok {
			need[key] = fmt.Sprintf("planner_step %s %s", verdict, reason)
		}
	}
	for concern, cause := range active {
		if cause == "" {
			continue
		}
		if _, ok := need[concern]; !ok {
			need[concern] = "progress record planner cause " + string(cause)
		}
	}

	explained := map[string]bool{}
	for _, r := range transitions {
		res.Transitions++
		target, cause, err := checkRow(r)
		if err != nil {
			problems = append(problems, fmt.Errorf("transition row seq %d: %w", r.Sequence, err))
			continue
		}
		if cause != "" {
			res.NonClear++
			explained[target] = true
		}
	}

	var missing []string
	for key, where := range need {
		if explained[key] {
			res.Explained = append(res.Explained, key)
		} else {
			missing = append(missing, key+" ("+where+")")
		}
	}
	for concern, cause := range active {
		if _, ok := need[concern]; !ok && cause == "" {
			res.Silent = append(res.Silent, concern)
		}
	}
	sort.Strings(res.Explained)
	sort.Strings(res.Silent)
	sort.Strings(missing)

	if len(missing) > 0 {
		problems = append(problems, fmt.Errorf("%d key(s) refused or waited with no concern_transitions row naming a cause (a planner stopped emitting): %v", len(missing), missing))
	}
	switch {
	case len(active) == 0:
		problems = append(problems, errors.New("no active concern: the review held no progress record, nothing was checked"))
	case res.Planner == 0:
		problems = append(problems, errors.New("no planner refused or waited in the window: nothing was checked"))
	case res.NonClear == 0:
		problems = append(problems, errors.New("concern_transitions holds no row with a cause"))
	}
	return res, errors.Join(problems...)
}

// checkRow holds one transition row to the closed shape and returns its
// target and cause.
func checkRow(r store.ConcernTransition) (target string, cause policy.Cause, err error) {
	target, verdict, reason := r.Concern, r.Verdict, r.Cause
	if target == "" {
		return "", "", errors.New("no target")
	}
	if reason == "" {
		if !slices.Contains(clearOutcomes, verdict) {
			return target, "", fmt.Errorf("%s: clear row with verdict %q", target, verdict)
		}
	} else {
		if !slices.Contains(causeOutcomes, verdict) {
			return target, "", fmt.Errorf("%s: verdict %q with cause %q", target, verdict, reason)
		}
		if err := named(policy.Cause(reason)); err != nil {
			return target, "", fmt.Errorf("%s: %w", target, err)
		}
	}
	if r.Previous != "" {
		if err := named(policy.Cause(r.Previous)); err != nil {
			return target, "", fmt.Errorf("%s previous cause: %w", target, err)
		}
	}
	if r.Subject != policy.BoundSubject(r.Subject) {
		return target, "", fmt.Errorf("%s: subject %q is not a bounded printable subject", target, r.Subject)
	}
	if !idLike(r.Method) {
		return target, "", fmt.Errorf("%s: method %q is not an identifier", target, r.Method)
	}
	return target, policy.Cause(reason), nil
}

// named reports a cause the wording table does not name: unknown to the closed
// set or served the generic fallback sentence.
func named(c policy.Cause) error {
	if err := c.Validate(); err != nil {
		return err
	}
	if policy.Wording(c, "") == policy.Wording("", "") {
		return fmt.Errorf("cause %q has no plain-language wording", string(c))
	}
	return nil
}

// idLike is an identifier-shaped method: printable ASCII with no space, so a
// sentence cannot ride in the field.
func idLike(s string) bool {
	if len(s) > 80 {
		return false
	}
	for _, r := range s {
		if r <= ' ' || r > '~' {
			return false
		}
	}
	return true
}
