package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func (s *playerFakeSession) ResourceRules() []policy.ResourceRule {
	return append([]policy.ResourceRule(nil), s.rules...)
}

func TestRoutineMethodHonorsConfiguredResourceRules(t *testing.T) {
	for _, phase := range []string{"allow", "stop", "defense", "reserve"} {
		t.Run(phase, func(t *testing.T) {
			planner, _, native := cookingFixture(t)
			rules := []policy.ResourceRule{{Resource: "WoodLog", Spending: policy.Allow}}
			switch phase {
			case "stop":
				rules[0].Spending = policy.Stop
			case "defense":
				rules[0].Spending = policy.DefenseOnly
			case "reserve":
				rules[0].Reserve = 1
			}
			old := planner.reviewer
			old.player.session.(*playerFakeSession).rules = rules
			reviewer, err := NewRoutineReviewer(old.player, old.native, old.clock, old.policy, old.maxAge)
			if err != nil {
				t.Fatal(err)
			}
			planner.reviewer = reviewer
			rules[0] = policy.ResourceRule{Resource: "WoodLog", Spending: policy.Allow}
			out, err := planner.Step(context.Background())
			if err != nil || native.previews != 1 {
				t.Fatal(out, err)
			}
			if phase == "allow" {
				if !out.Decision.Admitted {
					t.Fatal(out)
				}
				return
			}
			want := policy.SpendingBlocked
			if phase == "reserve" {
				want = policy.InsufficientStock
			}
			if out.Decision.Admitted || len(out.Decision.Refused) != 1 || out.Decision.Refused[0].Reason != want {
				t.Fatal(out)
			}
		})
	}
}
