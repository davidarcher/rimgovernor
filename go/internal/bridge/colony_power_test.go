package bridge

import (
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestColonyPowerRejectsPartialOrAmbiguousCensus(t *testing.T) {
	for _, phase := range []string{"valid", "unknown", "duplicate", "foreign-map", "infinite", "extra-detail"} {
		t.Run(phase, func(t *testing.T) {
			row := &o.DevelopmentPower{BaseW: proto.Float64(-200), Building: &o.EntityRef{Id: proto.String("building"), MapId: proto.Int32(0)}}
			v := &o.DevelopmentFacts{Power: []*o.DevelopmentPower{row}}
			switch phase {
			case "unknown":
				row.BaseW = nil
			case "duplicate":
				v.Power = append(v.Power, proto.Clone(row).(*o.DevelopmentPower))
			case "foreign-map":
				row.Building.MapId = proto.Int32(1)
			case "infinite":
				row.BaseW = proto.Float64(math.Inf(1))
			case "extra-detail":
				row.Building.Label = proto.String("lamp")
			}
			err := validateColonyPower(v, &c.Identity{MapId: proto.Int32(0)}, &o.MapSize{Width: proto.Uint32(10), Height: proto.Uint32(10)})
			if (err == nil) != (phase == "valid" || phase == "unknown") {
				t.Fatal(phase, err)
			}
		})
	}
}

func TestColonyPowerGeometryAndConduitCensus(t *testing.T) {
	for _, phase := range []string{"valid", "hidden", "waterproof", "foreign", "duplicate-cell", "wrong-definition", "unknown-field"} {
		t.Run(phase, func(t *testing.T) {
			cell := &c.Cell{X: proto.Int32(2), Z: proto.Int32(2)}
			row := &o.DevelopmentFurniture{Building: &o.EntityRef{Id: proto.String("conduit"), DefName: proto.String("PowerConduit"), MapId: proto.Int32(0), Position: cell}}
			b := &o.EntityRef{Id: proto.String("lamp"), DefName: proto.String("StandingLamp"), MapId: proto.Int32(0), Position: cell}
			v := &o.DevelopmentFacts{Power: []*o.DevelopmentPower{{Building: b}}, Furniture: []*o.DevelopmentFurniture{row}}
			switch phase {
			case "hidden":
				row.Building.DefName = proto.String("HiddenConduit")
			case "waterproof":
				row.Building.DefName = proto.String("WaterproofConduit")
			case "foreign":
				row.Building.MapId = proto.Int32(1)
			case "duplicate-cell":
				v.Furniture = append(v.Furniture, proto.Clone(row).(*o.DevelopmentFurniture))
				v.Furniture[1].Building.Id = proto.String("other")
			case "wrong-definition":
				row.Building.DefName = proto.String("Bed")
			case "unknown-field":
				row.Indoors = proto.Bool(false)
			}
			err := validateColonyPower(v, &c.Identity{MapId: proto.Int32(0)}, &o.MapSize{Width: proto.Uint32(10), Height: proto.Uint32(10)})
			if (err == nil) != (phase == "valid" || phase == "hidden" || phase == "waterproof") {
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
	// An active cold snap arrives with every field native fills (#362).
	cold := &o.EnvironmentCondition{Id: proto.String("1"), DefName: proto.String("ColdSnap"), Implementation: proto.String("RimWorld.GameCondition_ColdSnap"), Label: proto.String("Cold snap"), Permanent: proto.Bool(false), TicksLeft: proto.Int64(240000)}
	v.Environment = append(v.Environment, cold)
	if err := validateColonyEnvironment(v); err != nil {
		t.Fatal("cold snap row rejected:", err)
	}
	cold.TicksLeft = proto.Int64(-1)
	if validateColonyEnvironment(v) == nil {
		t.Fatal("negative ticks_left accepted")
	}
	cold.TicksLeft, cold.Permanent = proto.Int64(10), proto.Bool(true)
	if validateColonyEnvironment(v) == nil {
		t.Fatal("permanent condition with ticks_left accepted")
	}
	cold.TicksLeft = nil
	if err := validateColonyEnvironment(v); err != nil {
		t.Fatal("permanent row rejected:", err)
	}
	v.Environment = v.Environment[:1]
	v.Issues = []*o.ReadIssue{{Field: proto.String("environment"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}}
	if validateColonyEnvironment(v) == nil {
		t.Fatal("unavailable populated environment accepted")
	}
}

// The census carries battery storage rows and per-network summaries
// beside its building references; the allowlist validator must admit
// exactly those and nothing else, or the live routine planner never
// observes the colony at all.
func TestColonyPowerAcceptsBatteryAndNetworkFacts(t *testing.T) {
	for _, phase := range []string{"valid", "stored-over-capacity", "duplicate-network", "network-unknown-field", "network-nan"} {
		t.Run(phase, func(t *testing.T) {
			generator := &o.DevelopmentPower{BaseW: proto.Float64(1000), Building: &o.EntityRef{Id: proto.String("generator"), MapId: proto.Int32(0)}}
			battery := &o.DevelopmentPower{BaseW: proto.Float64(0), StoredWattDays: proto.Float64(300), CapacityWattDays: proto.Float64(600), Building: &o.EntityRef{Id: proto.String("battery"), MapId: proto.Int32(0)}}
			net := &o.PowerNetwork{Id: proto.String("net"), Producers: proto.Uint32(1), Consumers: proto.Uint32(0), Batteries: proto.Uint32(1), Transmitters: proto.Uint32(3), Connectors: proto.Uint32(0), GenerationW: proto.Float64(0), ConsumptionW: proto.Float64(0), NetW: proto.Float64(0), StoredWattDays: proto.Float64(300), CapacityWattDays: proto.Float64(600), HasSource: proto.Bool(true), HasActiveSource: proto.Bool(false)}
			v := &o.DevelopmentFacts{Power: []*o.DevelopmentPower{generator, battery}, Networks: []*o.PowerNetwork{net}}
			switch phase {
			case "stored-over-capacity":
				battery.StoredWattDays = proto.Float64(601)
			case "duplicate-network":
				v.Networks = append(v.Networks, proto.Clone(net).(*o.PowerNetwork))
			case "network-unknown-field":
				net.MemberIds = []string{"generator"}
			case "network-nan":
				net.NetW = proto.Float64(math.NaN())
			}
			err := validateColonyPower(v, &c.Identity{MapId: proto.Int32(0)}, &o.MapSize{Width: proto.Uint32(10), Height: proto.Uint32(10)})
			if (err == nil) != (phase == "valid") {
				t.Fatal(phase, err)
			}
		})
	}
}
