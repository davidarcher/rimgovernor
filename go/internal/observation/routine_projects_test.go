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
	// extra is the catalog the frame serves subscribed definitions from.
	extra     []*o.PlanningDefinition
	requested []string
	onFrame   func()
	frame     bridge.RoutineFrame
}

// ReadRoutineFrame is the frame over the colony reply, with the sections
// the test set in frame and the subscribed definitions found in extra;
// the emergency census is empty by default.
func (s *projectSource) ReadRoutineFrame(_ context.Context, _ *c.Identity, definitions []string) (bridge.RoutineFrame, error) {
	s.requested = append([]string(nil), definitions...)
	if s.onFrame != nil {
		s.onFrame()
	}
	frame := s.frame
	observed := s.reply.GetObserved()
	frame.Context, frame.Colony = observed.Context, observed
	if frame.Emergency.Context == nil {
		frame.Emergency = bridge.EmergencyObservation{Context: observed.Context}
	}
	frame.Definitions = nil
	for _, row := range s.extra {
		for _, name := range definitions {
			if row.Definition.GetDefName() == name {
				frame.Definitions = append(frame.Definitions, row)
			}
		}
	}
	return frame, nil
}

func TestRoutineProjectDefinitionsStayInsideObservationBracket(t *testing.T) {
	for _, phase := range []string{"supplement", "default-only", "expired", "cancelled", "unrequested"} {
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
			row := proto.Clone(base.GetObserved().Planning.GetObserved().Definitions[0]).(*o.PlanningDefinition)
			row.Definition.DefName = proto.String("HospitalBed")
			row.ConstructionSkill = proto.Int32(8)
			s := &projectSource{colonySource: &colonySource{reply: base}, extra: []*o.PlanningDefinition{row}}
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
			case "expired":
				s.onFrame = func() { clock.Advance(2 * time.Second) }
			case "cancelled":
				s.onFrame = cancel
			case "unrequested":
				row.Definition.DefName = proto.String("Door")
			}
			out, err := observeRoutineUnowned(ctx, s, clock, expected, time.Second, names...)
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
			if !reflect.DeepEqual(s.requested, names) {
				t.Fatal(s.requested)
			}
		})
	}
}

// observeRoutineUnowned reads the routine census with no construction claims.
func observeRoutineUnowned(ctx context.Context, source RoutineSource, clock Clock, expected Identity, maxAge time.Duration, definitions ...string) (RoutineReading, error) {
	return ObserveRoutineOwned(ctx, source, clock, expected, maxAge, domain.Unknown[[]policy.ConstructionClaim](), definitions...)
}
