package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestRoutineExecutionRequiresCurrentReviewedMethod(t *testing.T) {
	for _, change := range []string{"valid", "direction", "native", "load", "revision", "unbound", "disabled", "cancelled", "unknown"} {
		t.Run(change, func(t *testing.T) {
			ctx := context.Background()
			s := open(t, filepath.Join(t.TempDir(), "routine.db"))
			r := routineRequest()
			r.Current.Direction, r.Current.Native = 1, 2
			g := routineGoal(t, reviewRoutine(t, s, &r), policy.MaintainWood)
			q := methodRequest(t, g, "method", 10)
			d, err := s.AdmitBuildingMethod(ctx, q)
			if err != nil || !d.Admitted {
				t.Fatal(d, err)
			}
			root, target := r.Current, q.Current
			switch change {
			case "direction":
				target.Direction++
			case "native":
				target.Native++
			case "load":
				target.Load = "other"
			case "revision":
				target.Revision++
			case "unbound":
				target.Plan = "other"
			case "disabled":
				r.Enabled = false
				reviewRoutine(t, s, &r)
			case "cancelled":
				if _, err = s.CancelGoal(ctx, g.Goal.ID, d.Goal.Revision); err != nil {
					t.Fatal(err)
				}
			case "unknown":
				r.Facts.Wood = domain.Unknown[int64]()
				reviewRoutine(t, s, &r)
			}
			err = s.AuthorizeRoutinePlan(ctx, root, target)
			if (err == nil) != (change == "valid") {
				t.Fatal(change, err)
			}
		})
	}
}
