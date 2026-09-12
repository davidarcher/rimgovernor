package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
	"testing"
)

func TestColonyPowerRejectsPartialOrAmbiguousCensus(t *testing.T) {
	for _, phase := range []string{"valid", "unknown", "duplicate", "partial", "foreign-map", "infinite", "extra-detail", "disconnected-network"} {
		t.Run(phase, func(t *testing.T) {
			row := &o.DevelopmentPower{BaseW: proto.Float64(-200), Building: &o.BuildingState{Building: &o.EntityRef{Id: proto.String("building"), MapId: proto.Int32(0)}, Service: &o.BuildingServiceState{Connected: proto.Bool(true), PowerOn: proto.Bool(true), PowerOutputW: proto.Float64(-200), SwitchedOn: proto.Bool(true), PowerNetId: proto.String("net")}, Settings: &o.BuildingSettings{Forbidden: proto.Bool(false)}}}
			v := &o.DevelopmentFacts{Power: []*o.DevelopmentPower{row}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}
			switch phase {
			case "unknown":
				row.BaseW = nil
				row.Building.Service = &o.BuildingServiceState{}
			case "duplicate":
				v.Power = append(v.Power, proto.Clone(row).(*o.DevelopmentPower))
				v.Completeness.Matched = proto.Uint64(2)
				v.Completeness.Returned = proto.Uint64(2)
			case "partial":
				v.Completeness.Page.Complete = proto.Bool(false)
			case "foreign-map":
				row.Building.Building.MapId = proto.Int32(1)
			case "infinite":
				row.BaseW = proto.Float64(math.Inf(1))
			case "extra-detail":
				row.Building.Settings.Flickable = proto.Bool(true)
			case "disconnected-network":
				row.Building.Service.Connected = proto.Bool(false)
			}
			err := validateColonyPower(v, &c.Identity{MapId: proto.Int32(0)}, &o.MapSize{Width: proto.Uint32(10), Height: proto.Uint32(10)})
			if (err == nil) != (phase == "valid" || phase == "unknown") {
				t.Fatal(phase, err)
			}
		})
	}
}

func TestColonyPowerGeometryAndConduitCensus(t *testing.T) {
	for _, phase := range []string{"valid", "partial", "foreign", "footprint", "duplicate-cell", "wrong-definition", "unknown-field"} {
		t.Run(phase, func(t *testing.T) {
			cell := &c.Cell{X: proto.Int32(2), Z: proto.Int32(2)}
			row := &o.DevelopmentFurniture{Building: &o.EntityRef{Id: proto.String("conduit"), DefName: proto.String("PowerConduit"), MapId: proto.Int32(0), Position: cell}}
			b := &o.BuildingState{Building: &o.EntityRef{Id: proto.String("lamp"), DefName: proto.String("StandingLamp"), MapId: proto.Int32(0), Position: cell}, OccupiedCells: []*c.Cell{cell}, Service: &o.BuildingServiceState{}, Settings: &o.BuildingSettings{}}
			v := &o.DevelopmentFacts{Power: []*o.DevelopmentPower{{Building: b}}, Furniture: []*o.DevelopmentFurniture{row}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(2), Returned: proto.Uint64(2), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}
			switch phase {
			case "partial":
				v.Completeness.Matched = proto.Uint64(1)
			case "foreign":
				row.Building.MapId = proto.Int32(1)
			case "footprint":
				b.OccupiedCells = []*c.Cell{{X: proto.Int32(3), Z: proto.Int32(2)}}
			case "duplicate-cell":
				v.Furniture = append(v.Furniture, proto.Clone(row).(*o.DevelopmentFurniture))
				v.Furniture[1].Building.Id = proto.String("other")
				v.Completeness.Matched = proto.Uint64(3)
				v.Completeness.Returned = proto.Uint64(3)
			case "wrong-definition":
				row.Building.DefName = proto.String("Bed")
			case "unknown-field":
				row.Indoors = proto.Bool(false)
			}
			err := validateColonyPower(v, &c.Identity{MapId: proto.Int32(0)}, &o.MapSize{Width: proto.Uint32(10), Height: proto.Uint32(10)})
			if (err == nil) != (phase == "valid") {
				t.Fatal(phase, err)
			}
		})
	}
}

func TestColonyEnvironmentRejectsUnavailablePopulatedAndDuplicateConditions(t *testing.T) {
	row := &o.EnvironmentCondition{Id: proto.String("flare"), DefName: proto.String("SolarFlare")}
	v := &o.ColonyFactsSnapshot{Environment: []*o.EnvironmentCondition{row}}
	if err := validateColonyEnvironment(v); err != nil {
		t.Fatal(err)
	}
	v.Environment = append(v.Environment, proto.Clone(row).(*o.EnvironmentCondition))
	if validateColonyEnvironment(v) == nil {
		t.Fatal("duplicate condition accepted")
	}
	v.Environment = v.Environment[:1]
	v.Issues = []*o.ReadIssue{{Field: proto.String("environment"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}}
	if validateColonyEnvironment(v) == nil {
		t.Fatal("unavailable populated environment accepted")
	}
}
