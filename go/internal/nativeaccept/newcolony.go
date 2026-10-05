package nativeaccept

import (
	"context"
	"fmt"
	"strings"
	"time"
)

// NewColonyPhasePrefix prefixes the wire names of NewColonyPhase.
const NewColonyPhasePrefix = "NEW_COLONY_PHASE_"

// NewColonyPhaseOrder is the wire order a start's phases must follow.
var NewColonyPhaseOrder = []string{"GENERATING_WORLD", "CHOOSING_TILE", "ROLLING_COLONISTS", "GENERATING_MAP", "FINISHING", "SAVING"}

// NewColonyTimeout is the timeoutMs a harness start asks for when the caller
// names none: the team policy's reroll budget can take minutes.
const NewColonyTimeout = 10 * time.Minute

// NewColonyRequest wraps a wire spec map in a lifecycle_new_colony request.
func NewColonyRequest(id string, spec map[string]any, timeout time.Duration) map[string]any {
	if timeout <= 0 {
		timeout = NewColonyTimeout
	}
	return map[string]any{"requestId": id, "spec": spec, "timeoutMs": timeout.Milliseconds()}
}

// NewColonyRun is a finished lifecycle_new_colony start.
type NewColonyRun struct {
	// Phases are the phases seen in wire order, each once.
	Phases []string
	// Completed is the completed reply: saveName, seed, paused, byteLength, context.
	Completed map[string]any
	// Rerolls is the last rerollCount a pending reply carried (completed
	// replies carry none), 0 when the first roll was accepted.
	Rerolls int
}

// RunNewColony starts a colony (lifecycle_new_colony) and polls until it
// completes. The game must be at the main menu; it ends paused with a live
// map, the naming dialog confirmed and the save in the profile's Saves. A
// terminal failure is an error (a failed start can leave game state behind:
// restart the game before retrying); phases must follow NewColonyPhaseOrder
// without going back.
func RunNewColony(ctx context.Context, h *Harness, request map[string]any, label string) (NewColonyRun, error) {
	var run NewColonyRun
	id := AsString(request["requestId"])
	reply, err := h.Wire(ctx, label+"-start", "lifecycle_new_colony", request)
	if err != nil {
		return run, err
	}
	last := -1
	for attempt := 0; attempt < 6000; attempt++ {
		if code, ok := FailureCode(reply); ok {
			failure, _ := AsMap(reply["failure"])
			return run, fmt.Errorf("%s: new colony failed: %s: %v", label, code, failure["detail"])
		}
		if _, completed, err := Outcome(reply, "completed"); err == nil {
			if indexOf(run.Phases, "SAVING") < 0 {
				return run, fmt.Errorf("%s: completed without reporting SAVING (phases %v)", label, run.Phases)
			}
			run.Completed = completed
			return run, nil
		}
		_, pending, err := Outcome(reply, "pending")
		if err != nil {
			return run, fmt.Errorf("%s: unexpected reply shape: %v", label, reply)
		}
		phase := strings.TrimPrefix(AsString(pending["phase"]), NewColonyPhasePrefix)
		index := indexOf(NewColonyPhaseOrder, phase)
		if index < 0 {
			return run, fmt.Errorf("%s: unknown phase %q", label, phase)
		}
		if index < last {
			return run, fmt.Errorf("%s: phase went backwards to %s after %s", label, phase, run.Phases[len(run.Phases)-1])
		}
		if index != last {
			run.Phases = append(run.Phases, phase)
			last = index
		}
		run.Rerolls = int(AsNumber(pending["rerollCount"]))
		select {
		case <-ctx.Done():
			return run, ctx.Err()
		case <-time.After(250 * time.Millisecond):
		}
		reply, err = h.Wire(ctx, fmt.Sprintf("%s-poll-%d", label, attempt), "lifecycle_read_new_colony", map[string]any{"requestId": id})
		if err != nil {
			return run, err
		}
	}
	return run, fmt.Errorf("%s: new colony did not complete within the polling budget (phases %v)", label, run.Phases)
}

func indexOf(list []string, want string) int {
	for i, v := range list {
		if v == want {
			return i
		}
	}
	return -1
}
