package startuplabor

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func capSample(tick domain.Tick, unused, runnable, committed int, limiting policy.DevelopmentReason) CapacitySample {
	return CapacitySample{Tick: tick, Unused: domain.Known(unused), Runnable: runnable, Committed: committed, Limiting: limiting}
}

func TestStrandingsFlagsPersistentUnusedCapacity(t *testing.T) {
	var run []CapacitySample
	for tick := domain.Tick(0); tick <= 10000; tick += 1000 {
		run = append(run, capSample(tick, 3, 2, 2, policy.DevelopmentCapacity))
	}
	got := Strandings(run, 0)
	if len(got) != 1 || got[0].From != 0 || got[0].To != 10000 || got[0].Limiting != policy.DevelopmentCapacity {
		t.Fatal("capacity_committed with a worker unused is slack, not a constraint", got)
	}
}

func TestStrandingsExcuseAdmissionEnforcedAndUnknown(t *testing.T) {
	for name, mid := range map[string]CapacitySample{
		"admission":  capSample(4000, 3, 2, 3, policy.DevelopmentCapacity),
		"overcommit": capSample(4000, 3, 2, 2, policy.DevelopmentOvercommitted),
		"stage":      capSample(4000, 3, 2, 2, policy.DevelopmentStage),
		"no ready":   capSample(4000, 3, 0, 2, policy.DevelopmentCapacity),
		"no unused":  capSample(4000, 0, 2, 2, policy.DevelopmentCapacity),
		"unknown":    {Tick: 4000, Unused: domain.Unknown[int](), Runnable: 2, Committed: 2},
	} {
		run := []CapacitySample{capSample(0, 3, 2, 2, ""), mid}
		for tick := domain.Tick(5000); tick <= 10000; tick += 1000 {
			run = append(run, capSample(tick, 3, 2, run[len(run)-1].Committed, ""))
		}
		if got := Strandings(run, 0); len(got) != 0 {
			t.Fatal(name, got)
		}
	}
}

func TestStrandingsRewindEndsTheStretch(t *testing.T) {
	run := []CapacitySample{capSample(5000, 1, 1, 1, ""), capSample(9000, 1, 1, 1, ""), capSample(1000, 1, 1, 1, ""), capSample(6000, 1, 1, 1, "")}
	if got := Strandings(run, 0); len(got) != 0 {
		t.Fatal("neither side of the rewind outlasts the budget", got)
	}
}
