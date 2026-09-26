package snapshot

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Planner picks converted from the native upkeep/* and clearance/* cases
// (#746): each planner step's recorded policy inputs, the reads the
// routine review's facts do not carry (feed benches and zones, haulers,
// dump sites, shrine squads and breach readiness), replayed through the
// policy that chose the bench, cell, pawn or casket.

func planner(t *testing.T, path string) Planner {
	t.Helper()
	p, err := LoadPlanner(path)
	if err != nil {
		t.Fatal(err)
	}
	return p
}

func animalFeed(t *testing.T, path string) policy.AnimalFeedMethod {
	t.Helper()
	p := planner(t, path)
	if len(p.AnimalFeed) != 1 {
		t.Fatalf("%s: %d feed selections recorded", path, len(p.AnimalFeed))
	}
	c := p.AnimalFeed[0]
	m, err := policy.SelectAnimalFeedMethod(c.Targets, c.Stocks, c.Have, c.Stopped)
	if err != nil {
		t.Fatal(err)
	}
	if m.Reason != policy.AnimalFeedSelected || m.Resource != "Kibble" {
		t.Fatalf("%s: %+v, want kibble selected", path, m)
	}
	return m
}

// upkeep/feed, tick 15: the kibble bill lands on the one bench inside the
// confined pet's area.
func TestPickFeedBillOnBenchInsidePetArea(t *testing.T) {
	m := animalFeed(t, "testdata/planner-feed-confined.json")
	if !reflect.DeepEqual(m.Benches, []string{"Thing_ButcherSpot44691"}) {
		t.Errorf("benches %v, want the in-area butcher spot", m.Benches)
	}
}

// upkeep/feed-delivered, ticks 15 and 18654: with no bench inside the
// pet's area the planner first zones feed storage on the area's free
// footprint, then, once that zone accepts kibble, bills any bench and
// counts on hauling to deliver.
func TestPickFeedZoneThenDelivery(t *testing.T) {
	m := animalFeed(t, "testdata/planner-feed-delivered-unzoned.json")
	if len(m.Benches) != 0 || m.Delivered || len(m.StorageCells) == 0 {
		t.Errorf("unzoned %+v, want feed storage cells and no bench", m)
	}
	m = animalFeed(t, "testdata/planner-feed-delivered-zoned.json")
	if len(m.Benches) != 0 || !m.Delivered {
		t.Errorf("zoned %+v, want delivery", m)
	}
}

// clearance/chunk-dump, tick 15: the dump allows the pending chunk kinds
// no existing store takes, plus slag (#702), on four native dump sites
// clear of every reserved footprint.
func TestPickChunkDumpForUnstoredKinds(t *testing.T) {
	p := planner(t, "testdata/planner-chunk-dump.json")
	if len(p.ChunkDumps) != 1 {
		t.Fatalf("%d dump selections", len(p.ChunkDumps))
	}
	c := p.ChunkDumps[0]
	cells, allow, ok := policy.SelectChunkDump(c.Chunks, c.DumpSites, c.Protected)
	if !ok {
		t.Fatal("no dump selected")
	}
	// Only the stray slate chunk is pending: the fixture chunks already
	// have a store and are left to hauling. Slag is always allowed.
	if pending := policy.PendingChunks(c.Chunks); len(pending) != 1 || pending[0].EntityID != "Thing_ChunkSlate43408" {
		t.Errorf("pending %v, want the slate chunk", pending)
	}
	if !reflect.DeepEqual(allow, []string{"ChunkSlagSteel", "ChunkSlate"}) {
		t.Errorf("dump allows %v", allow)
	}
	if len(cells) != 4 {
		t.Errorf("dump cells %v", cells)
	}
	for _, cell := range cells {
		if slices.Contains(c.Protected, cell) || !slices.Contains(c.DumpSites, cell) {
			t.Errorf("dump cell %v is reserved or not a native dump site", cell)
		}
	}
}

// homeShrineCaskets replays the routine review's open targets for its one
// Home shrine.
func homeShrineCaskets(t *testing.T, path string) (policy.AncientShrine, []policy.ShrineCasket) {
	t.Helper()
	row, p := homeShrine(t, path)
	caskets := policy.ShrineOpenTargets([]policy.AncientShrine{row}, p)[row.ID]
	if len(caskets) != 2 {
		t.Fatalf("%s: open targets %v, want two", path, caskets)
	}
	return row, caskets
}

// clearance/shrine-open, tick 15: the melee lock stands a squad colonist
// at each filled casket and the lowest casket's locker opens it.
func TestPickShrineMeleeLock(t *testing.T) {
	_, caskets := homeShrineCaskets(t, "testdata/planner-shrine-open-routine.json.gz")
	squad := planner(t, "testdata/planner-shrine-open.json").ShrineSquads
	if len(squad) != 1 {
		t.Fatalf("%d squad reads", len(squad))
	}
	lock := policy.ShrineMeleeLock(caskets, squad[0])
	want := map[string]domain.PawnID{"Thing_AncientCryptosleepCasket44710": "Thing_Human724", "Thing_AncientCryptosleepCasket44718": "Thing_Human726"}
	if lock.Reason != "" || !reflect.DeepEqual(lock.Lockers, want) || lock.Opener != "Thing_Human724" || lock.Casket != "Thing_AncientCryptosleepCasket44710" {
		t.Errorf("lock %+v", lock)
	}
}

// clearance/shrine-heat, ticks 15, 16592 and 88414: with no melee
// colonist free for the lock, the heat fallback builds the room's one
// door, then its three heaters, and once the room is hot sends the
// rifleman to the doorway to shoot the lowest casket and retreat.
func TestPickShrineHeatDoorHeatersShooter(t *testing.T) {
	step := func(tick string) policy.ShrineHeatProposal {
		t.Helper()
		row, caskets := homeShrineCaskets(t, "testdata/planner-shrine-heat-"+tick+"-routine.json.gz")
		squad := planner(t, "testdata/planner-shrine-heat-"+tick+".json").ShrineSquads[0]
		if lock := policy.ShrineMeleeLock(caskets, squad); lock.Reason != policy.CasketHoldLockUnderstaffed {
			t.Errorf("tick %s lock %+v, want understaffed", tick, lock)
		}
		return policy.SelectShrineHeat(row, caskets, squad)
	}
	if got := step("15"); got.Phase != "heat_door" || got.Cell != (domain.Cell{X: 139, Z: 53}) {
		t.Errorf("tick 15 %+v, want the door at 139,53", got)
	}
	if got := step("16592"); got.Phase != "heat_heater" || got.Heaters != 3 || !reflect.DeepEqual(got.Cells, []domain.Cell{{X: 137, Z: 54}, {X: 137, Z: 55}, {X: 137, Z: 56}}) {
		t.Errorf("tick 16592 %+v, want three heaters", got)
	}
	got := step("88414")
	if got.Phase != "heat_open" || got.Pawn != "Thing_Human724" || got.Casket.EntityID != "Thing_AncientCryptosleepCasket44710" || got.Cell != (domain.Cell{X: 139, Z: 53}) || got.Retreat != (domain.Cell{X: 139, Z: 52}) {
		t.Errorf("tick 88414 %+v, want Human724 shooting 44710 from the doorway", got)
	}
}

// clearance/shrine-claim, tick 15: the sealed shrine is ready to breach
// through Thing_Wall44693 with the full eight-colonist squad, the three
// colony traps accounted for.
func TestPickShrineBreachReadiness(t *testing.T) {
	p := planner(t, "testdata/planner-shrine-breach-ready.json")
	if len(p.ShrineReadiness) != 1 {
		t.Fatalf("%d readiness requests", len(p.ShrineReadiness))
	}
	r := policy.ShrineBreachReadiness(p.ShrineReadiness[0])
	if !r.Ready || r.Wall.EntityID != "Thing_Wall44693" || len(r.Squad) != 8 || r.Traps != 3 {
		t.Errorf("readiness %+v", r)
	}
}

// upkeep/scattered, tick 15: the exposed medicine goes to Thing_Human724,
// the first of the eight colonists eligible to haul.
func TestPickSecureSuppliesHauler(t *testing.T) {
	p := planner(t, "testdata/planner-secure-supplies-hauler.json")
	if len(p.SecureSupplies) != 1 || len(p.SecureSupplies[0].Pawns) != 8 {
		t.Fatalf("recorded %+v", p.SecureSupplies)
	}
	c := p.SecureSupplies[0]
	item, pawn, ok := policy.SelectSecureSupplies(c.Items, c.Pawns)
	if !ok || item.ID != "Thing_MedicineHerbal44693" || pawn != "Thing_Human724" {
		t.Errorf("picked %s for %s (%v)", pawn, item.ID, ok)
	}
}

// upkeep/storage-missing, tick 34692: with no storage accepting the
// medicine, the covered storage fallback sites one 2x2 stockpile at
// 102,137.
func TestPickCoveredStorageFallbackSite(t *testing.T) {
	p := planner(t, "testdata/planner-secure-supplies-covered-storage.json.gz")
	if len(p.CoveredStorage) != 1 {
		t.Fatalf("%d covered storage searches", len(p.CoveredStorage))
	}
	sites, err := policy.CoveredStorageSites(p.CoveredStorage[0])
	if err != nil {
		t.Fatal(err)
	}
	if want := []policy.Rectangle{{X: 102, Z: 137, Width: 2, Height: 2}}; !reflect.DeepEqual(sites, want) {
		t.Errorf("sites %v, want %v", sites, want)
	}
}
