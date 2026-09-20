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
	db := caravanTrackingFixture(t)
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
