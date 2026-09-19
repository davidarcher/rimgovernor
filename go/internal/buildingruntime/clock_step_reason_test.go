package buildingruntime

import (
	"context"
	"errors"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

func selectedPlanners(reason StepReason, kindOf func(domain.ActionID) (domain.ActionKind, bool)) (bool, []string) {
	planners, pick := plannerSelection(reason, kindOf)
	if !planners {
		return false, nil
	}
	var names []string
	for _, entry := range plannerCatalog {
		if pick == nil || pick(entry) {
			names = append(names, entry.name)
		}
	}
	return true, names
}

// TestPlannerSelectionByReason: a full or settled step selects the whole
// catalog; a timer step selects it only once the tick moved; a wake selects
// the planners of the latched outcomes' kinds and the readers of the
// invalidated families, and everything when an outcome's kind is unknown or
// authority changed.
func TestPlannerSelectionByReason(t *testing.T) {
	t.Parallel()
	all := make([]string, 0, len(plannerCatalog))
	for _, entry := range plannerCatalog {
		all = append(all, entry.name)
	}
	kinds := map[domain.ActionID]domain.ActionKind{"haul-1": domain.HaulAction, "wall-1": domain.BuildingAction}
	kindOf := func(id domain.ActionID) (domain.ActionKind, bool) { kind, ok := kinds[id]; return kind, ok }
	cases := []struct {
		name     string
		reason   StepReason
		planners bool
		want     []string
	}{
		{"full", StepReason{Cause: StepFull}, true, all},
		{"settled", StepReason{Cause: StepSettled}, true, all},
		{"timer same tick", StepReason{Cause: StepTimer}, false, nil},
		{"timer tick advanced", StepReason{Cause: StepTimer, TickAdvanced: true}, true, all},
		{"wake haul", StepReason{Cause: StepWake, Events: []WakeOutcome{{Action: "haul-1", Attempt: 1, Terminal: true}}}, true, []string{"secureSupplies", "haul", "foodStorageUpkeep"}},
		{"wake research family", StepReason{Cause: StepWake, Families: []bridge.FactFamily{bridge.FactResearch}}, true, []string{"research"}},
		{"wake unknown kind", StepReason{Cause: StepWake, Events: []WakeOutcome{{Action: "ghost", Attempt: 1}}}, true, all},
		{"wake authority", StepReason{Cause: StepWake, Authority: true}, true, all},
		{"wake empty", StepReason{Cause: StepWake}, true, all},
	}
	for _, tc := range cases {
		planners, got := selectedPlanners(tc.reason, kindOf)
		if planners != tc.planners || !reflect.DeepEqual(got, tc.want) {
			t.Errorf("%s: planners=%v %v, want %v %v", tc.name, planners, got, tc.planners, tc.want)
		}
	}
	// A building wake selects every construction planner (research stages
	// its bench through one, #254) and no pawn-only one.
	_, building := selectedPlanners(StepReason{Cause: StepWake, Events: []WakeOutcome{{Action: "wall-1", Attempt: 1, Terminal: true}}}, kindOf)
	set := map[string]bool{}
	for _, name := range building {
		set[name] = true
	}
	if !set["sleeping"] || !set["defenseLayout"] || !set["research"] || set["haul"] || set["tend"] {
		t.Fatal(building)
	}
}

// TestClockSchedulerTimerStepSkipsPlannersUntilTickMoves: after the first
// (full) step, a timer step at the same tick runs the admission tail alone
// (one status read, nothing planned), a timer step after the tick moved
// plans everything, and the FullStepEvery safety net promotes a same-tick
// timer step to a full one.
func TestClockSchedulerTimerStepSkipsPlannersUntilTickMoves(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	s.config.FullStepEvery = time.Hour
	ctx := context.Background()
	first, err := s.Step(ctx)
	if err != nil || first.Reason.Cause != StepFull || !first.Reason.TickAdvanced || first.Attempt == nil {
		t.Fatal(first, err)
	}
	// The window ran out under our epoch; the next step retires it and,
	// the tick having moved, reviews in the same step (issue #162).
	epoch := f.status.GetRunning().GetEpoch()
	f.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epoch, Reason: k.StopReason_STOP_REASON_TICK_BUDGET.Enum(), ActualPaused: proto.Bool(true), PauseVerified: proto.Bool(true), PauseRequested: proto.Bool(false), StoppedAtUnixMs: proto.Int64(1)}}
	f.status.Context.Tick = proto.Int64(f.status.Context.GetTick() + 100)
	settled, err := s.StepWithReason(ctx, StepReason{Cause: StepTimer})
	if !errors.Is(err, executor.ErrHeld) || settled.Cleaned || settled.Reason.Cause != StepTimer || !settled.Reason.TickAdvanced {
		t.Fatal(settled, err)
	}
	reads := f.reads
	timer, err := s.StepWithReason(ctx, StepReason{Cause: StepTimer})
	if timer.Reason.Cause != StepTimer || timer.Reason.TickAdvanced || timer.Planners != nil || timer.Routine != nil || f.reads != reads+1 {
		t.Fatal(timer, err, f.reads-reads)
	}
	s.lastFull = time.Time{}
	promoted, err := s.StepWithReason(ctx, StepReason{Cause: StepTimer})
	if promoted.Reason.Cause != StepFull || promoted.Reason.TickAdvanced {
		t.Fatal(promoted, err)
	}
	f.status.Context.Tick = proto.Int64(f.status.Context.GetTick() + 10)
	advanced, err := s.StepWithReason(ctx, StepReason{Cause: StepTimer})
	if advanced.Reason.Cause != StepTimer || !advanced.Reason.TickAdvanced {
		t.Fatal(advanced, err)
	}
}
