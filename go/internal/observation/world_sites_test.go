package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

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
