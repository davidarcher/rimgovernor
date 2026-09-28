package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// prisonPlan is a ring (interior 10..89) holding a prison whose door opens
// south onto (22,19), and a bedroom beside it.
func prisonPlan() LayoutPlan {
	return LayoutPlan{
		Rooms: []LayoutRoom{
			{Role: ModulePrison, Interior: Rectangle{X: 20, Z: 21, Width: 5, Height: 5}, Door: domain.Cell{X: 22, Z: 20}, DoorRot: domain.South},
			{Role: ModuleBedroom, Interior: Rectangle{X: 26, Z: 21, Width: 4, Height: 4}, Door: domain.Cell{X: 27, Z: 20}, DoorRot: domain.South},
		},
		Reservations: []LayoutReservation{
			{Kind: ReservePerimeter, Area: Rectangle{X: 7, Z: 7, Width: 86, Height: 3}},
			{Kind: ReservePerimeter, Area: Rectangle{X: 7, Z: 90, Width: 86, Height: 3}},
			{Kind: ReservePerimeter, Area: Rectangle{X: 7, Z: 10, Width: 3, Height: 80}},
			{Kind: ReservePerimeter, Area: Rectangle{X: 90, Z: 10, Width: 3, Height: 80}},
		},
	}
}

func TestPrisonTurrets(t *testing.T) {
	p := prisonPlan()
	sites := PrisonTurretSites(p)
	want := map[domain.Cell]bool{{X: 23, Z: 19}: true, {X: 21, Z: 19}: true}
	if len(sites) != 2 {
		t.Fatal("sites", sites)
	}
	for _, c := range sites {
		if !want[c] {
			t.Fatal("not beside the door's front cell", c)
		}
	}
	// A second prison adds one more: three at most.
	p.Rooms = append(p.Rooms, LayoutRoom{Role: ModulePrison, Interior: Rectangle{X: 40, Z: 21, Width: 5, Height: 5}, Door: domain.Cell{X: 42, Z: 20}, DoorRot: domain.South})
	if n := len(PrisonTurretSites(p)); n != prisonTurretMax {
		t.Fatal("cap", n)
	}
	// Sections: the turret, then conduits to the network.
	p = prisonPlan()
	if none, _ := PerimeterPrisonTurrets(p, "Turret_MiniTurret", "Steel", "HiddenConduit", nil); len(none) != 0 {
		t.Fatal("dark turrets planned")
	}
	transmitter := domain.Cell{X: 22, Z: 40}
	sections, err := PerimeterPrisonTurrets(p, "Turret_MiniTurret", "Steel", "HiddenConduit", []domain.Cell{transmitter})
	if err != nil || len(sections) != 2 {
		t.Fatal(sections, err)
	}
	net := map[domain.Cell]bool{transmitter: true}
	for _, s := range sections {
		turret := s.Buildings[0]
		if turret.Definition() != "Turret_MiniTurret" || !want[turret.Cell()] || turret.Stuff() != "Steel" {
			t.Fatal("section without its turret", s.Name, turret)
		}
		for _, b := range s.Buildings[1:] {
			net[b.Cell()] = true
		}
		reach := false
		for c := range net {
			reach = reach || chebyshev(c, turret.Cell()) <= conduitReach
		}
		if !reach {
			t.Fatal("turret out of connector reach", turret.Cell())
		}
	}
}

func TestNoWeaponsNearPrison(t *testing.T) {
	prisons := PrisonCells(prisonPlan())
	if !nearPrison([]domain.Cell{{X: 30, Z: 23}}, prisons) {
		t.Fatal("a cell six from the prison wall is near")
	}
	if nearPrison([]domain.Cell{{X: 32, Z: 23}}, prisons) {
		t.Fatal("a cell seven from the wall is clear")
	}
	// The creation pass moves the weapons stockpile off the site it takes
	// without a prison once a prison stands there; apparel keeps it.
	r := stockpileCreateRequest()
	r.Needs = map[string]int{domain.WeaponsRole: 3}
	site := func(r StockpileRequest, role string) []domain.Cell {
		for _, e := range PlanStockpileMaintenance(r).Edits {
			if e.Kind == StockpileCreate && e.Role == role {
				return e.Cells
			}
		}
		return nil
	}
	free := site(r, domain.WeaponsRole)
	if len(free) == 0 {
		t.Fatal("no weapons stockpile without a prison")
	}
	r.Prisons = []domain.Cell{free[0]}
	if moved := site(r, domain.WeaponsRole); nearPrison(moved, r.Prisons) {
		t.Fatal("weapons stockpile beside the prison", moved)
	}
	r.Needs = map[string]int{domain.ApparelRole: 3}
	if len(site(r, domain.ApparelRole)) == 0 {
		t.Fatal("apparel refused near the prison")
	}
}
