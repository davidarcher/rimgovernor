package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A home fire after a short circuit is a priority-1 emergency, but a serve
// without the fire family has no method to clear it: suspending every other
// goal for it parks the clock on no_work and the fire is never fought. The
// undeclared emergency is recorded without the suspension, so an admitted
// power method stays authorized across the injury stop and the authority
// regeneration that follows. Declared, the fire suspends as before.
func TestRoundsUndeclaredFireEmergencyKeepsMethodsAuthorized(t *testing.T) {
	t.Parallel()
	for _, declared := range []bool{false, true} {
		ctx := context.Background()
		s := open(t, memoryPath(t))
		r := roundsRequest()
		r.Current.Native = 3
		r.Facts.AvailableMethods = domain.Known([]policy.ConcernID{policy.EnsureBasicPower})
		if declared {
			r.Facts.AvailableMethods = domain.Known([]policy.ConcernID{policy.EnsureBasicPower, policy.MaintainFireSafety})
		}
		r.Facts.PowerRequired = domain.Known(true)
		r.Facts.PowerHeadroom = domain.Known(-100.0)
		r.Facts.DisabledConsumers = domain.Known(false)
		g := roundsProject(t, reviewRounds(t, s, &r), policy.EnsureBasicPower)
		if g.Project.Finding != domain.FindingUnmet || g.Project.Status != domain.ProjectOpen {
			t.Fatal(declared, g)
		}
		q := methodRequest(t, g, "enclosure", 10)
		d, err := s.AdmitBuildingMethod(ctx, q)
		if err != nil || !d.Admitted {
			t.Fatal(declared, d, err)
		}
		if err = s.AuthorizeRoundsPlan(ctx, r.Current, q.Current); err != nil {
			t.Fatal(declared, err)
		}
		// The short circuit injures a colonist: the clock stops, control is
		// paused for the interruption, then the keep-alive reacquires a new
		// native generation and the review runs with the fire burning.
		r.Enabled = false
		reviewRounds(t, s, &r)
		r.Enabled = true
		r.Current.Native++
		r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{{ID: "fire", Home: true, Size: domain.Known(.5)}})
		out := reviewRounds(t, s, &r)
		fire := roundsGoal(t, out, policy.MaintainFireSafety)
		if fire.Standard.Priority != 1 || fire.Standard.Finding != domain.FindingUnmet {
			t.Fatal("fire need not recorded", declared, fire)
		}
		target := q.Current
		target.Native = r.Current.Native
		err = s.AuthorizeRoundsPlan(ctx, r.Current, target)
		power := roundsProject(t, out, policy.EnsureBasicPower)
		if declared {
			if len(out.Emergency) != 1 || out.Review.VetoProject(power.Project) != "" || err != nil {
				t.Fatal("declared fire blocked ordinary power work", out.Emergency, power, err)
			}
			continue
		}
		if len(out.Emergency) != 0 || power.Project.Status != domain.ProjectOpen {
			t.Fatal("undeclared fire suspended the colony", out.Emergency, power)
		}
		if err != nil {
			t.Fatal("power method not authorized under the regenerated root", err)
		}
	}
}
