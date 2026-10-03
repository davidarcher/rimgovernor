package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestProfileChildStaysOffWorkBelowMinAge (#1682): a child is forbidden each
// work type whose catalog minimum age exceeds their age, and no other.
func TestProfileChildStaysOffWorkBelowMinAge(t *testing.T) {
	ages := map[WorkType]int{"Hauling": 3, "Cleaning": 5, "Cooking": 10}
	pawn := WorkPawn{Age: domain.Known(5.0), Biotech: domain.Known(PawnBiotech{
		DevelopmentalStage: domain.Known("Child"), WorkMinAges: domain.Known(ages)})}
	p := BuildProfile(pawn)
	if p.Forbidden("Hauling") || p.Forbidden("Cleaning") || !p.Forbidden("Cooking") || p.Forbidden("Growing") {
		t.Fatalf("forbidden = %v", p.ForbiddenWork())
	}
	if got := p.ForbiddenWork(); len(got) != 1 || got[0] != "Cooking" {
		t.Fatalf("forbidden work = %v", got)
	}
	pawn.Age = domain.Known(10.0)
	if got := BuildProfile(pawn).ForbiddenWork(); len(got) != 0 {
		t.Fatalf("a ten-year-old is forbidden %v", got)
	}
}
