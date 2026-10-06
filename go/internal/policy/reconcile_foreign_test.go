package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func foreignCell(c domain.Cell, things ...Thing) SiteCell {
	return SiteCell{Cell: c, Things: things}
}

func ruinThing(id uint64, def string, flags ThingFlags) Thing {
	return Thing{ID: id, Def: def, Category: ThingBuilding, Faction: FactionNone, Flags: FlagEdifice | FlagDeconstructible | flags, Building: &BuildingState{}}
}

func hasHold(rec Reconciliation, reason string) bool {
	for _, h := range rec.Holds {
		if h.Reason == reason {
			return true
		}
	}
	return false
}

func TestReconcileForeignRuinOnWallCellIsClaimed(t *testing.T) {
	in, _ := reconFixture()
	wall := domain.Cell{X: 9, Z: 10}
	in.Ground.walls[wall] = false
	in.Cells = []SiteCell{foreignCell(wall, ruinThing(7, ShellWallDefinition, FlagClaimable))}
	rec := Reconcile(in)
	op := readyOp(t, rec, OpClaim)
	if !kindsEqual(readyKinds(rec), OpClaim) || len(op.Targets) != 1 || op.Targets[0].EntityID != "Thing_"+ShellWallDefinition+"7" || op.Cells[0] != wall {
		t.Fatalf("claim, no wall_in: %+v", rec.Ready)
	}
	// A ruin of another wall kind is deconstructed and its wall raised after.
	in.Cells = []SiteCell{foreignCell(wall, ruinThing(7, "Wall_Other", FlagClaimable))}
	rec = Reconcile(in)
	if k := readyKinds(rec); !kindsEqual(k, OpFurnitureOut) {
		t.Fatalf("other kind deconstructs: %v", k)
	}
	if len(readyOp(t, rec, OpWallIn).Cells) != 0 || len(rec.Owed) != 2 {
		t.Fatalf("wall_in waits behind the removal: %+v", rec.Owed)
	}
}

func TestReconcileForeignRuinOnInteriorCellIsDeconstructed(t *testing.T) {
	in, _ := reconFixture()
	in.Furniture = []WantedPiece{{DefName: "Bed", Minimum: domain.Cell{X: 11, Z: 11}, Maximum: domain.Cell{X: 11, Z: 11}}}
	in.Cells = []SiteCell{foreignCell(domain.Cell{X: 11, Z: 11}, ruinThing(8, "AncientBarrier", FlagClaimable))}
	rec := Reconcile(in)
	if k := readyKinds(rec); !kindsEqual(k, OpFurnitureOut) {
		t.Fatalf("interior ruin is never claimed: %v", k)
	}
	if len(readyOp(t, rec, OpBuild).Pieces) != 0 || len(rec.Owed) != 2 || rec.Owed[1].Kind != OpBuild {
		t.Fatalf("build waits for the removal: %+v", rec.Owed)
	}
}

func TestReconcileForeignMinifiablePacks(t *testing.T) {
	in, _ := reconFixture()
	chair := Thing{ID: 9, Def: "Armchair", Category: ThingBuilding, Flags: FlagDeconstructible | FlagMinifiable, Building: &BuildingState{}}
	in.Cells = []SiteCell{foreignCell(domain.Cell{X: 10, Z: 10}, chair), foreignCell(domain.Cell{X: 10, Z: 11}, chair)}
	in.Furniture = []WantedPiece{{DefName: "Armchair", Minimum: domain.Cell{X: 12, Z: 12}, Maximum: domain.Cell{X: 12, Z: 12}}}
	rec := Reconcile(in)
	op := readyOp(t, rec, OpPack)
	if len(op.Targets) != 1 || op.Targets[0].Maximum.Z != 11 || !op.Targets[0].Packable {
		t.Fatalf("pack once for both cells: %+v", rec.Ready)
	}
	// What a foreign thing packs is not stock this pass: the piece builds, not installs.
	if len(readyOp(t, rec, OpBuild).Pieces) != 1 || len(readyOp(t, rec, OpInstall).Pieces) != 0 {
		t.Fatalf("ready = %+v", rec.Ready)
	}
}

func TestReconcileForeignImpassablePlantCutPassableLeft(t *testing.T) {
	in, _ := reconFixture()
	tree := Thing{ID: 3, Def: "Plant_Tree", Category: ThingPlant, Flags: FlagImpassable}
	bush := Thing{ID: 4, Def: "Plant_Bush", Category: ThingPlant}
	in.Cells = []SiteCell{foreignCell(domain.Cell{X: 10, Z: 10}, tree), foreignCell(domain.Cell{X: 11, Z: 10}, bush)}
	in.WantedFloor = func(domain.Cell) string { return "StoneTile" }
	rec := Reconcile(in)
	op := readyOp(t, rec, OpCut)
	if len(op.Cells) != 1 || op.Cells[0] != (domain.Cell{X: 10, Z: 10}) {
		t.Fatalf("cut the tree only: %+v", rec.Ready)
	}
	if fl := readyOp(t, rec, OpFloorIn); len(fl.Floors) != 8 {
		t.Fatalf("the cut cell's floor waits: %+v", fl)
	}
}

func TestReconcileForeignHaulableItemMoves(t *testing.T) {
	in, _ := reconFixture()
	steel := Thing{ID: 5, Def: "Steel", Category: ThingItem, Flags: FlagHaulable, Count: 40}
	kept := Thing{ID: 6, Def: "Silver", Category: ThingItem, Flags: FlagHaulable | FlagForbidden}
	in.Cells = []SiteCell{foreignCell(domain.Cell{X: 10, Z: 10}, steel, kept)}
	rec := Reconcile(in)
	if op := readyOp(t, rec, OpHaulOut); len(op.Targets) != 1 || op.Targets[0].EntityID != "Thing_Steel5" {
		t.Fatalf("haul the unforbidden stack: %+v", rec.Ready)
	}
}

func TestReconcileForeignHoldsNameTheirReason(t *testing.T) {
	in, _ := reconFixture()
	danger := ruinThing(1, "AncientTurret", FlagClaimable|FlagAncientDanger)
	casket := ruinThing(2, "AncientCasket", FlagClaimable)
	casket.Building.Casket = []string{"Gold"}
	stuck := Thing{ID: 3, Def: "Statue", Category: ThingBuilding, Building: &BuildingState{}}
	rock := Thing{ID: 4, Def: "Granite", Category: ThingBuilding, Flags: FlagEdifice, Building: &BuildingState{}}
	in.Ground.walls[domain.Cell{X: 9, Z: 10}] = false
	in.Cells = []SiteCell{
		foreignCell(domain.Cell{X: 9, Z: 10}, danger),
		foreignCell(domain.Cell{X: 10, Z: 10}, casket),
		foreignCell(domain.Cell{X: 11, Z: 10}, stuck),
		foreignCell(domain.Cell{X: 12, Z: 10}, rock),
	}
	rec := Reconcile(in)
	if len(rec.Holds) != 3 || !hasHold(rec, "ancient_danger") || !hasHold(rec, "casket") || !hasHold(rec, "not_deconstructible") {
		t.Fatalf("holds = %+v", rec.Holds)
	}
	if len(rec.Ready) != 0 {
		t.Fatalf("a held cell is not worked: %+v", rec.Ready)
	}
	// The held ring cell raises no wall until the hold clears.
	if len(readyOp(t, rec, OpWallIn).Cells) != 0 {
		t.Fatalf("wall_in over a hold: %+v", rec.Ready)
	}
}

func TestReconcileForeignRemovalPrecedesWallInAndBuild(t *testing.T) {
	in, _ := reconFixture()
	in.Ground = GroundCensus{}
	in.Cells = []SiteCell{foreignCell(domain.Cell{X: 9, Z: 9}, ruinThing(1, "Junk", 0))}
	rec := Reconcile(in)
	if wi := readyOp(t, rec, OpWallIn); len(wi.Cells) != 14 {
		t.Fatalf("the other ring cells still raise: %d", len(wi.Cells))
	}
	if len(readyOp(t, rec, OpFurnitureOut).Targets) != 1 {
		t.Fatalf("removal ready: %+v", rec.Ready)
	}
}

// The id form is vanilla GetUniqueLoadID: the def sits between the prefix and
// the number, so RefIndex.Thing resolves a foreign claim or cut target (#2293).
func TestThingLoadIDIsVanillaForm(t *testing.T) {
	if got := (Thing{ID: 44693, Def: "Husky"}).LoadID(); got != "Thing_Husky44693" {
		t.Fatalf("LoadID = %q", got)
	}
}
