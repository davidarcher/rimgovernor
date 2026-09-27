package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func eligibleRepairer(pawn domain.PawnID) RepairCandidateFacts {
	return RepairCandidateFacts{Pawn: pawn, Dead: knownFalse(), Downed: knownFalse(), Drafted: knownFalse(),
		MentalState: knownFalse(), PlayerForced: knownFalse(), NeedsTend: knownFalse(), Bleeding: knownFalse(),
		ConstructionEnabled: knownTrue()}
}

func TestSelectRepairPicksTopStructureAndLowestEligiblePawn(t *testing.T) {
	structures := []UpkeepStructure{{ID: "wall"}, {ID: "door"}}
	pawns := []RepairCandidateFacts{eligibleRepairer("z"), eligibleRepairer("a")}
	structure, pawn, ok := SelectRepair(structures, pawns)
	if !ok || structure.ID != "wall" || pawn != "a" {
		t.Fatal(structure, pawn, ok)
	}
}

func TestSelectRepairExcludesIneligiblePawns(t *testing.T) {
	structures := []UpkeepStructure{{ID: "wall"}}
	base := eligibleRepairer("a")
	for _, mutate := range []func(*RepairCandidateFacts){
		func(p *RepairCandidateFacts) { p.Dead = knownTrue() },
		func(p *RepairCandidateFacts) { p.Downed = knownTrue() },
		func(p *RepairCandidateFacts) { p.Drafted = knownTrue() },
		func(p *RepairCandidateFacts) { p.MentalState = knownTrue() },
		func(p *RepairCandidateFacts) { p.NeedsTend = knownTrue() },
		func(p *RepairCandidateFacts) { p.Bleeding = knownTrue() },
		func(p *RepairCandidateFacts) { p.ConstructionEnabled = knownFalse() },
		func(p *RepairCandidateFacts) { p.ConstructionEnabled = domain.Unknown[bool]() },
	} {
		p := base
		mutate(&p)
		if _, _, ok := SelectRepair(structures, []RepairCandidateFacts{p}); ok {
			t.Fatal("ineligible repairer selected", p)
		}
	}
}

func TestSelectRepairRequiresStructuresAndPawns(t *testing.T) {
	if _, _, ok := SelectRepair(nil, []RepairCandidateFacts{eligibleRepairer("a")}); ok {
		t.Fatal("selected with no structures")
	}
	if _, _, ok := SelectRepair([]UpkeepStructure{{ID: "wall"}}, nil); ok {
		t.Fatal("selected with no pawns")
	}
}

func TestSelectRepairUsesForcedPawnAsFallback(t *testing.T) {
	forced, idle := eligibleRepairer("a"), eligibleRepairer("z")
	forced.PlayerForced = domain.Known(true)
	structures := []UpkeepStructure{{ID: "wall"}}
	if _, pawn, ok := SelectRepair(structures, []RepairCandidateFacts{forced, idle}); !ok || pawn != idle.Pawn {
		t.Fatal(pawn, ok)
	}
	if _, pawn, ok := SelectRepair(structures, []RepairCandidateFacts{forced}); !ok || pawn != forced.Pawn {
		t.Fatal(pawn, ok)
	}
}
