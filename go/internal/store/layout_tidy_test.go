package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestLayoutTidiesKeepTheLatestStatePerWorld(t *testing.T) {
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
	// A read at an earlier tick sees the state then, and another load of
	// the same world shares the table (#1009); another colony never sees it.
	if got, err := db.LayoutTidies(ctx, first, 200); err != nil || len(got) != 0 {
		t.Fatal("tidy visible before its tick", got, err)
	}
	if got, err := db.LayoutTidies(ctx, extentWorld("colony", "load-2", 1), 300); err != nil || len(got) != 1 || got[0].Status != LayoutTidyMoving {
		t.Fatal(got, err)
	}
	if got, err := db.LayoutTidies(ctx, extentWorld("other", "load-1", 1), 400); err != nil || len(got) != 0 {
		t.Fatal(got, err)
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
	if _, err := db.db.ExecContext(ctx, "INSERT INTO layout_tidies(colony,map_id,tick,item,kind,status,from_x,from_z,from_w,from_h,to_x,to_z,to_w,to_h,crop,new_zone,plan_id,explanation) VALUES(?,?,210,'Zone_5','stockpile','done',0,0,3,3,0,0,3,3,'','','','')", world.Colony, world.Map); err != nil {
		t.Fatal(err)
	}
	if got, err := db.LayoutTidies(ctx, world, 300); err != nil || len(got) != 1 || got[0].Item != "Zone_7" {
		t.Fatal(got, err)
	}
	if err := db.RecordLayoutTidy(ctx, world, 300, LayoutTidy{Item: "Zone_5", Kind: "stockpile", Status: LayoutTidyDone}); err == nil {
		t.Fatal("a stockpile tidy was recorded")
	}
}
