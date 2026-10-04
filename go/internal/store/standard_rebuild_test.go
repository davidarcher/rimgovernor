package store

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The orphan pass sees the committed method's plan before the rebuild
// deletes the method row and retires the plan (#998/#1000); a pass error
// aborts the rebuild with both intact.
func TestRebuildStandardsHandsOldPlansToOrphanPass(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, g := goalFixture(t)
	id := domain.MintPlanID()
	g, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "shell", plan(t, id, domain.ActionID(id+"-0")))
	if err != nil {
		t.Fatal(err)
	}
	blob, err := json.Marshal(GovernorStandardBlob{SchemaVersion: GovernorStateSchemaVersion, Standard: g.Standard, Revision: g.Revision})
	if err != nil {
		t.Fatal(err)
	}
	saved := map[string]string{GovernorStandardKeyPrefix + string(g.Standard.ID): string(blob)}
	state := func() (methods, retired int) {
		t.Helper()
		if err := s.db.QueryRowContext(ctx, "SELECT COUNT(*) FROM plan_methods WHERE plan_id=?", id).Scan(&methods); err != nil {
			t.Fatal(err)
		}
		if err := s.db.QueryRowContext(ctx, "SELECT retired FROM plans WHERE id=?", id).Scan(&retired); err != nil {
			t.Fatal(err)
		}
		return
	}
	refused := errors.New("refused")
	if err = s.RebuildStandards(ctx, saved, func(context.Context, []PlanState) error { return refused }); !errors.Is(err, refused) {
		t.Fatal("pass error did not abort the rebuild", err)
	}
	if methods, retired := state(); methods != 1 || retired != 0 {
		t.Fatal("aborted rebuild touched the method", methods, retired)
	}
	var seen []domain.PlanID
	if err = s.RebuildStandards(ctx, saved, func(_ context.Context, plans []PlanState) error {
		if methods, retired := state(); methods != 1 || retired != 0 {
			t.Error("pass ran after the retire", methods, retired)
		}
		for _, p := range plans {
			seen = append(seen, p.Spec.ID())
		}
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	if len(seen) != 1 || seen[0] != id {
		t.Fatal("pass did not see the old plan", seen)
	}
	if methods, retired := state(); methods != 0 || retired != 1 {
		t.Fatal("method not deleted or plan not retired", methods, retired)
	}
	if kept, err := s.LoadStandard(ctx, g.Standard.ID); err != nil || len(kept.Methods) != 0 {
		t.Fatal("saved goal not rebuilt without methods", kept, err)
	}
}
