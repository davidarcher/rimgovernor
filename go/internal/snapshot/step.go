package snapshot

import (
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"

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

// RecordStep writes goal's step read into dir as
// step-<planner>-<goal>-<tick>-<seq>.json, seq the first from 1 not already taken.
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
	if err = os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	for seq := 1; ; seq++ {
		f, err := os.OpenFile(filepath.Join(dir, fmt.Sprintf("step-%s-%s-%d-%d.json", planner, goal, tick, seq)), os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o644)
		if errors.Is(err, fs.ErrExist) {
			continue
		}
		if err != nil {
			return err
		}
		_, err = f.Write(data)
		return errors.Join(err, f.Close())
	}
}

// LoadStep reads a recorded planner step read.
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
