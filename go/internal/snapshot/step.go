package snapshot

import (
	"encoding/json"
	"fmt"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Step is one planner step's own colony read (#794): the projection a
// building or bill planner decided from, which carries what the review's
// read lacks (rooms, the step's definitions, fresh benches). A test
// replays the planner's selector over Projection.
type Step struct {
	Recorded string
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Goal     policy.GoalID
	// Planner is "building" or "bill".
	Planner string
	// Projection is the step's reading with its Facts; its Zones and
	// Window are not recorded, as in Routine.
	Projection observation.ColonyProjection
}

// stepFrame is a step read as a stream line records it (#795 step 4): the
// step's tree as a patch against the last review line's projection (its
// Facts folded back in), so the line carries the planner's extras and
// whatever moved since the review, not a second projection. A bound field
// the mirror sections materialise is left to them, as in a review line.
type stepFrame struct {
	Planner string
	Goal    policy.GoalID
	Patch   json.RawMessage
}

// StepRead names one step read in a stream, as the per-step files were
// named before #795 step 4: step-<planner>-<goal>-<tick>-<seq>.
type StepRead struct {
	Planner string
	Goal    policy.GoalID
	Tick    domain.Tick
	Seq     int
}

func (s StepRead) String() string {
	return fmt.Sprintf("step-%s-%s-%d-%d", s.Planner, s.Goal, s.Tick, s.Seq)
}

// stepBase is the tree a step line patches: the review tree's projection
// with the review's Facts, the part of a Step a review line holds.
func stepBase(review any) any {
	r, _ := review.(map[string]any)
	proj, ok := r["Projection"].(map[string]any)
	if !ok {
		return map[string]any{}
	}
	out := make(map[string]any, len(proj)+1)
	for k, v := range proj {
		out[k] = v
	}
	if f, ok := r["Facts"]; ok {
		out["Facts"] = f
	}
	return map[string]any{"Projection": out}
}

// RecordStep appends goal's step read to this process's stream in dir,
// numbered from 1 per planner, goal and tick.
func RecordStep(dir, planner string, goal policy.GoalID, current domain.GenerationSnapshot, reading observation.ColonyProjection) error {
	var none observation.ColonyProjection
	reading.Zones, reading.Window = none.Zones, none.Window
	tick := reading.Identity.Tick
	data, err := Encode(Step{
		Recorded: fmt.Sprintf("colony %s load %s map %d tick %d goal %s", current.Colony, current.Load, current.Map, tick, goal),
		Snapshot: current, Tick: tick, Goal: goal, Planner: planner, Projection: reading,
	})
	if err != nil {
		return err
	}
	tree, err := parseTree(data)
	if err != nil {
		return err
	}
	streamsMu.Lock()
	defer streamsMu.Unlock()
	rec, err := openStream(dir, tick)
	if err != nil {
		return err
	}
	line := streamLine{Tick: tick, Step: &stepFrame{Planner: planner, Goal: goal}}
	tree, line.Mirror = elide(tree, rec.sections)
	patch, changed := diffTree(stepBase(rec.prev), tree)
	if !changed {
		patch = map[string]any{"~": map[string]any{}}
	}
	if line.Step.Patch, err = json.Marshal(patch); err != nil {
		return err
	}
	name := StepRead{Planner: planner, Goal: goal, Tick: tick}.String()
	line.Seq = rec.steps[name] + 1
	if err = rec.append(line); err != nil {
		return err
	}
	rec.steps[name] = line.Seq
	return nil
}

// Steps lists the step reads a stream recorded, in order.
func Steps(path string) ([]StepRead, error) {
	var out []StepRead
	err := walk(path, func(line streamLine, _ *replayState) (bool, error) {
		if line.Step != nil {
			out = append(out, StepRead{Planner: line.Step.Planner, Goal: line.Step.Goal, Tick: line.Tick, Seq: line.Seq})
		}
		return true, nil
	})
	return out, err
}

// LoadStreamStep materialises the step read name (StepRead.String) of the
// stream at path.
func LoadStreamStep(path, name string) (Step, error) {
	var out Step
	found := false
	// A step read at a tick may precede that tick's reviews: sync strictly
	// before it.
	before := func(domain.Tick, int) bool { return false }
	if parts := strings.Split(name, "-"); len(parts) >= 3 {
		if tick, err := strconv.ParseInt(parts[len(parts)-2], 10, 64); err == nil {
			before = func(t domain.Tick, _ int) bool { return t < domain.Tick(tick) }
		}
	}
	err := seek(path, before, func(from int64) (bool, error) {
		found = false
		err := walkFrom(path, from, visitStep(name, &out, &found))
		return found, err
	})
	if err != nil || found {
		return out, err
	}
	steps, err := Steps(path)
	if err != nil {
		return Step{}, err
	}
	names := make([]string, 0, len(steps))
	for _, s := range steps {
		names = append(names, s.String())
	}
	return Step{}, fmt.Errorf("%s: no %s; recorded %s", path, name, strings.Join(names, " "))
}

// visitStep materialises the step read name into out.
func visitStep(name string, out *Step, found *bool) func(streamLine, *replayState) (bool, error) {
	return func(line streamLine, st *replayState) (bool, error) {
		if line.Step == nil || (StepRead{Planner: line.Step.Planner, Goal: line.Step.Goal, Tick: line.Tick, Seq: line.Seq}).String() != name {
			return true, nil
		}
		node, err := parseTree(line.Step.Patch)
		if err != nil {
			return false, err
		}
		patch, _ := node.(map[string]any)
		tree, err := applyPatch(stepBase(st.tree), patch)
		if err != nil {
			return false, err
		}
		if tree, err = restore(tree, line.Mirror, st.sections); err != nil {
			return false, err
		}
		data, err := json.Marshal(tree)
		if err != nil {
			return false, err
		}
		*found = true
		return false, Decode(data, out)
	}
}

// LoadStep reads a recorded planner step read: committed testdata, or a
// per-step file recorded before #795 step 4.
func LoadStep(path string) (Step, error) {
	data, err := readFile(path)
	if err != nil {
		return Step{}, fmt.Errorf("%s: %w", path, err)
	}
	var s Step
	if err = Decode(data, &s); err != nil {
		return Step{}, fmt.Errorf("%s: %w", path, err)
	}
	return s, nil
}
