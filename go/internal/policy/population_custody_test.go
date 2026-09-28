package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func downedRaider(id domain.PawnID, recruitable bool) CustodyFacts {
	f := domain.Known(false)
	return CustodyFacts{
		Pawn: id, Dead: f, Downed: domain.Known(true), Guest: f, Admitted: f, Prisoner: f,
		Hostile: domain.Known(true), Recruitable: domain.Known(recruitable), WearingApparel: f,
	}
}

func TestSelectCustodyMethodPrefersRecruitableCapture(t *testing.T) {
	for _, tc := range []struct {
		name string
		rows []CustodyFacts
		want domain.PawnID
	}{
		{"recruitable over lower-ID unrecruitable", []CustodyFacts{downedRaider("1", false), downedRaider("5", true)}, "5"},
		{"tie falls back to lowest ID", []CustodyFacts{downedRaider("7", true), downedRaider("3", true)}, "3"},
		{"lone unrecruitable still captured", []CustodyFacts{downedRaider("2", false)}, "2"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got := SelectCustodyMethod(domain.Known(tc.rows))
			if got.Pawn != tc.want || got.Decision != CustodyCapture {
				t.Fatalf("got %+v, want capture of %v", got, tc.want)
			}
		})
	}
}
