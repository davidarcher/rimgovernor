package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestRoundsPowerCensusReachesDurableNeed(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, _, _, _, native := roundsFixture(t)
	v := native.reply.GetObserved()
	watts := 200.0
	consumer := buildable("PowerConsumer", 4, 1, 1)
	consumer.PowerW = &watts
	native.putCatalog(consumer)
	for _, phase := range []string{"no-consumers", "disconnected", "powered", "unknown"} {
		p := &o.DevelopmentFacts{}
		want := domain.FindingMet
		if phase != "no-consumers" {
			p.Power = []*o.DevelopmentPower{{Building: bridge.NewRef(native.building(&o.BuildingState{Building: &o.EntityRef{Id: proto.String("consumer"), DefName: proto.String("PowerConsumer"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Service: &o.BuildingServiceState{Connected: proto.Bool(false), PowerOn: proto.Bool(false), PowerOutputW: proto.Float64(0), SwitchedOn: proto.Bool(true)}, Settings: &o.BuildingSettings{Forbidden: proto.Bool(false)}}).GetId())}}
			want = domain.FindingUnmet
			if phase == "powered" {
				s := native.buildings.At("consumer").Service
				s.Connected, s.PowerOn, s.PowerNetId = proto.Bool(true), proto.Bool(true), proto.String("net")
				want = domain.FindingMet
			}
			if phase == "unknown" {
				native.buildings.At("consumer").Building.DefName = nil
				want = domain.FindingUnclear
			}
		}
		v.Development = &o.DevelopmentSection{Outcome: &o.DevelopmentSection_Observed{Observed: p}}
		out, err := r.Step(context.Background())
		if err != nil {
			t.Fatal(phase, err)
		}
		found := false
		for _, a := range out.Needs.Assessments {
			if a.ID == policy.EnsureBasicPower {
				found = true
				if a.Finding != want {
					t.Fatal(phase, a, want)
				}
			}
		}
		if !found {
			t.Fatal("power need missing")
		}
	}
}
