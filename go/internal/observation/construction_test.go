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
	pp "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func TestConstructionReadsTheFramesBuiltBuildings(t *testing.T) {
	for _, phase := range []string{"stable", "no-stuff", "unknown-stuff", "unknown-claims", "empty-claims", "absent"} {
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
			row := &o.BuildingState{Building: &o.EntityRef{Id: proto.String("wall"), DefName: proto.String("Wall"), MapId: proto.Int32(base.GetObserved().Context.Identity.GetMapId()), Position: &c.Cell{X: proto.Int32(3), Z: proto.Int32(7)}}, Occupied: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(3), Z: proto.Int32(7)}, Maximum: &c.Cell{X: proto.Int32(3), Z: proto.Int32(7)}}, Rotation: pp.Rotation_ROTATION_NORTH.Enum(), Status: o.BuildingStatus_BUILDING_STATUS_BUILT.Enum(), Stuff: proto.String("WoodLog")}
			snapshot := &o.BuildingsSnapshot{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), Buildings: []*o.BuildingState{row}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}
			s := &projectSource{colonySource: &colonySource{reply: base}, frame: bridge.RoutineFrame{Buildings: bridge.BuildingCensusOf(snapshot)}}
			b, _ := domain.NewBuilding("Wall", domain.Cell{X: 3, Z: 7}, domain.North, "WoodLog")
			claims := domain.Known([]policy.ConstructionClaim{{Plan: "method", Action: "placed", Goal: "goal", Identity: domain.ConstructionIdentity{Origin: "blueprint", Current: "wall"}, Building: b}})
			clock := testkit.NewManualClock(time.Now())
			switch phase {
			case "absent":
				s.frame.Buildings = nil
			case "no-stuff":
				row.Stuff = nil
				row.Issues = []*o.ReadIssue{{Field: proto.String("stuff"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}
			case "unknown-stuff":
				row.Stuff = nil
			case "unknown-claims":
				claims = domain.Unknown[[]policy.ConstructionClaim]()
			case "empty-claims":
				claims = domain.Known([]policy.ConstructionClaim{})
			}
			got, err := ObserveRoutineOwned(context.Background(), s, clock, expected, time.Second, claims)
			if err != nil {
				t.Fatal(phase, err)
			}
			current, known := got.Projection.Facts.CurrentConstruction.Value()
			if known != (phase != "unknown-stuff" && phase != "absent") {
				t.Fatal(phase, known)
			}
			if known && (!current.Colony || len(current.Requested) != 0 || len(current.Buildings) != 1 || current.Buildings[0].ID != "wall" || current.Buildings[0].Building.Rotation() != domain.North) {
				t.Fatal(current)
			}
		})
	}
}
