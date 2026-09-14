package domain

import (
	"math"
	"testing"
)

func TestPopulationPolicyBounds(t *testing.T) {
	t.Parallel()
	for _, valid := range [][2]float64{{1, 1}, {100, 120}, {12, 30.5}} {
		policy, err := NewPopulationPolicy(int32(valid[0]), valid[1])
		if err != nil || policy.Maximum() != int32(valid[0]) || policy.FoodDays() != valid[1] || !policy.Set() {
			t.Fatal(valid, policy, err)
		}
	}
	for _, invalid := range [][2]float64{
		{0, 30}, {-1, 30}, {101, 30},
		{12, 0}, {12, 0.99}, {12, 120.01}, {12, -1},
		{12, math.NaN()}, {12, math.Inf(1)}, {12, math.Inf(-1)},
	} {
		if _, err := NewPopulationPolicy(int32(invalid[0]), invalid[1]); err == nil {
			t.Fatal("out-of-range population policy must be rejected", invalid)
		}
	}
	if (PopulationPolicy{}).Set() {
		t.Fatal("the zero value must report no policy")
	}
}

// A population policy is deliberately not a plan action: it names no native
// entity and has no ActionKind, so no supported action kind can carry one.
func TestPopulationPolicyIsNotAnActionKind(t *testing.T) {
	t.Parallel()
	for _, kind := range SupportedActionKinds() {
		if string(kind) == "population_policy" {
			t.Fatal("population policy must not be a dispatchable action kind")
		}
	}
}
