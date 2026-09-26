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
	shelter := policy.StarterRequest{Anchor: domain.Cell{X: 4, Z: 5}, Shelter: policy.ShelterHut, Grid: domain.Unknown[policy.ColonyGrid](),
		Cells: []policy.SiteCell{{Cell: domain.Cell{X: 1, Z: 2}, Walkable: domain.Known(true)}}}
	NoteShelter(ctx, shelter)
	target := policy.ExcavationTarget{Access: domain.Cell{X: 9, Z: 9}}
	NoteSite(ctx, "verify", target, []domain.Cell{{X: 1, Z: 1}}, bridge.ExcavationSite{AccessReachable: true, Cells: []bridge.ExcavationSiteCell{{Cell: domain.Cell{X: 1, Z: 1}, Eligible: true}}})
	NoteChoice(ctx, ExcavationChoice{Anchor: domain.Cell{X: 3, Z: 3}, Target: &target, Excavate: true})
	if err := finish(domain.GenerationSnapshot{Colony: "c"}, 120); err != nil {
		t.Fatal(err)
	}
	p, err := LoadPlanner(dir + "/planner-EnsureInitialShelter-120.json")
	if err != nil {
		t.Fatal(err)
	}
	if len(p.Shelter) != 1 || !reflect.DeepEqual(p.Shelter[0].Cells, shelter.Cells) || p.Shelter[0].Shelter != policy.ShelterHut {
		t.Fatalf("shelter %+v", p.Shelter)
	}
	if len(p.Sites) != 1 || p.Sites[0].Purpose != "verify" || !p.Sites[0].Site.AccessReachable || len(p.Choices) != 1 || !p.Choices[0].Excavate {
		t.Fatalf("sites %+v choices %+v", p.Sites, p.Choices)
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
