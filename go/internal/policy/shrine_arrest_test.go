package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestShrineArrestRoutesOnlyStandingNeutralCaptureDecisions(t *testing.T) {
	base := joinerFacts(t, 2, 5)
	for name, occupant := range map[string]ShrineOccupant{
		"standing ancient": {EntityID: "ancient", Faction: "Ancients"},
		"visitor":          {EntityID: "visitor", Faction: "OutlanderCivil"},
		"hostile":          {EntityID: "hostile", Faction: "Ancients", Hostile: true},
		"downed":           {EntityID: "downed", Faction: "Ancients", Downed: true},
		"dead":             {EntityID: "dead", Faction: "Ancients", Dead: true},
		"prisoner":         {EntityID: "prisoner", Faction: "Ancients", Prisoner: true},
	} {
		t.Run(name, func(t *testing.T) {
			facts := RoutineFacts{Custody: base.Custody, Sleeping: base.Sleeping, FoodDays: base.FoodDays, PopulationCapacity: base.Policy}
			facts.Upkeep.Shrines = domain.Known([]AncientShrine{{ID: "shrine", Occupants: []ShrineOccupant{occupant}}})
			want := domain.PawnID("")
			if name == "standing ancient" {
				want = "ancient"
			}
			if got := ShrineArrestTarget(facts); got != want {
				t.Fatalf("target = %q, want %q", got, want)
			}
			facts.PopulationCapacity = domain.Unknown[domain.PopulationPolicy]()
			if got := ShrineArrestTarget(facts); got != "" {
				t.Fatalf("unknown capacity selected %q", got)
			}
			facts.PopulationCapacity = base.Policy
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
	capacity := joinerFacts(t, 2, 5)
	facts.Custody, facts.Sleeping, facts.FoodDays, facts.PopulationCapacity = capacity.Custody, capacity.Sleeping, capacity.FoodDays, capacity.Policy
	facts.Upkeep.Shrines = domain.Known([]AncientShrine{{ID: "shrine", Occupants: []ShrineOccupant{{EntityID: "ancient", Faction: "Ancients"}}}})
	needs, err := DetectRoutine(facts, RoutineLatches{}, DefaultRoutinePolicy())
	if err != nil {
		t.Fatal(err)
	}
	found := false
	for _, assessment := range needs.Assessments {
		if assessment.ID == MaintainPopulation {
			found = assessment.Need == domain.NeedDeficit
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

func TestArrestAdmissionRequiresStandingPatientDraftAndNativeEligibility(t *testing.T) {
	makeRequest := func() CaptureRequest {
		r := captureRequest(t)
		intent, _ := domain.NewArrest("capturer", "patient", "prison")
		action, _ := domain.NewCaptureAction("capture", intent)
		draft, _ := domain.NewOwnedDraft("capturer")
		da, _ := domain.NewOwnedDraftAction("draft", draft)
		plan, err := domain.NewPlan("plan", 1, []domain.Action{da, action}, domain.ActionDependency{Action: action.ID(), Requires: da.ID(), Coupled: true})
		if err != nil {
			t.Fatal(err)
		}
		r.Action = action
		r.Progress, _ = domain.NewProgress(plan, action.ID())
		r.Facts.Capturer.Drafted = domain.Known(true)
		r.Facts.Patient.Downed = domain.Known(false)
		return r
	}
	if got := EvaluateCapture(makeRequest()); !got.Admitted {
		t.Fatal(got)
	}
	for _, change := range []func(*CaptureRequest){
		func(r *CaptureRequest) { r.Facts.Capturer.Drafted = domain.Known(false) },
		func(r *CaptureRequest) { r.Facts.Patient.Downed = domain.Known(true) },
		func(r *CaptureRequest) { r.Facts.Patient.Prisoner = domain.Known(true) },
		func(r *CaptureRequest) { r.Facts.NativeCanTry = domain.Known(false) },
	} {
		r := makeRequest()
		change(&r)
		if got := EvaluateCapture(r); got.Admitted {
			t.Fatal(got)
		}
	}
}
