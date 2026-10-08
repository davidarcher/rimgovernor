package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestWorldSiteExtensionsProjectionRetainsInventoryAndUnknownSecurity(t *testing.T) {
	s := &o.WorldSite{Id: proto.String("site"), Security: &o.QuestSiteSecurity{Known: proto.Bool(false)}, PeaceTalks: &o.QuestPeaceTalks{WorstGoodwillLoss: proto.Int32(126), BestGoodwillGain: proto.Int32(50)}, Extraction: &o.QuestSiteExtraction{CanReform: proto.Bool(false), CrewIds: []string{"pawn"}, CrewNutritionPerDay: proto.Float64(1.6), Cargo: []*o.QuestSiteCargo{{Id: proto.String("meal"), Def: proto.String("MealSimple"), Count: proto.Int64(4), Held: proto.Bool(true)}}}}
	rows, _ := frameWorldSites(&bridge.WorldProgressionRead{Sites: []*o.WorldSite{s}}).Value()
	security, _ := rows[0].Security.Value()
	if known, present := security.Known.Value(); !present || known {
		t.Fatal("unknown envelope became safe")
	}
	if _, present := security.PendingRaidPoints.Value(); present {
		t.Fatal("missing raid estimate became zero")
	}
	ex, _ := rows[0].Extraction.Value()
	held, known := ex.Cargo[0].Held.Value()
	if !known || !held || len(ex.Crew) != 1 {
		t.Fatal(ex)
	}
	risk, _ := rows[0].PeaceTalks.Value()
	loss, _ := risk.WorstGoodwillLoss.Value()
	if loss != 126 {
		t.Fatal(risk)
	}
	survey, _ := questSurveyScanner(&o.QuestSurveyScanner{SiteId: proto.String("site"), DurationTicks: proto.Int64(900000)}).Value()
	if _, known := survey.Alive.Value(); known {
		t.Fatal("pending scanner became alive")
	}
}

func TestWorldSiteProjectionKeepsUnknownThreatAndTravel(t *testing.T) {
	read := &bridge.WorldProgressionRead{Sites: []*o.WorldSite{{Id: proto.String("site"), DefName: proto.String("Site"), State: o.WorldSiteState_WORLD_SITE_STATE_PENDING.Enum(), Tile: proto.Int32(0), LayerId: proto.Int32(0), QuestIds: []string{"quest"}}}}
	rows, known := frameWorldSites(read).Value()
	if !known || len(rows) != 1 {
		t.Fatal(rows, known)
	}
	row := rows[0]
	if _, known := row.Threat.Value(); known {
		t.Fatal("unknown threat became safe")
	}
	if _, known := row.TravelTicks.Value(); known {
		t.Fatal("distance became travel time")
	}
	if tile, known := row.Tile.Value(); !known || tile != 0 {
		t.Fatal(row)
	}
	if _, known := frameWorldSites(nil).Value(); known {
		t.Fatal("missing census became empty sites")
	}
}

func TestGravEngineProjectionPreservesInspectionPawn(t *testing.T) {
	engine := &o.QuestGravEngine{EngineId: proto.String("engine"), Spawned: proto.Bool(true), MapId: proto.Int32(0), Inspected: proto.Bool(false), EligiblePawnIds: []string{"colonist"}, InspectingPawnIds: []string{"inspector"}}
	row, known := questGravEngine(engine).Value()
	if !known || row.ID != "engine" || len(row.InspectingPawnIDs) != 1 || row.InspectingPawnIDs[0] != "inspector" {
		t.Fatal(row, known)
	}
	if done, known := row.Inspected.Value(); !known || done {
		t.Fatal(row)
	}
	if _, known := questGravEngine(nil).Value(); known {
		t.Fatal("missing target became executable engine")
	}
}
