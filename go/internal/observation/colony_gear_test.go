package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestColonyGearExactRoutineCensus(t *testing.T) {
	gear := domain.Known(policy.GearObservation{Pawns: []policy.GearPawn{{Pawn: "pawn", Loadout: "native-loadout"}}})
	emergency := policy.EmergencyFacts{ColonistsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "pawn"}}}
	if _, known := routineGear(gear, emergency).Value(); !known {
		t.Fatal("matching census lost")
	}
	emergency.Colonists[0].ID = "other"
	if _, known := routineGear(gear, emergency).Value(); known {
		t.Fatal("same count concealed missing pawn")
	}
}

// The held share lands on each modelled pawn's input without mutating the
// census it was copied from; a pawn with no held share is gated (#1842).
func TestStampGearSharesGatesEachModelledPawn(t *testing.T) {
	census := policy.GearObservation{Pawns: []policy.GearPawn{
		{Pawn: "a", LoadoutModel: domain.Known(policy.GearLoadoutInput{})},
		{Pawn: "b", LoadoutModel: domain.Known(policy.GearLoadoutInput{})},
		{Pawn: "c"},
	}}
	p := ColonyProjection{Facts: policy.RoutineFacts{PersonalShares: policy.PersonalShares(domain.Known(1000.0), []policy.ShareMember{{Profile: policy.PawnProfile{ID: "a"}, Free: true, Spent: domain.Known(0.0)}})}}
	p.Facts.Gear = domain.Known(census)
	stampGearShares(&p)
	gear, _ := p.Facts.Gear.Value()
	a, _ := gear.Pawns[0].LoadoutModel.Value()
	b, _ := gear.Pawns[1].LoadoutModel.Value()
	if !a.Share.Allows(200) || a.Share.Allows(201) || b.Share.Allows(1) || !b.Share.Allows(0) {
		t.Fatal("shares not stamped", a.Share, b.Share)
	}
	if _, ok := gear.Pawns[2].LoadoutModel.Value(); ok {
		t.Fatal("an unmodelled pawn gained a model")
	}
	if m, _ := census.Pawns[0].LoadoutModel.Value(); !m.Share.Allows(1e9) {
		t.Fatal("the source census was mutated")
	}
}

// A frame that cannot ground the census (a partial roster, no gear, no
// outdoor temperature, unknown finished research) leaves it unknown.
func TestColonyGearUnknownWithoutFrameInputs(t *testing.T) {
	row := &o.GearLoadout{Pawn: &c.Ref{Id: proto.String("pawn")}, Snapshot: &o.SnapshotRef{Token: proto.String("native-loadout")}}
	v := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(1), OutdoorTemperatureC: proto.Float64(10), Planning: &o.PlanningSection{Outcome: &o.PlanningSection_Observed{Observed: &o.PlanningFacts{Gear: &o.GearSnapshot{Pawns: []*o.GearLoadout{row}}}}}}
	defs := GearDefinitions{Catalog: &bridge.DefinitionCatalog{}, Finished: domain.Known(map[string]bool{})}
	if _, known := GearFacts(v, bridge.Tables{}, defs).Value(); !known {
		t.Fatal("complete frame unknown")
	}
	if _, known := GearFacts(v, bridge.Tables{}, GearDefinitions{Catalog: defs.Catalog}).Value(); known {
		t.Fatal("census known without finished research")
	}
	v.OutdoorTemperatureC = nil
	if _, known := GearFacts(v, bridge.Tables{}, defs).Value(); known {
		t.Fatal("census known without the outdoor temperature")
	}
	v.OutdoorTemperatureC = proto.Float64(10)
	v.ColonistCount = proto.Uint32(2)
	if _, known := GearFacts(v, bridge.Tables{}, defs).Value(); known {
		t.Fatal("partial gear census accepted")
	}
	v.GetPlanning().GetObserved().Gear = nil
	if _, known := GearFacts(v, bridge.Tables{}, defs).Value(); known {
		t.Fatal("unavailable gear treated as empty")
	}
}
