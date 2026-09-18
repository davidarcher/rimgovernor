package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"time"
)

// ClockWindowRequest carries fresh policy facts through the serialized command.
//
// Status is the validated owned clock status the admitting step read after
// its planners (the second bundle) and StatusAt when it was read. When both
// are set and StatusAt is within MaxAge of dispatch, the coordinator's
// pre-dispatch inspection checks that status instead of reading
// clock_read_status again: the bundle was the step's last native round trip
// and every call to the game host is serial, so a second read would answer
// with the same paused tick a few tens of milliseconds later (#200). A nil
// Status, or one older than MaxAge, keeps the native read.
type ClockWindowRequest struct {
	Intent         store.ClockIntent
	Facts          policy.ClockWindowFacts
	MaxAge         time.Duration
	CombatMaxTicks uint32
	Status         *k.Status
	StatusAt       time.Time
}
