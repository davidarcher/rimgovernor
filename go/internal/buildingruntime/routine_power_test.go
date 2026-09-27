package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestRoutinePowerCensusReachesDurableNeed(t *testing.T) {
	t.Parallel()
	r, _, _, _, native := routineFixture(t)
	v := native.reply.GetObserved()
	for _, phase := range []string{"no-consumers", "disconnected", "powered", "unknown"} {
		p := &o.DevelopmentFacts{Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}
		want := domain.NeedRecovered
		if phase != "no-consumers" {
			p.Power = []*o.DevelopmentPower{{BaseW: proto.Float64(-200), Building: &o.BuildingState{Building: &o.EntityRef{Id: proto.String("consumer"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Service: &o.BuildingServiceState{Connected: proto.Bool(false), PowerOn: proto.Bool(false), PowerOutputW: proto.Float64(0), SwitchedOn: proto.Bool(true)}, Settings: &o.BuildingSettings{Forbidden: proto.Bool(false)}}}}
			want = domain.NeedDeficit
			if phase == "powered" {
				s := p.Power[0].Building.Service
				s.Connected, s.PowerOn, s.PowerNetId = proto.Bool(true), proto.Bool(true), proto.String("net")
				want = domain.NeedRecovered
			}
			if phase == "unknown" {
				p.Power[0].BaseW = nil
				want = domain.NeedUnknown
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
				if a.Need != want {
					t.Fatal(phase, a, want)
				}
			}
		}
		if !found {
			t.Fatal("power need missing")
		}
	}
}
