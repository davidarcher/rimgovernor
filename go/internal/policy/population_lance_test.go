package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func lanceRow(id domain.PawnID, downed, recruitable bool, skill int) CustodyFacts {
	return CustodyFacts{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(downed), Hostile: domain.Known(true), Prisoner: domain.Known(false),
		Guest: domain.Known(false), Admitted: domain.Known(false), Recruitable: domain.Known(recruitable),
		Prospect: domain.Known(PrisonerProspect{Skills: []PrisonerSkill{{Name: "Shooting", Level: skill}, {Name: "Crafting", Level: 20, Disabled: true}}})}
}

func lanceFacts(colonists int, worn bool, rows ...CustodyFacts) RoundsFacts {
	apparel := []GearApparel{{Definition: "Apparel_Parka"}}
	if worn {
		apparel = append(apparel, GearApparel{Definition: "Apparel_PsychicShockLance"})
	}
	return RoundsFacts{
		PrisonerColony: domain.Known(PrisonerColony{Colonists: colonists}),
		Custody:        domain.Known(rows),
		Gear:           domain.Known(GearObservation{Pawns: []GearPawn{{Pawn: "c1", Apparel: domain.Known(apparel)}}}),
	}
}

func TestLanceTargetPicksBestSkilledStandingRecruitable(t *testing.T) {
	f := lanceFacts(3, true,
		lanceRow("low", false, true, 4),
		lanceRow("downed", true, true, 19),
		lanceRow("stubborn", false, false, 18),
		lanceRow("best", false, true, 12),
		lanceRow("alsoBest", false, true, 12),
	)
	if got := LanceTarget(f); got != "alsoBest" {
		t.Fatalf("target = %q, want the best-skilled standing recruitable, lowest ID on a tie", got)
	}
	unknown := lanceRow("unknown", false, true, 20)
	unknown.Recruitable = domain.Unknown[bool]()
	if got := LanceTarget(lanceFacts(3, true, unknown)); got != "" {
		t.Fatalf("unknown recruitability chosen: %q", got)
	}
}

func TestLanceTargetNeedsDeficitAndWornLance(t *testing.T) {
	row := lanceRow("r", false, true, 5)
	if got := LanceTarget(lanceFacts(domain.PopulationTarget, true, row)); got != "" {
		t.Fatalf("at population target chose %q", got)
	}
	if got := LanceTarget(lanceFacts(3, false, row)); got != "" {
		t.Fatalf("no worn lance chose %q", got)
	}
	if got := LanceCandidate(lanceFacts(3, false, row)); got != "r" {
		t.Fatalf("candidate = %q; the planner's combat read finds the wearer", got)
	}
	f := lanceFacts(3, true, row)
	f.PrisonerColony = domain.Unknown[PrisonerColony]()
	if got := LanceTarget(f); got != "" {
		t.Fatalf("unknown colony size chose %q", got)
	}
}

func TestSelectLanceUseLowestIDWearer(t *testing.T) {
	choice, ok := SelectLanceUse("raider", []LanceUser{{Pawn: "c2", Item: "lance2"}, {Pawn: "c1", Item: "lance1"}})
	if !ok || choice != (LanceChoice{User: "c1", Target: "raider", Item: "lance1"}) {
		t.Fatal(choice, ok)
	}
	if _, ok := SelectLanceUse("", []LanceUser{{Pawn: "c1", Item: "l"}}); ok {
		t.Fatal("empty target chosen")
	}
	if _, ok := SelectLanceUse("raider", nil); ok {
		t.Fatal("no wearer chosen")
	}
}
