package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitBuildingTemperature stores explicit player intent to patch one
// already-observed temperature-controlled building's target setpoint, under
// the shared player gate. Building temperature is player-command-driven, the
// same as tend: it never runs through a routine planner, only this direct
// submission. Submission neither acquires authority nor issues a native
// command.
func (p *Player) SubmitBuildingTemperature(ctx context.Context, request store.BuildingTemperatureSubmissionRequest) (store.BuildingTemperatureSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.BuildingTemperatureSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupBuildingTemperatureSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.BuildingTemperatureSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.BuildingTemperatureSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.BuildingTemperatureSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.BuildingTemperatureSubmission{}, false, err
	}
	return p.journal.SubmitBuildingTemperature(call, request)
}
