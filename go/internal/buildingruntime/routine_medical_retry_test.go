package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestMedicalAttemptCountScopesToPrefixAndEpoch(t *testing.T) {
	t.Parallel()
	methods := []domain.GoalMethod{
		{Goal: "CriticalMedical", Epoch: 1, Method: "tend-alice-0", Plan: "p0"},
		{Goal: "CriticalMedical", Epoch: 1, Method: "tend-alice-1", Plan: "p1"},
		{Goal: "CriticalMedical", Epoch: 1, Method: "tend-bob-0", Plan: "p2"},
		{Goal: "CriticalMedical", Epoch: 1, Method: "rescue-alice-0", Plan: "p3"},
		{Goal: "CriticalMedical", Epoch: 2, Method: "tend-alice-0", Plan: "p4"},
	}
	if n := medicalAttemptCount(methods, 1, "tend-alice-"); n != 2 {
		t.Fatalf("tend-alice epoch 1: got %d, want 2", n)
	}
	if n := medicalAttemptCount(methods, 1, "tend-bob-"); n != 1 {
		t.Fatalf("tend-bob epoch 1: got %d, want 1", n)
	}
	if n := medicalAttemptCount(methods, 1, "rescue-alice-"); n != 1 {
		t.Fatalf("rescue-alice epoch 1: got %d, want 1", n)
	}
	// A new episode (epoch) resets the retry budget for the same patient.
	if n := medicalAttemptCount(methods, 2, "tend-alice-"); n != 1 {
		t.Fatalf("tend-alice epoch 2: got %d, want 1", n)
	}
	if n := medicalAttemptCount(methods, 3, "tend-alice-"); n != 0 {
		t.Fatalf("tend-alice epoch 3: got %d, want 0", n)
	}
	if n := medicalAttemptCount(nil, 1, "tend-alice-"); n != 0 {
		t.Fatalf("empty methods: got %d, want 0", n)
	}
}

func TestMaxMedicalAttemptsPerPatientIsPositiveAndBounded(t *testing.T) {
	t.Parallel()
	if maxMedicalAttemptsPerPatient <= 0 || maxMedicalAttemptsPerPatient > 256 {
		t.Fatal(maxMedicalAttemptsPerPatient)
	}
}

// Each completed covered-storage zone earns SecureSupplies another round of
// direct hauls: before the zone exists native refuses every haul, so the base
// bound alone would send the goal straight back to its fallbacks once the
// zone was placed.
func TestSecureSuppliesHaulBudgetGrowsWithCompletedZones(t *testing.T) {
	t.Parallel()
	if secureSuppliesHaulBudget(0) != maxSecureSuppliesHaulAttempts || secureSuppliesHaulBudget(-1) != maxSecureSuppliesHaulAttempts {
		t.Fatal("base budget", secureSuppliesHaulBudget(0))
	}
	if secureSuppliesHaulBudget(1) != 2*maxSecureSuppliesHaulAttempts || secureSuppliesHaulBudget(maxSecureSuppliesZoneMethods) != (1+maxSecureSuppliesZoneMethods)*maxSecureSuppliesHaulAttempts {
		t.Fatal("zones", secureSuppliesHaulBudget(1))
	}
}
