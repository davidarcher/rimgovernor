package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

type noFirebreakSource struct{}

func (noFirebreakSource) ReadDefenseSite(context.Context, *c.Identity, bridge.CellRect) (bridge.DefenseSite, bridge.Result, error) {
	return bridge.DefenseSite{}, bridge.Result{}, errors.New("unexpected defense site read")
}

// A second round whose first firebreak plan was cancelled (authority churn
// retires it) finds the owner's method still bound; it admits a fresh
// ordinal method instead of failing "method already bound" every round.
func TestFirebreakSecondRoundAfterChurnMintsFreshMethod(t *testing.T) {
	ctx := context.Background()
	reviewer, db, session, _, _ := roundsFixture(t)
	reviewer.methods = domain.Known([]policy.ConcernID{policy.MaintainFirebreak})
	review, err := reviewer.Step(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var id domain.ConcernID
	for _, b := range review.Review.Standards {
		if b.Concern == policy.MaintainFirebreak {
			id = b.Standard
		}
	}
	if id == "" {
		t.Fatal("MaintainFirebreak is not bound", review.Review.Standards)
	}
	state := session.State()
	goal, err := db.LoadStandard(ctx, id)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = db.ReviewStandard(ctx, id, goal.Revision, state.Snapshot, review.Review.Tick, domain.FindingUnmet); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsFirebreakPlanner(reviewer, noFirebreakSource{})
	if err != nil {
		t.Fatal(err)
	}
	planner.memory.world, planner.memory.tick = stockpileWorld(state.Snapshot), review.Review.Tick
	planner.memory.work = policy.FirebreakWork{Cut: []domain.Cell{{X: 3, Z: 1}}}
	call, epoch, done, err := reviewer.player.enter(ctx, "test", false)
	if err != nil {
		t.Fatal(err)
	}
	defer done()
	first, err := planner.step(call, epoch, nil)
	if err != nil || first.Verdict != BuildingReasonAdmitted {
		t.Fatal(first, err)
	}
	plan, err := db.LoadPlan(ctx, first.Plan)
	if err != nil {
		t.Fatal(err)
	}
	for _, action := range plan.Spec.Actions() {
		if _, err = db.Cancel(ctx, first.Plan, action.ID()); err != nil {
			t.Fatal(err)
		}
	}
	second, err := planner.step(call, epoch, nil)
	if err != nil || second.Verdict != BuildingReasonAdmitted || second.Plan == first.Plan {
		t.Fatal("second round", second, err)
	}
}

func TestFirebreakActionsCutAndRuins(t *testing.T) {
	ruins := map[domain.Cell]domain.CoverClearance{}
	work := policy.FirebreakWork{Cut: []domain.Cell{{X: 3, Z: 1}, {X: 1, Z: 1}}}
	for i := int32(0); i < maxDefenseCoverBatch+2; i++ {
		cell := domain.Cell{X: 20 + i, Z: 5}
		clearance, err := domain.NewCoverClearance(fmt.Sprintf("Wall%d", i), "Wall", domain.CoverClearanceDeconstruct, cell)
		if err != nil {
			t.Fatal(err)
		}
		ruins[cell] = clearance
		work.Deconstruct = append(work.Deconstruct, cell)
	}
	got, err := firebreakActions("plan-1", work, ruins)
	if err != nil || got.cut != 2 || got.ruins != maxDefenseCoverBatch || len(got.actions) != 1+maxDefenseCoverBatch {
		t.Fatal(got, err)
	}
	cut, ok := got.actions[0].AreaPlantCut()
	if !ok || len(cut.Cells()) != 2 {
		t.Fatal(got.actions[0])
	}
}

func TestFirebreakGround(t *testing.T) {
	for _, tt := range []struct {
		cell bridge.DefenseCell
		want policy.FirebreakGround
	}{
		{bridge.DefenseCell{Terrain: "Soil"}, policy.FirebreakOpen},
		{bridge.DefenseCell{Terrain: "WaterShallow"}, policy.FirebreakWater},
		{bridge.DefenseCell{NaturalRock: true, EdificeDefName: "Granite"}, policy.FirebreakNaturalRock},
		{bridge.DefenseCell{Fogged: true}, policy.FirebreakNaturalRock},
		{bridge.DefenseCell{EdificeDefName: "Wall", EdificeStuff: "WoodLog", PlayerOwned: true}, policy.FirebreakPlayerEdifice},
		{bridge.DefenseCell{EdificeDefName: "Wall", EdificeStuff: "WoodLog"}, policy.FirebreakWoodenRuin},
		{bridge.DefenseCell{EdificeDefName: "Wall", EdificeStuff: "BlocksGranite"}, policy.FirebreakStoneRuin},
	} {
		if got := firebreakGround(tt.cell); got != tt.want {
			t.Error(tt.cell, got)
		}
	}
}

// The firebreak reads standing plants from the mirror's thing lists: an
// undesignated plant on a cut cell stands, a designated one, another
// category and a cell off the cut list do not.
func TestStandingPlantsReadsMirrorThings(t *testing.T) {
	plant := policy.Thing{Def: "Plant_Grass", Category: policy.ThingPlant}
	designated := policy.Thing{Def: "Plant_Grass", Category: policy.ThingPlant, Flags: policy.FlagDesignated}
	rock := policy.Thing{Def: "ChunkSlate", Category: policy.ThingItem}
	cells := []policy.SiteCell{
		{Cell: domain.Cell{X: 1, Z: 1}, Things: []policy.Thing{rock, plant}},
		{Cell: domain.Cell{X: 2, Z: 1}, Things: []policy.Thing{designated}},
		{Cell: domain.Cell{X: 3, Z: 1}, Things: []policy.Thing{rock}},
		{Cell: domain.Cell{X: 4, Z: 1}, Things: []policy.Thing{plant}},
	}
	cut := []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}, {X: 3, Z: 1}, {X: 5, Z: 1}}
	got := standingPlants(cells, cut)
	if len(got) != 1 || !got[domain.Cell{X: 1, Z: 1}] {
		t.Fatalf("standing = %v", got)
	}
}
