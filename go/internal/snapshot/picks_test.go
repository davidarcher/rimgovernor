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
	m, err := policy.SelectAnimalFeedMethod(c.Targets, c.Stocks, c.Have, kibbleRaces(c.Targets))
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

// clearance/chunk-dump, tick 15: the three fixture chunks no store takes
// get one dump allowing exactly their kinds (slag is always allowed,
// #702), on four native dump sites clear of every reserved footprint.
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
	if pending := policy.PendingChunks(c.Chunks); len(pending) != 3 {
		t.Errorf("pending %v, want the three fixture chunks", pending)
	}
	if !reflect.DeepEqual(allow, []string{"ChunkGranite", "ChunkLimestone", "ChunkSlagSteel"}) {
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

// clearance/salvage-hold-resume, tick 26: the review selects the remote
// battery ruin, holds it threat_present while a raider stands beside it
// (no salvage target, so nothing is designated), and selects it again once
// the raider is gone.
func TestPickSalvageHoldAndResume(t *testing.T) {
	const ruin = "Thing_Battery12682"
	for _, step := range []struct {
		file, selected, hold string
	}{
		{"salvage-hold-selected", ruin, ""},
		{"salvage-hold-threat", "", policy.RemoteHoldThreat},
		{"salvage-hold-resumed", ruin, ""},
	} {
		r, err := Load("testdata/" + step.file + ".json.gz")
		if err != nil {
			t.Fatal(err)
		}
		rows, known := r.Facts.Upkeep.Clearance.Value()
		if !known {
			t.Fatalf("%s: clearance census unknown", step.file)
		}
		holds, selected, err := policy.ReviewClearanceHolds(r.Policy, r.Facts, rows)
		if err != nil {
			t.Fatal(err)
		}
		hold := ""
		for _, h := range holds {
			if h.Target == ruin {
				hold = string(h.Reason)
			}
		}
		if selected != step.selected || hold != step.hold {
			t.Errorf("%s: selected %q hold %q, want %q %q", step.file, selected, hold, step.selected, step.hold)
		}
	}
}

// kibbleRaces is the catalog the recordings predate: every target race eats
// kibble, which a bench makes.
func kibbleRaces(targets []policy.AnimalFeedTarget) policy.AnimalRaceCatalog {
	races := map[policy.Resource]policy.AnimalRace{}
	for _, t := range targets {
		races[t.Definition] = policy.AnimalRace{Def: t.Definition, FeedItems: []policy.RaceFeedItem{{Def: "Kibble", Nutrition: 0.05}}}
	}
	return policy.AnimalRaceCatalog{Races: races}
}

// layout replan, hand-built (#1826): a plan carrying three worship rooms
// replans to one, the smallest, and nothing else leaves the plan.
func TestPickReplanRetiresDuplicateWorshipRooms(t *testing.T) {
	p := planner(t, "testdata/planner-replan-duplicate-worship.json.gz")
	if len(p.Replans) != 1 {
		t.Fatalf("%d replans", len(p.Replans))
	}
	call := p.Replans[0]
	worship := func(plan policy.LayoutPlan) (out []policy.LayoutRoom) {
		for _, r := range plan.AllRooms() {
			if r.Role == policy.ModuleWorship {
				out = append(out, r)
			}
		}
		return out
	}
	if got := len(worship(call.Plan)); got != 3 {
		t.Fatalf("recorded plan holds %d worship rooms, want 3", got)
	}
	next, changed := call.Run()
	got := worship(next)
	if !changed || len(got) != 1 || len(next.AllRooms()) != len(call.Plan.AllRooms())-2 {
		t.Fatalf("changed=%v worship=%d rooms %d -> %d", changed, len(got), len(call.Plan.AllRooms()), len(next.AllRooms()))
	}
	if want := (policy.Rectangle{X: 14, Z: 14, Width: 3, Height: 3}); got[0].Interior != want {
		t.Errorf("kept %+v, want the smallest %+v", got[0].Interior, want)
	}
}
