package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// turretFixture adds the turret request to the corridor fixture: a mini
// turret available, a network with spare watts whose one conduit sits in the
// yard behind the back wall, steel for two turrets and their conduits, and
// a known line of sight from both flank slots (x 11 and 19 on the shooters'
// row, three cells clear of the firing span x 14..16) to the kill zone.
func turretFixture() DefenseRequest {
	r := defenseFixture()
	r.UnitCosts["Turret_MiniTurret"] = []Amount{{Resource: "Steel", Count: 70}, {Resource: "ComponentIndustrial", Count: 3}}
	r.UnitCosts["PowerConduit"] = []Amount{{Resource: "Steel", Count: 1}}
	r.Turret = DefenseTurretRequest{Definition: "Turret_MiniTurret", Conduit: "PowerConduit", Available: domain.Known(true), DrawW: domain.Known(80.0), SpareW: domain.Known(200.0),
		Transmitters: cells(18, 28), Stock: domain.Known(map[Resource]int64{"Steel": 300, "ComponentIndustrial": 10}), Max: 2}
	for _, c := range flankSlots {
		r.Lines = append(r.Lines, DefenseLine{From: c, To: fixtureExit, LineOfSight: domain.Known(true)})
	}
	return r
}

var (
	flankSlots  = cells(11, 23, 19, 23)
	fixtureExit = domain.Cell{X: 18, Z: 19}
)

func turretCells(tier DefenseTier, definition string) []domain.Cell {
	var out []domain.Cell
	for _, b := range tier.Buildings {
		if b.Definition() == definition {
			out = append(out, b.Cell())
		}
	}
	return out
}

func TestDefenseTurretsFlankTheFiringLineAndReachTheNetwork(t *testing.T) {
	layout, err := DefenseLayouts(turretFixture())
	if err != nil {
		t.Fatal(err)
	}
	tier, ok := layout.Tier(TierTurrets)
	if !ok || len(layout.Tiers) != 5 {
		t.Fatal(layout.Tiers)
	}
	// Slots step outward from the firing span on the shooters' row to the
	// kill zone's side walls; the row behind is within chain-explosion
	// range of the front slots, so only these two remain.
	if !reflect.DeepEqual(layout.Turrets, []TurretPosition{{Cell: flankSlots[0], Verified: true}, {Cell: flankSlots[1], Verified: true}}) {
		t.Fatalf("%+v", layout.Turrets)
	}
	turrets := turretCells(tier, "Turret_MiniTurret")
	if !reflect.DeepEqual(turrets, flankSlots) || !reflect.DeepEqual(tier.Reserved, turrets) {
		t.Fatal(turrets, tier.Reserved)
	}
	for _, a := range turrets {
		for _, f := range layout.Firing {
			if chebyshev(a, f.Cell) < turretSpacing {
				t.Fatal("turret beside a shooter", a, f.Cell)
			}
		}
	}
	if chebyshev(turrets[0], turrets[1]) < turretSpacing {
		t.Fatal("turrets within chain-explosion range")
	}
	// The east turret (19,23) is within connector reach of the conduit at
	// (18,28); the west one needs a cardinal chain out of the walled kill
	// zone, under the back wall, ending beside the existing conduit and
	// never on a lane or a reserved cell other than the wall.
	chain := turretCells(tier, "PowerConduit")
	if len(chain) == 0 {
		t.Fatal("no conduit chain")
	}
	if chebyshev(chain[0], turrets[0]) > conduitReach {
		t.Fatal("chain does not start within reach of the turret", chain)
	}
	if last := chain[len(chain)-1]; chebyshev(last, domain.Cell{X: 18, Z: 28}) != 1 {
		t.Fatal("chain does not end beside the conduit", last)
	}
	for i := 1; i < len(chain); i++ {
		if a, b := chain[i-1], chain[i]; abs32(a.X-b.X)+abs32(a.Z-b.Z) != 1 {
			t.Fatal("chain is not cardinally connected", chain)
		}
	}
	funnel, _ := layout.Tier(TierFunnel)
	underWall := map[domain.Cell]bool{}
	for _, b := range funnel.Buildings {
		underWall[b.Cell()] = true
	}
	avoid := map[domain.Cell]bool{}
	for _, tr := range layout.Tiers {
		if tr.Name == TierTurrets {
			continue
		}
		for _, c := range tr.Reserved {
			avoid[c] = true
		}
		for _, b := range tr.Buildings {
			avoid[b.Cell()] = true
		}
	}
	crossed := false
	for _, c := range chain {
		crossed = crossed || underWall[c]
		if avoid[c] && !underWall[c] {
			t.Fatal("conduit on a lane or reserved cell", c)
		}
	}
	if !crossed {
		t.Fatal("chain never left the kill zone", chain)
	}
	firing, _ := layout.Probe()
	if !reflect.DeepEqual(firing, cells(15, 23, 14, 23, 16, 23, 11, 23, 19, 23)) {
		t.Fatal(firing)
	}
	if v, k := tier.Costs.Value(); !k || v[0] != (Amount{Resource: "ComponentIndustrial", Count: 6}) || v[1].Resource != "Steel" || v[1].Count != int64(140+len(chain)) {
		t.Fatal(tier.Costs)
	}
	// The same tier from a stored record's geometry.
	again, candidates, err := DefenseTurrets(turretFixture(), layout.Geometry())
	if err != nil || !reflect.DeepEqual(again, tier) || !reflect.DeepEqual(candidates, layout.Turrets) {
		t.Fatal(again, candidates, err)
	}
}

// A mini turret takes the shooters' row, a heavier rung the row behind it:
// the autocannon's minimum range wants it further back.
func TestDefenseTurretsHeavierRungsStandFurtherBack(t *testing.T) {
	r := turretFixture()
	r.UnitCosts[TurretAutocannon] = []Amount{{Resource: "Steel", Count: 100}, {Resource: "ComponentIndustrial", Count: 4}}
	r.Turret.Definition = TurretAutocannon
	r.Lines = nil
	for _, c := range cells(11, 24, 19, 24) {
		r.Lines = append(r.Lines, DefenseLine{From: c, To: fixtureExit, LineOfSight: domain.Known(true)})
	}
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(layout.Turrets, []TurretPosition{{Cell: domain.Cell{X: 11, Z: 24}, Verified: true}, {Cell: domain.Cell{X: 19, Z: 24}, Verified: true}}) {
		t.Fatalf("%+v", layout.Turrets)
	}
	mini, err := DefenseLayouts(turretFixture())
	if err != nil {
		t.Fatal(err)
	}
	d := directionOf(layout.Toward)
	depth := func(c domain.Cell) int32 { return c.X*d.X + c.Z*d.Z }
	for _, heavy := range layout.Turrets {
		for _, m := range mini.Turrets {
			if depth(heavy.Cell) <= depth(m.Cell) {
				t.Fatal("autocannon slot no further back than the mini turret's", heavy, m)
			}
		}
	}
}
func TestDefenseTurretsGateOnObservedResearchPowerAndStock(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	count := func(t *testing.T, r DefenseRequest) int {
		t.Helper()
		layout, err := DefenseLayouts(r)
		if err != nil {
			t.Fatal(err)
		}
		tier, _ := layout.Tier(TierTurrets)
		return len(turretCells(tier, "Turret_MiniTurret"))
	}
	for name, edit := range map[string]func(*DefenseRequest){
		"research unknown": func(r *DefenseRequest) { r.Turret.Available = domain.Unknown[bool]() },
		"not researched":   func(r *DefenseRequest) { r.Turret.Available = domain.Known(false) },
		"draw unknown":     func(r *DefenseRequest) { r.Turret.DrawW = domain.Unknown[float64]() },
		"spare unknown":    func(r *DefenseRequest) { r.Turret.SpareW = domain.Unknown[float64]() },
		"no spare watts":   func(r *DefenseRequest) { r.Turret.SpareW = domain.Known(79.0) },
		"stock unknown":    func(r *DefenseRequest) { r.Turret.Stock = domain.Unknown[map[Resource]int64]() },
		"no steel": func(r *DefenseRequest) {
			r.Turret.Stock = domain.Known(map[Resource]int64{"Steel": 69, "ComponentIndustrial": 10})
		},
		"no components":    func(r *DefenseRequest) { r.Turret.Stock = domain.Known(map[Resource]int64{"Steel": 300}) },
		"cost unknown":     func(r *DefenseRequest) { delete(r.UnitCosts, "Turret_MiniTurret") },
		"no transmitter":   func(r *DefenseRequest) { r.Turret.Transmitters = nil },
		"max zero":         func(r *DefenseRequest) { r.Turret.Max = 0 },
		"lines unverified": func(r *DefenseRequest) { r.Lines = nil },
		"no line of sight": func(r *DefenseRequest) {
			r.Lines[0].LineOfSight, r.Lines[1].LineOfSight = domain.Known(false), domain.Known(false)
		},
		"no firing line": func(r *DefenseRequest) { r.Defenders = 0 },
	} {
		r := turretFixture()
		edit(&r)
		if n := count(t, r); n != 0 {
			t.Fatal(name, "placed", n)
		}
	}
	r := turretFixture()
	r.Turret.SpareW = domain.Known(80.0)
	if n := count(t, r); n != 1 {
		t.Fatal("spare watts for one turret placed", n)
	}
	r = turretFixture()
	r.Turret.Stock = domain.Known(map[Resource]int64{"Steel": 139, "ComponentIndustrial": 10})
	if n := count(t, r); n != 1 {
		t.Fatal("steel for one turret placed", n)
	}
	// Conduits count against stock once routed: the west turret needs a
	// chain, so with steel for exactly one turret only the east one, already
	// within connector reach, survives the budget.
	r = turretFixture()
	r.Turret.Stock = domain.Known(map[Resource]int64{"Steel": 70, "ComponentIndustrial": 10})
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	tier, _ := layout.Tier(TierTurrets)
	if len(tier.Buildings) != 0 {
		// The first verified candidate (west) is routed first and cannot be
		// afforded with its chain; trimming drops it and nothing remains.
		t.Fatal("chain cost ignored", tier.Buildings)
	}
	r = turretFixture()
	r.Turret.Max = 1
	if n := count(t, r); n != 1 {
		t.Fatal("max ignored", n)
	}
	r = turretFixture()
	r.Turret.Definition = ""
	if layout, err := DefenseLayouts(r); err != nil || len(layout.Tiers) != 4 || len(layout.Turrets) != 0 {
		t.Fatal("tier without a request", layout.Tiers, err)
	}
}
func TestDefenseTurretsAvoidLanesReservedAndUnknownCells(t *testing.T) {
	r := turretFixture()
	r.Protected = cells(11, 23)
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual([]domain.Cell{layout.Turrets[0].Cell, layout.Turrets[1].Cell}, cells(19, 23, 11, 24)) {
		t.Fatalf("%+v", layout.Turrets)
	}
	r = turretFixture()
	for i := range r.Cells {
		if r.Cells[i].Cell == (domain.Cell{X: 19, Z: 23}) {
			r.Cells[i].Walkable = domain.Unknown[bool]()
		}
	}
	if layout, err = DefenseLayouts(r); err != nil {
		t.Fatal(err)
	}
	for _, c := range layout.Turrets {
		if c.Cell == (domain.Cell{X: 19, Z: 23}) {
			t.Fatal("unknown cell became a turret site")
		}
	}
	// Every candidate is a free, unreserved, off-lane cell inside the
	// kill zone's walls.
	layout, err = DefenseLayouts(turretFixture())
	if err != nil {
		t.Fatal(err)
	}
	lanes := map[domain.Cell]bool{}
	for _, c := range layout.TrapLane {
		lanes[c] = true
	}
	for _, c := range layout.Turrets {
		if lanes[c.Cell] || c.Cell.X <= 10 || c.Cell.X >= 20 {
			t.Fatal("turret off the kill zone", c)
		}
	}
}
func TestDefenseTurretsSeeTheKillZoneNotTheEntry(t *testing.T) {
	// The corridor's walls hide the entry from the kill zone: a slot that
	// sees the exit is verified; one whose every corridor line is known
	// blocked is skipped.
	plain, err := DefenseLayouts(defenseFixture())
	if err != nil {
		t.Fatal(err)
	}
	r := turretFixture()
	r.Lines = nil
	for _, c := range flankSlots {
		r.Lines = append(r.Lines, DefenseLine{From: c, To: domain.Cell{X: 15, Z: 5}, LineOfSight: domain.Known(false)})
	}
	r.Lines = append(r.Lines, DefenseLine{From: flankSlots[0], To: fixtureExit, LineOfSight: domain.Known(true)})
	for _, a := range plain.TrapLane {
		r.Lines = append(r.Lines, DefenseLine{From: flankSlots[1], To: a, LineOfSight: domain.Known(false)})
	}
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	tier, _ := layout.Tier(TierTurrets)
	if turrets := turretCells(tier, "Turret_MiniTurret"); !reflect.DeepEqual(turrets, flankSlots[:1]) {
		t.Fatal(turrets)
	}
	for _, c := range layout.Turrets {
		if c.Cell == flankSlots[1] {
			t.Fatal("blocked flank kept as a candidate", layout.Turrets)
		}
	}
	if g := layout.Geometry(); !reflect.DeepEqual(g.Approach, layout.TrapLane) {
		t.Fatal(g.Approach)
	}
}
