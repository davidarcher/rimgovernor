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

func TestColonyGearPreservesMissingDeficitAndExactRoutineCensus(t *testing.T) {
	row := &o.GearLoadout{Pawn: &c.Ref{Id: proto.String("pawn")}, Snapshot: &o.SnapshotRef{Token: proto.String("native-loadout")}, Deficit: proto.Bool(false)}
	v := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(1), Planning: &o.PlanningSection{Outcome: &o.PlanningSection_Observed{Observed: &o.PlanningFacts{Gear: &o.GearSnapshot{Pawns: []*o.GearLoadout{row}}}}}}
	gear := colonyGear(v, bridge.Tables{}, GearDefinitions{Catalog: &bridge.DefinitionCatalog{}})
	review, err := policy.ReviewGear(gear)
	if err != nil || review.Recovered != domain.Known(true) {
		t.Fatal(review, err)
	}
	emergency := policy.EmergencyFacts{ColonistsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "pawn"}}}
	if _, known := routineGear(gear, emergency).Value(); !known {
		t.Fatal("matching census lost")
	}
	emergency.Colonists[0].ID = "other"
	if _, known := routineGear(gear, emergency).Value(); known {
		t.Fatal("same count concealed missing pawn")
	}
	row.Deficit = nil
	review, err = policy.ReviewGear(colonyGear(v, bridge.Tables{}, GearDefinitions{Catalog: &bridge.DefinitionCatalog{}}))
	if _, known := review.Recovered.Value(); err != nil || known {
		t.Fatal("missing deficit became false", review, err)
	}
	row.Deficit = proto.Bool(true)
	row.Blocker = proto.String("player job")
	review, err = policy.ReviewGear(colonyGear(v, bridge.Tables{}, GearDefinitions{Catalog: &bridge.DefinitionCatalog{}}))
	if err != nil || review.Recovered != domain.Known(false) {
		t.Fatal("blocked pawn lost need", review, err)
	}
	v.ColonistCount = proto.Uint32(2)
	if _, known := colonyGear(v, bridge.Tables{}, GearDefinitions{Catalog: &bridge.DefinitionCatalog{}}).Value(); known {
		t.Fatal("partial gear census accepted")
	}
	v.GetPlanning().GetObserved().Gear = nil
	if _, known := colonyGear(v, bridge.Tables{}, GearDefinitions{Catalog: &bridge.DefinitionCatalog{}}).Value(); known {
		t.Fatal("unavailable gear treated as empty")
	}
}
