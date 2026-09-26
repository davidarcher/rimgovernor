package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func perimeterPlan(t *testing.T, cell func(x, z int32) SurveyCell) LayoutPlan {
	t.Helper()
	s := zoningSurvey(200, cell)
	p := PlanPerimeter(PlanCore(Zone(s), 3), s)
	if !p.Valid() {
		t.Fatal("invalid plan")
	}
	return p
}

func reserved(p LayoutPlan, kind ReservationKind) []Rectangle {
	var out []Rectangle
	for _, r := range p.Reservations {
		if r.Kind == kind {
			out = append(out, r.Area)
		}
	}
	return out
}

func checkPerimeter(t *testing.T, p LayoutPlan) (killbox Rectangle) {
	t.Helper()
	walls, gates := reserved(p, ReservePerimeter), reserved(p, ReserveGate)
	if len(walls) == 0 || len(gates) == 0 {
		t.Fatal("walls", walls, "gates", gates)
	}
	for _, g := range gates {
		if g.Width*g.Height != 3 {
			t.Fatal("gate is 3 doors", g)
		}
		in := false
		for _, w := range walls {
			in = in || contains(w, domain.Cell{X: g.X, Z: g.Z}) && contains(w, domain.Cell{X: g.X + g.Width - 1, Z: g.Z + g.Height - 1})
		}
		if !in {
			t.Fatal("gate off the wall", g)
		}
	}
	kb := reserved(p, ReserveKillbox)
	turrets := reserved(p, ReserveTurret)
	if len(kb) != 1 || len(turrets) != 3 || len(reserved(p, ReserveKillboxApproach)) != 2 {
		t.Fatal("killbox", kb, turrets)
	}
	for i, a := range turrets {
		if !contains(kb[0], domain.Cell{X: a.X, Z: a.Z}) {
			t.Fatal("turret outside killbox", a)
		}
		for _, b := range turrets[i+1:] {
			if max(a.X-b.X, b.X-a.X, a.Z-b.Z, b.Z-a.Z) < 3 {
				t.Fatal("turrets too close", a, b)
			}
		}
	}
	if len(reserved(p, ReserveCoverClear)) == 0 {
		t.Fatal("no cover band")
	}
	m := reserved(p, ReserveMortar)
	if len(m) != 1 {
		t.Fatal("mortar", m)
	}
	for _, r := range p.Rooms {
		if contains(pad(r.Interior, 1), domain.Cell{X: m[0].X, Z: m[0].Z}) {
			t.Fatal("mortar in a room")
		}
	}
	return kb[0]
}

func TestPerimeterOpenPlains(t *testing.T) {
	p := perimeterPlan(t, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	checkPerimeter(t, p)
	// Replanning replaces, never duplicates.
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	if again := PlanPerimeter(p, s); len(again.Reservations) != len(p.Reservations) {
		t.Fatal("replan duplicated")
	}
}

func TestPerimeterMountainSealsFlank(t *testing.T) {
	p := perimeterPlan(t, func(x, z int32) SurveyCell {
		if x >= 120 {
			return SurveyCell{Rock: true}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	})
	checkPerimeter(t, p)
	for _, w := range reserved(p, ReservePerimeter) {
		if w.X+w.Width > 120 {
			t.Fatal("wall on rock", w)
		}
	}
}

func TestPerimeterKillboxWhereApproachesConverge(t *testing.T) {
	// A walled valley with its only mouth to the south, x 95..105.
	p := perimeterPlan(t, func(x, z int32) SurveyCell {
		open := x >= 30 && x < 170 && z >= 40 && z < 170 || x >= 95 && x <= 105 && z < 40
		if open {
			return SurveyCell{Walkable: true, Fertility: 1}
		}
		return SurveyCell{Rock: true}
	})
	kb := checkPerimeter(t, p)
	if c := kb.X + kb.Width/2; c < 90 || c > 110 {
		t.Fatal("killbox off the mouth", kb)
	}
	app := reserved(p, ReserveKillboxApproach)
	if app[0].Z >= kb.Z {
		t.Fatal("approach not outside the south wall", app, kb)
	}
}
