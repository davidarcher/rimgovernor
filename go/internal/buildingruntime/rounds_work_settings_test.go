package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// TestRoundsWorkAdmitsAssignmentsWithPawnSettings: a work assignment and a
// hostility change for the same pawn ride one EnsureWorkAssignments plan;
// the store admits the mixed plan instead of refusing it as a conflict
// (pawn/hostility and pawn/reading-policy stalled on that refusal, #1560).
func TestRoundsWorkAdmitsAssignmentsWithPawnSettings(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	reviewer, db, _, _, native, planners := composedRoundsFixture(t)
	row := native.pawnReply.GetObserved().Pawns[0]
	row.Biography.DisabledWorkTags = []string{"Violent"}
	row.Settings.HostilityResponse = op.HostilityResponse_HOSTILITY_RESPONSE_IGNORE.Enum()
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}

	result, err := planners.work.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatalf("work admission: %+v %v", result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	kinds := map[domain.ActionKind]int{}
	for _, action := range plan.Spec.Actions() {
		kinds[action.Kind()]++
		if s, ok := action.PawnSettings(); ok && s.Hostility() != domain.HostilityFlee {
			t.Fatalf("hostility %v, want Flee", s.Hostility())
		}
	}
	if kinds[domain.WorkAssignmentAction] != 1 || kinds[domain.PawnSettingsAction] != 1 {
		t.Fatalf("plan actions %v, want one assignment and one hostility setting", kinds)
	}
}
