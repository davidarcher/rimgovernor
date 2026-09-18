package observation

import (
	"context"
	"os"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

type projectSource struct {
	*colonySource
	extra     *o.ColonyFactsReply
	requested []string
	onExtra   func()
}

func (s *projectSource) ReadColonyFacts(ctx context.Context, id *c.Identity, planning bool, defs []string) (*o.ColonyFactsReply, bridge.Result, error) {
	if len(defs) == 0 {
		return s.colonySource.ReadColonyFacts(ctx, id, planning, defs)
	}
	s.requested = append([]string(nil), defs...)
	if s.onExtra != nil {
		s.onExtra()
	}
	return s.extra, bridge.Result{}, nil
}
func (s *projectSource) ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return bridge.EmergencyObservation{Context: s.reply.GetObserved().Context, Facts: policy.EmergencyFacts{}}, bridge.Result{}, nil
}
func (s *projectSource) ReadRoutinePopulation(context.Context, *c.Identity) (bridge.PrisonerCensus, bridge.Result, error) {
	return bridge.PrisonerCensus{Context: s.reply.GetObserved().Context, Prisoners: domain.Known([]policy.PrisonerFacts{})}, bridge.Result{}, nil
}
func (s *projectSource) ReadRoutinePawns(context.Context, *c.Identity, []string) (*o.ListPawnsReply, bridge.Result, error) {
	panic("unknown census must skip pawns")
}

func TestRoutineProjectDefinitionsStayInsideObservationBracket(t *testing.T) {
	for _, phase := range []string{"supplement", "default-only", "changed-tick", "expired", "cancelled", "unrequested"} {
		t.Run(phase, func(t *testing.T) {
			data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
			if err != nil {
				t.Fatal(err)
			}
			base := &o.ColonyFactsReply{}
			if err := protojson.Unmarshal(data, base); err != nil {
				t.Fatal(err)
			}
			identity := func() *l.IdentityReply {
				return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), Paused: proto.Bool(true)}}}
			}
			s := &projectSource{colonySource: &colonySource{reply: base}, extra: proto.Clone(base).(*o.ColonyFactsReply)}
			s.extra.GetObserved().Planning.GetObserved().Definitions[0].Definition.DefName = proto.String("HospitalBed")
			s.extra.GetObserved().Planning.GetObserved().Definitions[0].ConstructionSkill = proto.Int32(8)
			expected, err := DecodeIdentity(identity())
			if err != nil {
				t.Fatal(err)
			}
			clock := testkit.NewManualClock(time.Now())
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			names := []string{"Wall", "HospitalBed"}
			switch phase {
			case "default-only":
				names = []string{"Wall"}
			case "changed-tick":
				s.extra.GetObserved().Context.Tick = proto.Int64(int64(expected.Tick + 1))
				s.extra.GetObserved().Planning.GetObserved().Cells.Context.Tick = proto.Int64(int64(expected.Tick + 1))
			case "expired":
				s.onExtra = func() { clock.Advance(2 * time.Second) }
			case "cancelled":
				s.onExtra = cancel
			case "unrequested":
				s.extra.GetObserved().Planning.GetObserved().Definitions[0].Definition.DefName = proto.String("Door")
			}
			out, err := ObserveRoutine(ctx, s, clock, expected, time.Second, names...)
			if phase != "supplement" && phase != "default-only" {
				if err == nil {
					t.Fatal("unsafe extra read accepted")
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			if len(out.Projection.Definitions) != len(names) || out.Projection.Definitions[0].Name != "Wall" {
				t.Fatal(out.Projection.Definitions)
			}
			if phase == "supplement" && !reflect.DeepEqual(s.requested, []string{"HospitalBed"}) {
				t.Fatal(s.requested)
			}
			if phase == "default-only" && len(s.requested) != 0 {
				t.Fatal(s.requested)
			}
		})
	}
}
