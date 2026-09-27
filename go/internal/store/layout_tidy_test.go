package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestLayoutTidiesFollowTheSavedTimelineAndKeepTheLatestState(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	db := open(t, memoryPath(t))
	first := extentWorld("colony", "load-1", 1)
	if _, err := db.EstablishColonyExtent(ctx, first, 100, []policy.ExtentRegion{extentRegion("a", domain.Cell{X: 0, Z: 0})}); err != nil {
		t.Fatal(err)
	}
	moving := LayoutTidy{Item: "Zone_7", Kind: policy.TidyField, Status: LayoutTidyMoving, From: policy.Rectangle{X: 20, Z: 5, Width: 2, Height: 2}, To: policy.Rectangle{X: 17, Z: 7, Width: 11, Height: 5}, Crop: "Plant_Rice", NewZone: "Zone_9", Explanation: "field Zone_7 -> 11x5 at 17,7"}
	if err := db.RecordLayoutTidy(ctx, first, 250, moving); err != nil {
		t.Fatal(err)
	}
	if err := db.RecordLayoutTidy(ctx, first, 250, LayoutTidy{Kind: policy.TidyField, Status: LayoutTidyMoving}); err == nil {
		t.Fatal("an item-less tidy was accepted")
	}
	if _, err := db.EstablishColonyExtent(ctx, first, 300, []policy.ExtentRegion{extentRegion("b", domain.Cell{X: 1, Z: 0})}); err != nil {
		t.Fatal(err)
	}
	done := moving
	done.Status = LayoutTidyDone
	if err := db.RecordLayoutTidy(ctx, first, 350, done); err != nil {
		t.Fatal(err)
	}
	got, err := db.LayoutTidies(ctx, first, 400)
	if err != nil || len(got) != 1 || got[0].Status != LayoutTidyDone || got[0].Tick != 350 || got[0].To != moving.To || got[0].Crop != "Plant_Rice" {
		t.Fatal(got, err)
	}
	// A save taken while the tidy was moving sees it moving; one taken
	// before it knows nothing; another colony never sees it.
	early := extentWorld("colony", "load-2", 1)
	if got, err := db.LayoutTidies(ctx, early, 200); err != nil || len(got) != 0 {
		t.Fatal("tidy leaked into an earlier save", got, err)
	}
	late := extentWorld("colony", "load-3", 1)
	if got, err := db.LayoutTidies(ctx, late, 300); err != nil || len(got) != 1 || got[0].Status != LayoutTidyMoving {
		t.Fatal(got, err)
	}
	if err := db.RecordLayoutTidy(ctx, late, 320, LayoutTidy{Item: "Zone_7", Kind: policy.TidyField, Status: LayoutTidyAbandoned}); err != nil {
		t.Fatal(err)
	}
	if got, err := db.LayoutTidies(ctx, late, 330); err != nil || len(got) != 1 || got[0].Status != LayoutTidyAbandoned {
		t.Fatal(got, err)
	}
	if got, err := db.LayoutTidies(ctx, first, 400); err != nil || len(got) != 1 || got[0].Status != LayoutTidyDone {
		t.Fatal("the branch's abandonment leaked into its parent", got, err)
	}
	if got, err := db.LayoutTidies(ctx, extentWorld("other", "load-1", 1), 400); err != nil || len(got) != 0 {
		t.Fatal(got, err)
	}
	// A same-load rewind past the tidy's tick forgets it.
	if _, err := db.ReconcileColonyExtent(ctx, first, 240); err != nil {
		t.Fatal(err)
	}
	if got, err := db.LayoutTidies(ctx, first, 240); err != nil || len(got) != 0 {
		t.Fatal("rewound load kept its tidy", got, err)
	}
}

// A journal still holding a stockpile tidy row from before #725 loads and
// ignores it (#933).
func TestLayoutTidiesIgnoreRetiredStockpileRows(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	db := open(t, memoryPath(t))
	world := extentWorld("colony", "load-1", 1)
	if _, err := db.EstablishColonyExtent(ctx, world, 100, []policy.ExtentRegion{extentRegion("a", domain.Cell{X: 0, Z: 0})}); err != nil {
		t.Fatal(err)
	}
	if err := db.RecordLayoutTidy(ctx, world, 200, LayoutTidy{Item: "Zone_7", Kind: policy.TidyField, Status: LayoutTidyDone}); err != nil {
		t.Fatal(err)
	}
	if _, err := db.db.ExecContext(ctx, "INSERT INTO layout_tidies(colony,map_id,load_token,tick,item,kind,status,from_x,from_z,from_w,from_h,to_x,to_z,to_w,to_h,crop,new_zone,plan_id,explanation) VALUES(?,?,?,210,'Zone_5','stockpile','done',0,0,3,3,0,0,3,3,'','','','')", world.Colony, world.Map, world.Load); err != nil {
		t.Fatal(err)
	}
	if got, err := db.LayoutTidies(ctx, world, 300); err != nil || len(got) != 1 || got[0].Item != "Zone_7" {
		t.Fatal(got, err)
	}
	if err := db.RecordLayoutTidy(ctx, world, 300, LayoutTidy{Item: "Zone_5", Kind: "stockpile", Status: LayoutTidyDone}); err == nil {
		t.Fatal("a stockpile tidy was recorded")
	}
}
