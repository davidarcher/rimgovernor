package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"reflect"
	"testing"
)

func breakingPawn(name string, aggro bool) EmergencyPawn {
	return EmergencyPawn{ID: "target", MentalState: domain.Known(MentalState{DefName: name, IsAggro: aggro, TicksInState: 60}), Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}
}
func TestBreakResponseStateKinds(t *testing.T) {
	for _, tc := range []struct {
		name  string
		aggro bool
	}{{"Berserk", true}, {"MurderousRage", true}, {"Slaughterer", true}, {"ModdedAggression", true}, {"Wander_Sad", false}, {"Binging_Food", false}, {"Tantrum", false}, {"HideInRoom", false}} {
		t.Run(tc.name, func(t *testing.T) {
			pawn := breakingPawn(tc.name, tc.aggro)
			r := meleeRequest(t)
			facts := r.Facts.Emergency.facts
			facts.Threats = nil
			facts.Colonists = append(facts.Colonists, pawn)
			snapshot, err := NewEmergencySnapshot(r.Current, 13, facts)
			if err != nil {
				t.Fatal(err)
			}
			threats, _ := EmergencyNeeds(snapshot, r.Current, 13)
			n, known := threats.Value()
			if !known || (n == 1) != tc.aggro {
				t.Fatalf("emergency: %v", threats)
			}
			h, err := ReviewMood(domain.Known([]MoodPawn{{ID: pawn.ID, Mental: domain.Known(true), Break: pawn.MentalState}}), MoodHistory{})
			if err != nil || len(h.States) != 1 || !h.States[0].Active {
				t.Fatalf("mood: %+v %v", h, err)
			}
			responders := []BreakResponder{{SquadDefenderFacts: squadDefender("responder", false), Cell: domain.Known(domain.Cell{X: 1})}}
			squad := SelectBreakSquad(pawn, domain.Known(domain.Cell{}), responders)
			if (len(squad) == 1) != tc.aggro {
				t.Fatal(squad)
			}
		})
	}
}

func TestSocialFightingDoesNotRaiseCombatEmergency(t *testing.T) {
	f, limits := clockWindowFixture(t)
	facts := completeEmergency()
	for _, id := range []PawnID{"fighter-a", "fighter-b"} {
		pawn := breakingPawn("SocialFighting", true)
		pawn.ID = id
		facts.Colonists = append(facts.Colonists, pawn)
		if AggressiveBreak(pawn) {
			t.Fatal("social fighter selected for subdue")
		}
	}
	var err error
	f.Emergency, err = NewEmergencySnapshot(f.Current, f.Tick, facts)
	if err != nil {
		t.Fatal(err)
	}
	if d := EvaluateEmergency(f.Emergency, f.Current, f.Tick); !d.Clear {
		t.Fatalf("social fight held the colony: %+v", d)
	}
	routine := stableRoutine()
	routine.Hostiles, routine.CriticalPatients = EmergencyNeeds(f.Emergency, f.Current, f.Tick)
	if r := needs(t, routine, RoutineLatches{}); hasNeed(r, ActiveCombat) {
		t.Fatalf("social fight raised ActiveCombat: %+v", r)
	}
	if d := EvaluateClockWindow(f, limits); !d.Admitted || d.Mode != ClockWindowColony || len(d.Hostiles) != 0 {
		t.Fatalf("social fight blocked routine ticks: %+v", d)
	}
	// Social fights must not hide independent combat or medical emergencies.
	facts.Threats = []EmergencyThreat{{ID: "raider", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}}
	if !hasEmergencyHold(evaluateEmergency(t, facts), EmergencyUnsafeThreat, "raider") {
		t.Fatal("social fight hid a hostile")
	}
	facts.Threats = nil
	facts.Colonists[0].Bleeding = domain.Known(true)
	if !hasEmergencyHold(evaluateEmergency(t, facts), EmergencyCriticalMedical, "fighter-a") {
		t.Fatal("social fight hid urgent medical care")
	}
}

func TestBreakResponseClockNeedsContainmentThenAllowsCombatTicks(t *testing.T) {
	f, l := clockWindowFixture(t)
	l.CombatMaxTicks = 30
	p := breakingPawn("Berserk", true)
	p.Bleeding = domain.Known(true)
	f.Emergency.facts.Colonists = append(f.Emergency.facts.Colonists, p)
	if EvaluateClockWindow(f, l).Admitted {
		t.Fatal("uncontained break advanced")
	}
	f.CombatPlan = domain.Known(true)
	d := EvaluateClockWindow(f, l)
	if !d.Admitted || d.Mode != ClockWindowCombat || !reflect.DeepEqual(d.Hostiles, []PawnID{"target"}) {
		t.Fatal(d)
	}
}
func TestBreakResponseSquadBoundsAndEligibility(t *testing.T) {
	p := breakingPawn("Berserk", true)
	at := domain.Known(domain.Cell{})
	candidate := func(id domain.PawnID, x int32) BreakResponder {
		return BreakResponder{SquadDefenderFacts: squadDefender(id, false), Cell: domain.Known(domain.Cell{X: x})}
	}
	near, second, far := candidate("near", 1), candidate("second", 2), candidate("far", 5)
	ranged := candidate("ranged", 0)
	ranged.RangedEquipped = domain.Known(true)
	injured := candidate("injured", 0)
	injured.NeedsTend = domain.Known(true)
	busy := candidate("busy", 0)
	busy.Drafted = domain.Known(true)
	busy.DraftOwned = domain.Known(true)
	unarmed := candidate("unarmed", 0)
	unarmed.Armed = domain.Known(false)
	unknown := candidate("unknown", 0)
	unknown.Cell = domain.Unknown[domain.Cell]()
	if got := SelectBreakSquad(p, at, []BreakResponder{far, ranged, injured, busy, unarmed, unknown, second, near}); !reflect.DeepEqual(got, []domain.PawnID{"near", "second"}) {
		t.Fatal(got)
	}
	if got := SelectBreakSquad(p, at, []BreakResponder{near}); len(got) != 1 {
		t.Fatal(got)
	}
	p.Downed = domain.Known(true)
	if got := SelectBreakSquad(p, at, []BreakResponder{near}); len(got) != 0 {
		t.Fatal("downed target must go to rescue", got)
	}
	if !InBreakRadius(domain.Cell{X: 8}, domain.Cell{}) || InBreakRadius(domain.Cell{X: 9}, domain.Cell{}) {
		t.Fatal("radius boundary")
	}
}

func TestSubdueAdmissionPreemptsOtherMedicalButNotResponderHealth(t *testing.T) {
	base := meleeRequest(t)
	m, _ := domain.NewSubdue("pawn", "target", "draft")
	a, _ := domain.NewMeleeAttackAction("attack", m)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{base.DraftProgress.Action(), a})
	if err != nil {
		t.Fatal(err)
	}
	base.Action = a
	base.Progress, _ = domain.NewProgress(plan, a.ID())
	victim := breakingPawn("Berserk", true)
	victim.Bleeding = domain.Known(true)
	base.Facts.Emergency.facts.Threats = nil
	base.Facts.Emergency.facts.Colonists = append(base.Facts.Emergency.facts.Colonists, victim)
	base.Facts.Target.MentalState = victim.MentalState
	base.Facts.Target.FreeColonist = domain.Known(true)
	base.Facts.Target.Hostile = domain.Known(false)
	if d := EvaluateMeleeDefense(base); !d.Admitted {
		t.Fatal(d)
	}
	for _, kind := range []string{"recovered", "noncolonist", "downed", "unhealthy"} {
		t.Run(kind, func(t *testing.T) {
			r := base
			switch kind {
			case "recovered":
				r.Facts.Target.MentalState = domain.Known(MentalState{DefName: "Wander_Sad"})
			case "noncolonist":
				r.Facts.Target.FreeColonist = domain.Known(false)
			case "downed":
				r.Facts.Target.Downed = domain.Known(true)
			case "unhealthy":
				r.Facts.Pawn.NeedsTend = domain.Known(true)
			}
			if EvaluateMeleeDefense(r).Admitted {
				t.Fatal("unsafe subdue admitted")
			}
		})
	}
}
