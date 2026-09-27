package snapshot

import (
	"context"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestPlannerRecordsWhatTheStepNotedAndRoundTrips(t *testing.T) {
	dir := t.TempDir()
	t.Setenv(DirEnv, dir)
	ctx, finish := StartPlanner(context.Background(), policy.EnsureInitialShelter)
	shelter := policy.StarterRequest{Anchor: domain.Cell{X: 4, Z: 5}, Shelter: policy.ShelterRectangle, Grid: domain.Unknown[policy.ColonyGrid](),
		Cells: []policy.SiteCell{{Cell: domain.Cell{X: 1, Z: 2}, Walkable: domain.Known(true)}}}
	NoteShelter(ctx, shelter)
	target := policy.ExcavationTarget{Access: domain.Cell{X: 9, Z: 9}}
	NoteSite(ctx, "verify", target, []domain.Cell{{X: 1, Z: 1}}, bridge.ExcavationSite{AccessReachable: true, Cells: []bridge.ExcavationSiteCell{{Cell: domain.Cell{X: 1, Z: 1}, Eligible: true}}})
	NoteChoice(ctx, ExcavationChoice{Anchor: domain.Cell{X: 3, Z: 3}, Target: &target, Excavate: true})
	if err := finish(domain.GenerationSnapshot{Colony: "c"}, 120); err != nil {
		t.Fatal(err)
	}
	p, err := LoadPlanner(dir + "/planner-EnsureInitialShelter-120-1.json")
	if err != nil {
		t.Fatal(err)
	}
	if len(p.Shelter) != 1 || !reflect.DeepEqual(p.Shelter[0].Cells, shelter.Cells) || p.Shelter[0].Shelter != policy.ShelterRectangle {
		t.Fatalf("shelter %+v", p.Shelter)
	}
	if len(p.Sites) != 1 || p.Sites[0].Purpose != "verify" || !p.Sites[0].Site.AccessReachable || len(p.Choices) != 1 || !p.Choices[0].Excavate {
		t.Fatalf("sites %+v choices %+v", p.Sites, p.Choices)
	}
}

func TestPlannerRecordsProductionAndResearchInputs(t *testing.T) {
	dir := t.TempDir()
	t.Setenv(DirEnv, dir)
	ctx, finish := StartPlanner(context.Background(), policy.MaintainResource)
	bench := policy.GearBench{ID: "b1"}
	NoteResourceMethod(ctx, policy.ResourceMethodRequest{Resource: "StoneBlocks", Target: 75, Benches: domain.Known([]policy.GearBench{bench})})
	NoteWorkshop(ctx, policy.WorkshopRequest{Resource: "StoneBlocks", Power: domain.Known(false)})
	NoteGearMethod(ctx, policy.GearPlanningRequest{Benches: domain.Known([]policy.GearBench{bench})})
	NoteResearch(ctx, ResearchCall{Needs: []string{"Stonecutting"}, Read: bridge.ResearchRead{Finished: []string{"Smithing"},
		Projects: map[string]policy.ResearchProjectFacts{"Stonecutting": {Name: "Stonecutting"}}}})
	if err := finish(domain.GenerationSnapshot{Colony: "c"}, 9); err != nil {
		t.Fatal(err)
	}
	p, err := LoadPlanner(dir + "/planner-MaintainResource-9-1.json")
	if err != nil {
		t.Fatal(err)
	}
	benches, _ := p.ResourceMethods[0].Benches.Value()
	if p.ResourceMethods[0].Target != 75 || len(benches) != 1 || p.Workshops[0].Resource != "StoneBlocks" || len(p.GearMethods) != 1 ||
		p.Research[0].Read.Projects["Stonecutting"].Name != "Stonecutting" || p.Research[0].Needs[0] != "Stonecutting" {
		t.Fatalf("planner %+v", p)
	}
}

func TestPlannerRecordsNothingWhenUnset(t *testing.T) {
	t.Setenv(DirEnv, "")
	ctx, finish := StartPlanner(context.Background(), policy.EnsureInitialShelter)
	NoteShelter(ctx, policy.StarterRequest{})
	if err := finish(domain.GenerationSnapshot{}, 1); err != nil {
		t.Fatal(err)
	}
}

func TestPlannerRecordsPlannerPickInputs(t *testing.T) {
	dir := t.TempDir()
	t.Setenv(DirEnv, dir)
	ctx, finish := StartPlanner(context.Background(), policy.ClearAncientShrine)
	NoteChunkDump(ctx, ChunkDumpCall{DumpSites: []domain.Cell{{X: 1, Z: 2}}})
	NoteAnimalFeed(ctx, AnimalFeedCall{Have: map[policy.Resource]int64{"Kibble": 3}})
	NoteSecureSupplies(ctx, SecureSuppliesCall{Pawns: []policy.SecureSuppliesHaulerFacts{{Pawn: "p1"}}})
	NoteShrineSquad(ctx, []policy.ShrineDefenderFacts{{ID: "p2"}})
	NoteShrineReadiness(ctx, policy.ShrineReadinessRequest{Center: domain.Cell{X: 7, Z: 8}})
	if err := finish(domain.GenerationSnapshot{Colony: "c"}, 9); err != nil {
		t.Fatal(err)
	}
	p, err := LoadPlanner(dir + "/planner-ClearAncientShrine-9-1.json")
	if err != nil {
		t.Fatal(err)
	}
	if len(p.ChunkDumps) != 1 || p.AnimalFeed[0].Have["Kibble"] != 3 || p.SecureSupplies[0].Pawns[0].Pawn != "p1" || p.ShrineSquads[0][0].ID != "p2" || p.ShrineReadiness[0].Center.X != 7 {
		t.Fatalf("planner %+v", p)
	}
}
