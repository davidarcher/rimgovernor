package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func eligibleCleaner(pawn domain.PawnID) CleanCandidateFacts {
	return CleanCandidateFacts{Pawn: pawn, Dead: knownFalse(), Downed: knownFalse(), Drafted: knownFalse(),
		MentalState: knownFalse(), PlayerForced: knownFalse(), NeedsTend: knownFalse(), Bleeding: knownFalse(),
		CleaningEnabled: knownTrue()}
}

func TestSelectCleanPicksTopFilthAndLowestEligiblePawn(t *testing.T) {
	filth := []UpkeepFilth{{ID: "dirt"}, {ID: "mud"}}
	pawns := []CleanCandidateFacts{eligibleCleaner("z"), eligibleCleaner("a")}
	target, pawn, ok := SelectClean(filth, pawns)
	if !ok || target.ID != "dirt" || pawn != "a" {
		t.Fatal(target, pawn, ok)
	}
}

func TestSelectCleanExcludesIneligiblePawns(t *testing.T) {
	filth := []UpkeepFilth{{ID: "dirt"}}
	base := eligibleCleaner("a")
	for _, mutate := range []func(*CleanCandidateFacts){
		func(p *CleanCandidateFacts) { p.Dead = knownTrue() },
		func(p *CleanCandidateFacts) { p.Downed = knownTrue() },
		func(p *CleanCandidateFacts) { p.Drafted = knownTrue() },
		func(p *CleanCandidateFacts) { p.MentalState = knownTrue() },
		func(p *CleanCandidateFacts) { p.PlayerForced = knownTrue() },
		func(p *CleanCandidateFacts) { p.NeedsTend = knownTrue() },
		func(p *CleanCandidateFacts) { p.Bleeding = knownTrue() },
		func(p *CleanCandidateFacts) { p.CleaningEnabled = knownFalse() },
		func(p *CleanCandidateFacts) { p.CleaningEnabled = domain.Unknown[bool]() },
	} {
		p := base
		mutate(&p)
		if _, _, ok := SelectClean(filth, []CleanCandidateFacts{p}); ok {
			t.Fatal("ineligible cleaner selected", p)
		}
	}
}

func TestSelectCleanRequiresFilthAndPawns(t *testing.T) {
	if _, _, ok := SelectClean(nil, []CleanCandidateFacts{eligibleCleaner("a")}); ok {
		t.Fatal("selected with no filth")
	}
	if _, _, ok := SelectClean([]UpkeepFilth{{ID: "dirt"}}, nil); ok {
		t.Fatal("selected with no pawns")
	}
}

func knownTrue() domain.Fact[bool]  { return domain.Known(true) }
func knownFalse() domain.Fact[bool] { return domain.Known(false) }
