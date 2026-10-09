package policy

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// partyBenefit is the AttendedParty mood effect the catalog carries.
const partyBenefit = 8.0

func gatherPawn(id string, mood float64, thoughts ...MoodThought) MoodPawn {
	return MoodPawn{
		ID: PawnID(id), Mood: domain.Known(mood),
		Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), Mental: domain.Known(false),
		Thoughts: domain.Known(thoughts),
	}
}

// gatherFixture is a colony of n pawns, `pressed` of which lose the party's
// own effect to a thought, with a built PartySpot and a calm census.
func gatherFixture(t *testing.T, n, pressed int) (GatheringInput, []MoodPawn) {
	t.Helper()
	var pawns []MoodPawn
	var ledgerPawns []MoodLedgerPawn
	for i := 0; i < n; i++ {
		var thoughts []MoodThought
		if i < pressed {
			thoughts = []MoodThought{{Def: "NeedJoy", Offset: -partyBenefit}}
		}
		pawns = append(pawns, gatherPawn(fmt.Sprintf("p%d", i), 0.5-float64(i)/100, thoughts...))
		ledgerPawns = append(ledgerPawns, MoodLedgerPawn{ID: PawnID(fmt.Sprintf("p%d", i)), Thoughts: domain.Known(thoughts), Traits: domain.Known([]string{}), Precepts: domain.Known([]string{}), Expectation: domain.Unknown[string]()})
	}
	spot, err := domain.NewBuilding(PartySpotDefinition, domain.Cell{X: 1, Z: 1}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	return GatheringInput{
		Ledger:            domain.Known(BuildMoodLedger(ledgerPawns, nil)),
		Pawns:             domain.Known(pawns),
		Benefit:           domain.Known(partyBenefit),
		Calm:              domain.Known(true),
		Census:            domain.Known(CurrentConstruction{Colony: true, Buildings: []CurrentBuilding{{ID: "s", Building: spot, Cells: []domain.Cell{{X: 1, Z: 1}}}}}),
		IdeologyInstalled: domain.Known(false),
		Ideology:          domain.Unknown[Ideoligion](),
	}, pawns
}

func gatherPlan(t *testing.T, in GatheringInput) GatheringPlan {
	t.Helper()
	plan, known := PlanGathering(in).Value()
	if !known {
		t.Fatal("plan unknown")
	}
	return plan
}

func TestGatheringTriggersOnAColonyWideDip(t *testing.T) {
	in, _ := gatherFixture(t, 6, 6)
	plan := gatherPlan(t, in)
	// The highest mood organizes; the party is the vanilla Party.
	if plan.Def != PartyGatheringDef || plan.Organizer != "p0" {
		t.Fatalf("%+v", plan)
	}
	if owed, known := GatheringOwed(domain.Known(plan)).Value(); !known || !owed {
		t.Fatal("a due party holds the concern open")
	}
}

func TestGatheringNeedsTheGuestShareUnderPressure(t *testing.T) {
	// 0.65 of 6 colonists is 3.9: four under pressure qualify, three do not.
	in, _ := gatherFixture(t, 6, 4)
	if gatherPlan(t, in).Organizer == "" {
		t.Fatal("four of six should trigger")
	}
	in, _ = gatherFixture(t, 6, 3)
	if gatherPlan(t, in).Organizer != "" {
		t.Fatal("three of six should not")
	}
	// A loss smaller than the party's own effect is not pressure.
	in, _ = gatherFixture(t, 6, 6)
	in.Benefit = domain.Known(partyBenefit + 1)
	if gatherPlan(t, in).Organizer != "" {
		t.Fatal("a loss the party cannot repay should not trigger")
	}
}

func TestGatheringGates(t *testing.T) {
	cases := []struct {
		name   string
		mutate func(*GatheringInput, *[]MoodPawn)
	}{
		{"too few colonists", func(in *GatheringInput, p *[]MoodPawn) {}}, // fixture below
		{"a raid or critical patient", func(in *GatheringInput, p *[]MoodPawn) { in.Calm = domain.Known(false) }},
		{"a mental break", func(in *GatheringInput, p *[]MoodPawn) { (*p)[2].Mental = domain.Known(true) }},
		{"a party memory is the cooldown", func(in *GatheringInput, p *[]MoodPawn) {
			(*p)[3].Thoughts = domain.Known([]MoodThought{{Def: PartyThought, Offset: partyBenefit}})
		}},
		{"no PartySpot", func(in *GatheringInput, p *[]MoodPawn) {
			in.Census = domain.Known(CurrentConstruction{Colony: true})
		}},
		{"only a PartySpot blueprint", func(in *GatheringInput, p *[]MoodPawn) {
			spot, _ := domain.NewBuilding(PartySpotDefinition, domain.Cell{X: 1, Z: 1}, domain.North, "")
			in.Census = domain.Known(CurrentConstruction{Colony: true, Sites: []ConstructionSite{{Building: spot}}})
		}},
		{"a ritual precept owns the celebration", func(in *GatheringInput, p *[]MoodPawn) {
			in.IdeologyInstalled = domain.Known(true)
			in.Ideology = domain.Known(Ideoligion{Facts: IdeoligionFacts{Rituals: []HeldRitual{{ID: "r", Pattern: "Party"}}}})
		}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			n := 6
			if c.name == "too few colonists" {
				n = GatheringMinColonists - 1
			}
			in, pawns := gatherFixture(t, n, n)
			c.mutate(&in, &pawns)
			in.Pawns = domain.Known(pawns)
			if plan := gatherPlan(t, in); plan.Organizer != "" {
				t.Fatalf("held a party: %+v", plan)
			}
		})
	}
}

func TestGatheringIdeologyWithoutRitualsStillParties(t *testing.T) {
	in, _ := gatherFixture(t, 6, 6)
	in.IdeologyInstalled = domain.Known(true)
	in.Ideology = domain.Known(Ideoligion{})
	if gatherPlan(t, in).Organizer == "" {
		t.Fatal("an ideoligion with no ritual precept has nothing to defer to")
	}
}

func TestGatheringUnknownNeverStarts(t *testing.T) {
	for name, mutate := range map[string]func(*GatheringInput){
		"ledger":    func(in *GatheringInput) { in.Ledger = domain.Unknown[MoodLedger]() },
		"pawns":     func(in *GatheringInput) { in.Pawns = domain.Unknown[[]MoodPawn]() },
		"benefit":   func(in *GatheringInput) { in.Benefit = domain.Unknown[float64]() },
		"calm":      func(in *GatheringInput) { in.Calm = domain.Unknown[bool]() },
		"census":    func(in *GatheringInput) { in.Census = domain.Unknown[CurrentConstruction]() },
		"ideology":  func(in *GatheringInput) { in.IdeologyInstalled = domain.Unknown[bool]() },
		"installed": func(in *GatheringInput) { in.IdeologyInstalled = domain.Known(true) },
	} {
		in, _ := gatherFixture(t, 6, 6)
		mutate(&in)
		if _, known := PlanGathering(in).Value(); known {
			t.Errorf("%s unknown should leave the plan unknown", name)
		}
		if _, known := GatheringOwed(PlanGathering(in)).Value(); known {
			t.Errorf("%s unknown should leave the deficit unknown", name)
		}
	}
	// A known "no" wins over an unknown input.
	in, _ := gatherFixture(t, 6, 6)
	in.Ledger = domain.Unknown[MoodLedger]()
	in.Calm = domain.Known(false)
	if plan, known := PlanGathering(in).Value(); !known || plan.Organizer != "" {
		t.Fatal("a raid is a known no")
	}
}

func TestGatheringOrganizerMustBeAble(t *testing.T) {
	in, pawns := gatherFixture(t, 6, 6)
	pawns[0].Drafted = domain.Known(true)
	pawns[1].Downed = domain.Unknown[bool]()
	in.Pawns = domain.Known(pawns)
	if got := gatherPlan(t, in).Organizer; got != "p2" {
		t.Fatalf("organizer %s", got)
	}
}

func TestGatheringRunningWindow(t *testing.T) {
	if !GatheringRunning(1000, 1000+GatheringMaxTicks-1) || GatheringRunning(1000, 1000+GatheringMaxTicks) {
		t.Fatal("a party runs at most GatheringMaxTicks")
	}
	if GatheringRunning(1000, 999) {
		t.Fatal("a start in the future is no running party")
	}
}

func TestGatheringOwedRaisesHoldGatherings(t *testing.T) {
	f := stableRounds()
	f.GatheringOwed = domain.Known(true)
	for _, g := range needs(t, f, RoundsLatches{}).Concerns {
		if g.ID == HoldGatherings {
			return
		}
	}
	t.Fatal("a due party raised no HoldGatherings goal")
}
