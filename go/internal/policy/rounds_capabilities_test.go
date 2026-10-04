package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestRoundsDisabledMethodsYieldSlotsWithoutErasingNeeds(t *testing.T) {
	f := stableRounds()
	f.IndoorCapacity = domain.Known(int64(3))
	f.ComfortRecovered = domain.Known(false)
	f.ComfortDeficit = domain.Known(.8)
	f.AvailableMethods = domain.Known([]ConcernID{MaintainHousing})
	needs := needs(t, f, RoundsLatches{})
	request := developmentFixture()
	request.Concerns = needs.Concerns
	got := rank(t, request)
	if !reflect.DeepEqual(selected(got), []ConcernID{MaintainHousing}) {
		t.Fatal(got)
	}
	found := false
	for _, a := range needs.Assessments {
		if a.ID == EnsureComfort {
			found = true
			if a.Finding != domain.FindingUnmet {
				t.Fatal(a)
			}
		}
	}
	if !found {
		t.Fatal("disabled method erased need")
	}
	for _, row := range got.Rows {
		if row.Concern == EnsureComfort && row.Reason != DevelopmentMethodUnavailable {
			t.Fatal(row)
		}
	}
	f.AvailableMethods = domain.Known([]ConcernID{})
	empty, err := InspectRounds(f, RoundsLatches{}, DefaultRoundsPolicy())
	if err != nil {
		t.Fatal(err)
	}
	request.Concerns = empty.Concerns
	if len(selected(rank(t, request))) != 0 {
		t.Fatal("disabled methods admitted")
	}
	for _, bad := range [][]ConcernID{{EnsureComfort, EnsureComfort}, {"unknown-method"}} {
		f.AvailableMethods = domain.Known(bad)
		if _, err := InspectRounds(f, RoundsLatches{}, DefaultRoundsPolicy()); err == nil {
			t.Fatal(bad)
		}
	}
}

// Every method capability the composed serve default declares must validate
// against empty facts, which is how NewRounder checks them before any
// native read. RecoverDisasterServices is only assessed once a disaster
// history exists, so it needs an explicit recognition.
func TestRoundsComposedCapabilitiesValidateOnEmptyFacts(t *testing.T) {
	all := []ConcernID{EnsureFoodSupply, MaintainFoodStorage, MaintainResource, EnsureCooking, EnsureTemperatureSafety, EnsureBasicPower, EnsureComfort, MaintainHousing, MaintainAnimalContainment, MaintainEssentialRepairs, MaintainCleanFacilities, MaintainWaste, RecoverDisasterServices, MaintainHerd, MaintainPopulation, MaintainHomeCoverage, MaintainStoneShell, EnsureResearch, MaintainAnimalFeed, RemoveBlight}
	if _, err := InspectRounds(RoundsFacts{AvailableMethods: domain.Known(all)}, RoundsLatches{}, DefaultRoundsPolicy()); err != nil {
		t.Fatal(err)
	}
}

// MaintainHousing's method availability follows the declared capability:
// a confirmed sleeping deficit ranks as a known Deficit and is only marked
// method-unavailable when the sleeping family is not declared.
func TestRoundsSleepingMethodFollowsDeclaredCapability(t *testing.T) {
	f := stableRounds()
	f.SleepingRecovered = domain.Known(false)
	for _, declared := range []bool{false, true} {
		methods := []ConcernID{}
		if declared {
			methods = append(methods, MaintainHousing)
		}
		f.AvailableMethods = domain.Known(methods)
		needs := needs(t, f, RoundsLatches{})
		found := false
		for _, g := range needs.Concerns {
			if g.ID != MaintainHousing {
				continue
			}
			found = true
			if deficit, known := g.Deficit.Value(); !known || deficit != 1 {
				t.Fatal(declared, g)
			}
			if g.MethodUnavailable == declared {
				t.Fatal("method availability does not follow the declared capability", declared, g)
			}
		}
		if !found {
			t.Fatal("sleeping deficit not raised", declared)
		}
	}
}

// The animal needs are admitted through the same capability gate as every
// other optional routine goal: declared, they rank and may take a development
// slot; undeclared, they stay MethodUnavailable without erasing the need.
func TestRoundsAnimalNeedsRankWhenTheirMethodIsDeclared(t *testing.T) {
	f := stableRounds()
	// A census that knows of no tame animal raises no animal goal; this one
	// does not know.
	f.AnimalUpkeep.Animals = domain.Unknown[[]UpkeepAnimal]()
	f.UpkeepIssued = map[ConcernID]bool{MaintainAnimalFeed: true, MaintainAnimalContainment: true}
	for _, declared := range []bool{true, false} {
		f.AvailableMethods = domain.Known([]ConcernID{})
		if declared {
			f.AvailableMethods = domain.Known([]ConcernID{MaintainAnimalFeed, MaintainAnimalContainment})
		}
		request := developmentFixture()
		request.Concerns = needs(t, f, RoundsLatches{}).Concerns
		got := selected(rank(t, request))
		if declared != (len(got) == 2) {
			t.Fatalf("declared=%v selected=%v", declared, got)
		}
		for _, g := range request.Concerns {
			if (g.ID == MaintainAnimalFeed || g.ID == MaintainAnimalContainment) && g.MethodUnavailable == declared {
				t.Fatalf("declared=%v goal=%+v", declared, g)
			}
		}
	}
}

// A home fire is a priority-1 emergency only when the fire family is
// declared: undeclared, the assessment is marked method-unavailable so the
// review records the need without suspending the colony behind a method it
// does not have (#435). Unknown capabilities leave the emergency intact.
func TestRoundsFireEmergencyFollowsDeclaredCapability(t *testing.T) {
	f := stableRounds()
	f.Upkeep.Fires = domain.Known([]UpkeepFire{{ID: "fire", Home: true, Size: domain.Known(.5)}})
	for _, tc := range []struct {
		name        string
		methods     domain.Fact[[]ConcernID]
		unavailable bool
	}{
		{"unknown", domain.Unknown[[]ConcernID](), false},
		{"declared", domain.Known([]ConcernID{MaintainFireSafety}), false},
		{"undeclared", domain.Known([]ConcernID{}), true},
	} {
		f.AvailableMethods = tc.methods
		found := false
		for _, a := range needs(t, f, RoundsLatches{}).Assessments {
			if a.ID != MaintainFireSafety {
				continue
			}
			found = true
			if a.Priority != 1 || a.Finding != domain.FindingUnmet || a.MethodUnavailable != tc.unavailable {
				t.Fatal(tc.name, a)
			}
		}
		if !found {
			t.Fatal(tc.name, "fire not assessed")
		}
	}
}
