package bridge

import (
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestColonyPowerRejectsPartialOrAmbiguousCensus(t *testing.T) {
	for _, phase := range []string{"valid", "duplicate"} {
		t.Run(phase, func(t *testing.T) {
			row := &o.DevelopmentPower{Building: &c.Ref{Id: proto.String("building")}}
			v := &o.DevelopmentFacts{Power: []*o.DevelopmentPower{row}}
			switch phase {
			case "duplicate":
				v.Power = append(v.Power, proto.Clone(row).(*o.DevelopmentPower))
			}
			err := validateColonyPower(v, &c.Identity{MapId: proto.Int32(0)}, &o.MapSize{Width: proto.Uint32(10), Height: proto.Uint32(10)})
			if (err == nil) != (phase == "valid") {
				t.Fatal(phase, err)
			}
		})
	}
}

func TestColonyPowerGeometryAndConduitCensus(t *testing.T) {
	for _, phase := range []string{"valid", "duplicate", "unknown-field"} {
		t.Run(phase, func(t *testing.T) {
			row := &o.DevelopmentFurniture{Building: &c.Ref{Id: proto.String("conduit")}}
			v := &o.DevelopmentFacts{Power: []*o.DevelopmentPower{{Building: NewRef("lamp")}}, Furniture: []*o.DevelopmentFurniture{row}}
			switch phase {
			case "duplicate":
				v.Furniture = append(v.Furniture, proto.Clone(row).(*o.DevelopmentFurniture))
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
	// An active cold snap arrives with every field native fills.
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
			generator := &o.DevelopmentPower{Building: &c.Ref{Id: proto.String("generator")}}
			battery := &o.DevelopmentPower{StoredWattDays: proto.Float64(300), CapacityWattDays: proto.Float64(600), Building: &c.Ref{Id: proto.String("battery")}}
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
