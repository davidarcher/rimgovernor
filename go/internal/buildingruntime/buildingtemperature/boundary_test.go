package buildingtemperature

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type temperatureFixture struct {
	*boundary.Fixture
	target bridge.BuildingTemperatureTarget
}

func (f *temperatureFixture) ReadBuildingTemperatureTarget(context.Context, *c.Identity, string) (bridge.BuildingTemperatureTarget, bridge.Result, error) {
	return f.target, bridge.Result{}, nil
}
func (f *temperatureFixture) PreviewBuildingTemperature(context.Context, *c.Identity, domain.BuildingTemperature) (*op.PreviewReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}
func (f *temperatureFixture) LookupBuildingTemperature(context.Context, bridge.BuildingTemperatureAttempt) (*r.LookupReply, bridge.Result, error) {
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: f.Receipt}}, bridge.Result{}, nil
}
func (f *temperatureFixture) ObserveBuildingTemperature(context.Context, bridge.BuildingTemperatureAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: f.Progress}}, bridge.Result{}, nil
}
func (f *temperatureFixture) ApplyBuildingTemperature(context.Context, *a.WritePrecondition, domain.BuildingTemperature) (*op.ExecuteReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}

func newTemperatureFixture(t *testing.T) (*Boundary, *temperatureFixture) {
	t.Helper()
	base, bf := boundary.NewFixture(t)
	temperature, err := domain.NewBuildingTemperature("Thing_Cooler1", -5, "temp-before")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewBuildingTemperatureAction("action", temperature)
	if err != nil {
		t.Fatal(err)
	}
	bf.Placement.Action = action
	evidence := &r.EffectEvidence{Effect: &r.EffectEvidence_Settings{Settings: &r.SettingsEffect{Snapshot: &r.SnapshotEvidence{EntityId: proto.String("Thing_Cooler1"), BeforeToken: proto.String("temp-before"), AfterToken: proto.String("temp-after")}}}}
	bf.Progress.Effect = &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: evidence}}
	f := &temperatureFixture{Fixture: bf, target: bridge.BuildingTemperatureTarget{Context: proto.Clone(bf.Progress.Context).(*c.ObservationContext), Thing: "Thing_Cooler1", Token: "temp-after", Temperature: -5}}
	return NewBoundary(base, Capabilities{Native: f, Writer: f}), f
}

// The setpoint re-read follows the observation, so under a running clock it
// lands a later tick. That is the same completed patch, not invalid evidence
// (#755: refrigeration/season's accepted patch stayed awaiting_observation
// because every retry demanded both reads on one tick); a re-read from
// before the observation still is.
func TestObserveBuildingTemperatureAcceptsAReReadAfterTheClockAdvanced(t *testing.T) {
	t.Parallel()
	b, f := newTemperatureFixture(t)
	f.target.Context.Tick = proto.Int64(f.Progress.Context.GetTick() + 369)
	out, err := b.ObserveBuildingTemperature(context.Background(), f.Placement, f.Placement.Snapshot)
	if err != nil || !out.Complete || out.Observation.Effect != domain.EffectCompleted {
		t.Fatal(err, out)
	}
	f.target.Context.Tick = proto.Int64(f.Progress.Context.GetTick() - 1)
	if _, err := b.ObserveBuildingTemperature(context.Background(), f.Placement, f.Placement.Snapshot); !errors.Is(err, executor.ErrEvidence) {
		t.Fatal(err)
	}
}
