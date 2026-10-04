package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestShrineArrestRoutesOnlyStandingNeutralCaptureDecisions(t *testing.T) {
	base := joinerFacts(t, 2)
	for name, occupant := range map[string]ShrineOccupant{
		"standing ancient": {EntityID: "ancient", Faction: "Ancients"},
		"visitor":          {EntityID: "visitor", Faction: "OutlanderCivil"},
		"hostile":          {EntityID: "hostile", Faction: "Ancients", Hostile: true},
		"downed":           {EntityID: "downed", Faction: "Ancients", Downed: true},
		"dead":             {EntityID: "dead", Faction: "Ancients", Dead: true},
		"prisoner":         {EntityID: "prisoner", Faction: "Ancients", Prisoner: true},
	} {
		t.Run(name, func(t *testing.T) {
			facts := RoutineFacts{Custody: base.Custody, Sleeping: base.Sleeping, FoodDays: base.FoodDays}
			facts.Upkeep.Shrines = domain.Known([]AncientShrine{{ID: "shrine", Occupants: []ShrineOccupant{occupant}}})
			want := domain.PawnID("")
			if name == "standing ancient" {
				want = "ancient"
			}
			if got := ShrineArrestTarget(facts); got != want {
				t.Fatalf("target = %q, want %q", got, want)
			}
			facts.Sleeping = domain.Unknown[SleepingObservation]()
			if got := ShrineArrestTarget(facts); got != "" {
				t.Fatalf("unknown capacity selected %q", got)
			}
			facts.Sleeping = base.Sleeping
			facts.FoodDays = domain.Known(0.0)
			if got := ShrineArrestTarget(facts); got != "" {
				t.Fatalf("no food capacity selected %q", got)
			}
		})
	}
}

func TestShrineArrestBedRequiresVacantPrisonerBed(t *testing.T) {
	prison := spareSleepingBed("prison")
	prison.Prisoners = domain.Known(true)
	if got := ShrineArrestBed(joinerBeds(spareSleepingBed("ordinary"), prison)); got != "prison" {
		t.Fatal(got)
	}
	for _, change := range []func(*SleepingBed){
		func(b *SleepingBed) { b.Users = []PawnID{"held"} },
		func(b *SleepingBed) { b.Owners = []PawnID{"held"} },
		func(b *SleepingBed) { b.Prisoners = domain.Unknown[bool]() },
		func(b *SleepingBed) { b.Humanlike = domain.Known(false) },
	} {
		bed := prison
		change(&bed)
		if got := ShrineArrestBed(joinerBeds(bed)); got != "" {
			t.Fatal(got)
		}
	}
}

func TestShrineArrestCreatesPopulationDeficitAndSelectsArmedPerformer(t *testing.T) {
	facts := stableRoutine()
	capacity := joinerFacts(t, 2)
	facts.Custody, facts.Sleeping, facts.FoodDays = capacity.Custody, capacity.Sleeping, capacity.FoodDays
	facts.Upkeep.Shrines = domain.Known([]AncientShrine{{ID: "shrine", Occupants: []ShrineOccupant{{EntityID: "ancient", Faction: "Ancients"}}}})
	needs, err := DetectRoutine(facts, RoutineLatches{}, DefaultRoutinePolicy())
	if err != nil {
		t.Fatal(err)
	}
	found := false
	for _, assessment := range needs.Assessments {
		if assessment.ID == MaintainPopulation {
			found = assessment.Finding == domain.FindingUnmet
		}
	}
	if !found {
		t.Fatal("standing ancient did not create a population deficit")
	}
	unarmed := shrineDefender("a", false, 0)
	unarmed.Armed = domain.Known(false)
	busy := shrineDefender("b", false, 0)
	busy.Drafted, busy.DraftOwned = domain.Known(true), domain.Known(true)
	if got := ShrineArrester([]ShrineDefenderFacts{unarmed, busy, shrineDefender("c", false, 0)}); got != "c" {
		t.Fatal(got)
	}
}
