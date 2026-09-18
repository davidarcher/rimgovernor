package observation

import (
	"context"
	"os"
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

type constructionSource struct {
	*projectSource
	buildings   *o.ListBuildingsReply
	onBuildings func()
	requested   []string
}

func (s *constructionSource) ReadConstructionBuildings(_ context.Context, _ *c.Identity, ids []string) (*o.ListBuildingsReply, bridge.Result, error) {
	s.requested = append([]string{}, ids...)
	if s.onBuildings != nil {
		s.onBuildings()
	}
	return s.buildings, bridge.Result{}, nil
}

func TestConstructionReadsStayInsidePausedRoutineBracket(t *testing.T) {
	for _, phase := range []string{"stable", "no-stuff", "unknown-stuff", "unknown-claims", "empty-claims", "changed-tick", "changed-generation", "expired", "cancelled"} {
		t.Run(phase, func(t *testing.T) {
			data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
			if err != nil {
				t.Fatal(err)
			}
			base := &o.ColonyFactsReply{}
			if err = protojson.Unmarshal(data, base); err != nil {
				t.Fatal(err)
			}
			identity := func() *l.IdentityReply {
				return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), Paused: proto.Bool(true)}}}
			}
			expected, err := DecodeIdentity(identity())
			if err != nil {
				t.Fatal(err)
			}
			row := &o.BuildingState{Building: &o.EntityRef{Id: proto.String("wall"), DefName: proto.String("Wall"), MapId: proto.Int32(base.GetObserved().Context.Identity.GetMapId()), Position: &c.Cell{X: proto.Int32(3), Z: proto.Int32(7)}}, Rotation: proto.String("North"), Status: proto.String("built"), Stuff: proto.String("WoodLog")}
			snapshot := &o.BuildingsSnapshot{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), Buildings: []*o.BuildingState{row}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}
			s := &constructionSource{projectSource: &projectSource{colonySource: &colonySource{reply: base}}, buildings: &o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: snapshot}}}
			b, _ := domain.NewBuilding("Wall", domain.Cell{X: 3, Z: 7}, domain.North, "WoodLog")
			claims := domain.Known([]policy.ConstructionClaim{{Plan: "method", Action: "placed", Goal: "goal", Identity: domain.ConstructionIdentity{Origin: "blueprint", Current: "wall"}, Building: b}})
			clock := testkit.NewManualClock(time.Now())
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			switch phase {
			case "no-stuff":
				row.Stuff = nil
				row.Issues = []*o.ReadIssue{{Field: proto.String("stuff"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}
			case "unknown-stuff":
				row.Stuff = nil
			case "unknown-claims":
				claims = domain.Unknown[[]policy.ConstructionClaim]()
			case "empty-claims":
				claims = domain.Known([]policy.ConstructionClaim{})
			case "changed-tick":
				snapshot.Context.Tick = proto.Int64(int64(expected.Tick + 1))
			case "changed-generation":
				snapshot.Context.NativeGeneration = proto.Uint64(snapshot.Context.GetNativeGeneration() + 1)
			case "expired":
				s.onBuildings = func() { clock.Advance(2 * time.Second) }
			case "cancelled":
				s.onBuildings = cancel
			}
			got, err := ObserveRoutineOwned(ctx, s, clock, expected, time.Second, claims)
			bad := phase == "changed-tick" || phase == "changed-generation" || phase == "expired" || phase == "cancelled"
			if (err != nil) != bad {
				t.Fatal(phase, err)
			}
			if bad {
				if got.Projection.Identity.Colony != "" {
					t.Fatal("failed bracket published facts")
				}
				return
			}
			current, known := got.Projection.Facts.CurrentConstruction.Value()
			if known != (phase != "unknown-stuff" && phase != "unknown-claims") {
				t.Fatal(phase, known)
			}
			if phase == "empty-claims" || phase == "unknown-claims" {
				if len(s.requested) != 0 {
					t.Fatal("unnecessary native query")
				}
				return
			}
			if len(s.requested) != 1 || s.requested[0] != "wall" {
				t.Fatal(s.requested)
			}
			if known && (len(current.Requested) != 1 || len(current.Buildings) != 1 || current.Buildings[0].ID != "wall" || current.Buildings[0].Building.Rotation() != domain.North) {
				t.Fatal(current)
			}
		})
	}
}
