package observation

import (
	"context"
	"errors"
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
	// extra is the definition catalog the frame carries, nil for none.
	extra   []*o.PlanningDefinition
	onFrame func()
	frame   bridge.RoutineFrame
	// racesErr fails the race catalog read.
	racesErr error
}

// testCatalog is a definition catalog of rows.
func testCatalog(rows ...*o.PlanningDefinition) *bridge.DefinitionCatalog {
	catalog := &bridge.DefinitionCatalog{Definitions: map[string]*o.PlanningDefinition{}}
	for _, row := range rows {
		catalog.Definitions[row.GetDefinition().GetDefName()] = row
	}
	return catalog
}

// ReadRoutineFrame is the frame over the colony reply, with the sections
// the test set in frame and the catalog of extra; the emergency census is
// empty by default.
func (s *projectSource) ReadRoutineFrame(context.Context, *c.Identity) (bridge.RoutineFrame, error) {
	if s.onFrame != nil {
		s.onFrame()
	}
	frame := s.frame
	observed := s.reply.GetObserved()
	frame.Context, frame.Colony = observed.Context, observed
	if frame.Emergency.Context == nil {
		frame.Emergency = bridge.EmergencyObservation{Context: observed.Context}
	}
	if s.extra != nil {
		frame.Catalog = testCatalog(s.extra...)
	}
	return frame, nil
}

// AnimalRaceCatalog is the empty race catalog (the observation source requires one).
func (s *projectSource) AnimalRaceCatalog(context.Context, *c.Identity) (*bridge.AnimalRaces, error) {
	if s.racesErr != nil {
		return nil, s.racesErr
	}
	return &bridge.AnimalRaces{}, nil
}

func TestRoutineProjectDefinitionsStayInsideObservationBracket(t *testing.T) {
	for _, phase := range []string{"supplement", "default-only", "expired", "cancelled", "uncataloged", "races-unread"} {
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
			wall := &o.PlanningDefinition{Definition: &o.DefinitionRef{DefName: proto.String("Wall")}, Stuff: proto.String("WoodLog"), ConstructionSkill: proto.Int32(0)}
			row := &o.PlanningDefinition{Definition: &o.DefinitionRef{DefName: proto.String("HospitalBed")}, ConstructionSkill: proto.Int32(8), ResearchPrerequisites: []string{"Medicine"}}
			s := &projectSource{colonySource: &colonySource{reply: base}, extra: []*o.PlanningDefinition{wall, row}}
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
			case "uncataloged":
				row.Definition.DefName = proto.String("Door")
			case "races-unread":
				s.racesErr = bridge.ErrUnavailable
			}
			out, err := observeRoutineUnowned(ctx, s, clock, expected, time.Second, names...)
			if phase == "expired" || phase == "cancelled" {
				if err == nil {
					t.Fatal("unsafe extra read accepted")
				}
				return
			}
			if phase == "races-unread" {
				if !errors.Is(err, bridge.ErrUnavailable) {
					t.Fatal("an unreadable race catalog must fail the reading", err)
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			// The starter definitions resolve first; a frame without
			// research leaves a definition behind research unknown, and a
			// name the catalog lacks is unavailable.
			var wallRow, bedRow PlanningDefinition
			for _, d := range out.Projection.Definitions {
				switch d.Name {
				case "Wall":
					wallRow = d
				case "HospitalBed":
					bedRow = d
				}
			}
			if wallRow.Available != domain.Known(true) || wallRow.Stuff != domain.Known("WoodLog") {
				t.Fatal(wallRow)
			}
			want := domain.Unknown[bool]()
			if phase == "uncataloged" {
				want = domain.Known(false)
			}
			if len(names) == 2 && (bedRow.Name != "HospitalBed" || bedRow.Available != want) {
				t.Fatal(bedRow)
			}
			if len(names) == 2 && !reflect.DeepEqual(out.Projection.Definitions[len(out.Projection.Definitions)-1].Name, names[len(names)-1]) {
				t.Fatal(out.Projection.Definitions)
			}
		})
	}
}

// observeRoutineUnowned reads the routine census with no construction claims.
func observeRoutineUnowned(ctx context.Context, source RoutineSource, clock Clock, expected Identity, maxAge time.Duration, definitions ...string) (RoutineReading, error) {
	return ObserveRoutineOwned(ctx, source, clock, expected, maxAge, domain.Unknown[[]policy.ConstructionClaim](), definitions...)
}
