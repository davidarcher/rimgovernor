package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A prey that outranges the gunners' weapons keeps them out of the squad, so
// below the minimum nothing forms.
func TestHuntSkipsGunnersOutrangedByPrey(t *testing.T) {
	view := huntView(wildGroup()...)
	setRange := func(id domain.PawnID, r float64) {
		for i := range view.Pawns {
			if view.Pawns[i].ID == id {
				view.Pawns[i].WeaponRange = r
			}
		}
	}
	setRange("p2", 30)
	if roles := huntFormation(view); len(roles) != 3 {
		t.Fatalf("equal range: %+v", roles)
	}
	setRange("p2", 40)
	if roles := huntFormation(view); roles != nil {
		t.Fatalf("outranged: %+v", roles)
	}
}

// Sleeping prey make the squad channel cheaper; it is still formed by day.
func TestSquadHuntSleepingPreyIsCheaperNotGated(t *testing.T) {
	mk := func(sleeping bool) []AcquisitionSource {
		rows := []AcquisitionSource{preyRow("a", 1, 1, 0.05), preyRow("b", 2, 2, 0.05), preyRow("c", 3, 3, 0.05)}
		for i := range rows {
			rows[i].Sleeping = sleeping
		}
		return rows
	}
	day, _ := SquadHunts(mk(false), SquadHuntMinGunners, domain.Fact[float64]{})
	night, _ := SquadHunts(mk(true), SquadHuntMinGunners, domain.Fact[float64]{})
	if len(day) != 1 || len(night) != 1 {
		t.Fatalf("day %v night %v", day, night)
	}
	d, _ := day[0].WorkPerDay.Value()
	n, _ := night[0].WorkPerDay.Value()
	if !(n < d) {
		t.Fatalf("work day %v night %v", d, n)
	}
}

// Rain, snow and fog make a squad hunt mildly dearer; clear, unlisted and
// unknown weather leave it unchanged, and nothing is gated.
func TestSquadHuntBadWeatherIsCostlierNotGated(t *testing.T) {
	rows := []AcquisitionSource{preyRow("a", 1, 1, 0.05), preyRow("b", 2, 2, 0.05), preyRow("c", 3, 3, 0.05)}
	work := func(w domain.Fact[float64]) float64 {
		channels, _ := SquadHunts(rows, SquadHuntMinGunners, w)
		if len(channels) != 1 {
			t.Fatalf("weather %v: channels %v", w, channels)
		}
		v, _ := channels[0].WorkPerDay.Value()
		return v
	}
	base := work(domain.Fact[float64]{})
	if got := work(domain.Known(1.0)); got != base {
		t.Fatalf("clear %v base %v", got, base)
	}
	rain, fog := work(domain.Known(0.8)), work(domain.Known(0.5))
	if !(base < rain && rain < fog && fog < 2*base) {
		t.Fatalf("base %v rain %v fog %v", base, rain, fog)
	}
}
