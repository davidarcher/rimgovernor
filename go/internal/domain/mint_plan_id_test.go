package domain

import (
	"regexp"
	"testing"
)

func TestMintPlanID(t *testing.T) {
	t.Parallel()
	shape := regexp.MustCompile(`^routine-x-[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`)
	seen := map[PlanID]bool{}
	for range 1000 {
		id := MintPlanID("routine-x")
		if !shape.MatchString(string(id)) || seen[id] {
			t.Fatal(id)
		}
		if _, err := NewPlan(id, 1, nil); err != nil {
			t.Fatal(err)
		}
		seen[id] = true
	}
}
