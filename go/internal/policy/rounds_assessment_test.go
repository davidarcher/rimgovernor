package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func assessment(t *testing.T, r RoundsFindings, id ConcernID) domain.Finding {
	t.Helper()
	for _, n := range r.All() {
		if n.ID == id {
			return n.Finding
		}
	}
	t.Fatal("missing assessment", id)
	return ""
}

func TestRoundsAssessmentsDoNotInferRecoveryFromAbsentWork(t *testing.T) {
	r := needs(t, RoundsFacts{}, RoundsLatches{})
	// ImproveIdeoligion contributes an assessment even when eligibility is unknown.
	if len(r.All()) != 56 {
		t.Fatal(r)
	}
	for _, n := range r.All() {
		// EnsureResearch and MaintainResource are gated on
		// operator config (RoundsPolicy.ResearchLadder/ResourceTargets): DefaultRoundsPolicy's empty
		// target/map is itself known evidence ("no target configured" is
		// certain, not unobserved) even though every other assessment here
		// is correctly still Unknown. With a target configured, research and
		// resource needs are measured from native facts (see
		// TestConfiguredTargetsRankForDevelopment). TradeWithCaravan is
		// gated the same way on the trader census being read at all.
		if n.ID == EnsureResearch || n.ID == MaintainResource || n.ID == EnsureDefensiveLayout || n.ID == TradeWithCaravan {
			continue
		}
		if n.Finding != domain.FindingUnclear {
			t.Fatal("missing native facts became evidence", n)
		}
	}
	r = needs(t, stableRounds(), RoundsLatches{})
	for _, n := range r.All() {
		if n.Finding != domain.FindingMet {
			t.Fatal("stable evidence not recovered", n)
		}
	}
}

func TestRoundsAssessmentsRetainHysteresisButRequireFreshEvidence(t *testing.T) {
	f := stableRounds()
	f.FoodDays = domain.Known(2.0)
	r := needs(t, f, RoundsLatches{})
	f.FoodDays = domain.Known(5.0)
	r = needs(t, f, r.Latches)
	if assessment(t, r, EnsureFoodSupply) != domain.FindingUnmet {
		t.Fatal(r)
	}
	f.FoodDays = domain.Unknown[float64]()
	r = needs(t, f, r.Latches)
	if !r.Latches.Food || assessment(t, r, EnsureFoodSupply) != domain.FindingUnclear {
		t.Fatal(r)
	}
	f.FoodDays = domain.Known(8.0)
	r = needs(t, f, r.Latches)
	if assessment(t, r, EnsureFoodSupply) != domain.FindingMet {
		t.Fatal(r)
	}
	f.BedCapacity = domain.Known(int64(0))
	f.IndoorCapacity = domain.Unknown[int64]()
	r = needs(t, f, r.Latches)
	if assessment(t, r, MaintainHousing) != domain.FindingUnmet {
		t.Fatal("known shortage masked by unknown neighbor", r)
	}
}
