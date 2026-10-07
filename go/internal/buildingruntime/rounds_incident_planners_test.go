package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The mood relief planner commits a pawn's relief to that pawn's EnsureMood
// incident (#1078).
func TestRoundsMoodReliefPlannerCommitsToPawnIncident(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, _, _, _ := roundsFixture(t)
	ctx := context.Background()
	out, err := r.Step(ctx)
	if err != nil {
		t.Fatal(err)
	}
	pawn := policy.MoodPawn{ID: "pawn", Mood: domain.Known(.2), Threshold: domain.Known(.3), Food: domain.Known(.1), Rest: domain.Known(.8), Joy: domain.Known(.8), Mental: domain.Known(false), Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), PlayerForced: domain.Known(false)}
	facts := policy.RoundsFacts{Workers: domain.Known(2), Wood: domain.Known(int64(100)), Hostiles: domain.Known(int64(0)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ChoiceDialog: domain.Known(false), MoodPawns: domain.Known([]policy.MoodPawn{pawn})}
	mood, err := db.ReviewRounds(ctx, store.RoundsRequest{Current: out.Review.Snapshot, Revision: out.Review.Revision, Tick: out.Review.Tick + 1, Enabled: true, Policy: policy.DefaultRoundsPolicy(), Facts: facts})
	if err != nil {
		t.Fatal(err)
	}
	bindings := mood.Review.SubjectIncidents(policy.EnsureMood)
	if len(bindings) != 1 || bindings[0].Subject != "pawn" || bindings[0].Situation != domain.SituationActive {
		t.Fatal(mood.Review.Incidents)
	}
	planner, err := NewRoundsMoodReliefPlanner(r)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	incident, err := db.LoadIncident(ctx, bindings[0].Incident)
	if err != nil || len(incident.Methods) != 1 || incident.Methods[0].Plan != result.Plan || incident.Methods[0].Method != "mood-food-pawn-0" {
		t.Fatal("relief not bound to the pawn's incident", incident, err)
	}
}
