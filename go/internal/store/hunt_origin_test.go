package store

import (
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The food plan raises ActiveCombat with no hostile (#1617): its occurrence
// carries the squad prey and recovers when the plan stops opening the hunt.
func TestRoutineReviewHuntOrigin(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "hunt.db"))
	r := routineRequest()
	rows := []policy.AcquisitionSource{{ID: "a", Hunt: true, Food: true, NutritionYield: 10, Yield: 1}, {ID: "b", Hunt: true, Food: true, NutritionYield: 10, Yield: 1}, {ID: "c", Hunt: true, Food: true, NutritionYield: 10, Yield: 1}}
	squads, _ := policy.SquadHunts(rows, policy.SquadHuntMaxGunners, domain.Fact[string]{})
	r.Facts.FoodPlan = domain.Known(policy.FoodPlan{Portfolio: []policy.FoodPlanEntry{{Channel: squads[0], Decision: policy.FoodPlanOpen}}})
	out := reviewRoutine(t, s, &r)
	b, ok := out.Review.Incident(policy.ActiveCombat)
	if !ok || b.Need != domain.NeedDeficit {
		t.Fatal("hunt raised no ActiveCombat incident", out.Review.Incidents)
	}
	incident := routineIncident(t, out, policy.ActiveCombat).Incident
	if got := HuntPrey(incident); !reflect.DeepEqual(got, []domain.PawnID{"a", "b", "c"}) || incident.Trigger != "ActiveCombat hunt" {
		t.Fatalf("origin = %v %q", got, incident.Trigger)
	}
	// A raid arriving mid-hunt is the fight's, not the hunt's.
	r.Facts.Hostiles = domain.Known(int64(2))
	out = reviewRoutine(t, s, &r)
	if got := HuntPrey(routineIncident(t, out, policy.ActiveCombat).Incident); len(got) != 0 {
		t.Fatalf("raid kept the hunt origin: %v", got)
	}
	r.Facts.Hostiles = domain.Known(int64(0))
	r.Facts.FoodPlan = domain.Unknown[policy.FoodPlan]()
	if out = reviewRoutine(t, s, &r); len(out.Review.Incidents) != 0 {
		t.Fatal("ended hunt left the incident open", out.Review.Incidents)
	}
}
