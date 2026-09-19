package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// turretFixture adds the turret request to the corridor fixture: a mini
// turret available, a network with spare watts whose one conduit sits in the
// home area's far corner, steel for two turrets and their conduits, a known
// line of sight from both flank candidates to the entry, and the row behind
// the firing line known blocked from every lane cell so the flanks are the
// verified sites.
func turretFixture() DefenseRequest {
	r := defenseFixture()
	r.UnitCosts["Turret_MiniTurret"] = []Amount{{Resource: "Steel", Count: 70}, {Resource: "ComponentIndustrial", Count: 3}}
	r.UnitCosts["PowerConduit"] = []Amount{{Resource: "Steel", Count: 1}}
	r.Turret = DefenseTurretRequest{Definition: "Turret_MiniTurret", Conduit: "PowerConduit", Available: domain.Known(true), DrawW: domain.Known(80.0), SpareW: domain.Known(200.0),
		Transmitters: cells(18, 28), Stock: domain.Known(map[Resource]int64{"Steel": 300, "ComponentIndustrial": 10}), Max: 2}
	entry := domain.Cell{X: 9, Z: 14}
	for _, c := range cells(5, 23, 13, 23) {
		r.Lines = append(r.Lines, DefenseLine{From: c, To: entry, LineOfSight: domain.Known(true)})
	}
	for _, c := range behindRow {
		for _, a := range laneCells {
			r.Lines = append(r.Lines, DefenseLine{From: c, To: a, LineOfSight: domain.Known(false)})
		}
	}
	return r
}

// behindRow is the candidate row three cells behind the shooters in line
// with the trap lane (x 9) and three cells to either side; laneCells is the
// fixture's trap lane, the kill zone the turrets must see.
var (
	behindRow = cells(9, 26, 6, 26, 12, 26)
	laneCells = cells(9, 14, 9, 15, 9, 16, 9, 17, 9, 18, 9, 19)
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
	// Candidates step outward from the firing span (x 8..10 on row 23) by
	// the spacing, so no turret is within two cells of a shooter or another
	// turret; the two flank positions are verified and taken.
	if !reflect.DeepEqual(layout.Turrets, []TurretPosition{{Cell: domain.Cell{X: 5, Z: 23}, Verified: true}, {Cell: domain.Cell{X: 13, Z: 23}, Verified: true}, {Cell: domain.Cell{X: 2, Z: 23}}, {Cell: domain.Cell{X: 16, Z: 23}}}) {
		t.Fatalf("%+v", layout.Turrets)
	}
	turrets := turretCells(tier, "Turret_MiniTurret")
	if !reflect.DeepEqual(turrets, cells(5, 23, 13, 23)) || !reflect.DeepEqual(tier.Reserved, turrets) {
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
	// The east turret (13,23) is within connector reach of the conduit at
	// (18,28); the west one needs a cardinal chain that ends within reach
	// and starts beside the existing conduit, never on a lane or a
	// reserved cell.
	chain := turretCells(tier, "PowerConduit")
	if len(chain) == 0 {
		t.Fatal("no conduit chain")
	}
	if chebyshev(chain[0], turrets[0]) > conduitReach || chebyshev(chain[1], turrets[0]) <= conduitReach && chebyshev(chain[0], turrets[0]) < chebyshev(chain[1], turrets[0]) {
		t.Fatal("chain does not start at the last cell within reach", chain)
	}
	last := chain[len(chain)-1]
	if d := chebyshev(last, domain.Cell{X: 18, Z: 28}); d != 1 || last.X != 18 && last.Z != 28 {
		t.Fatal("chain does not end beside the conduit", last)
	}
	for i := 1; i < len(chain); i++ {
		if a, b := chain[i-1], chain[i]; abs32(a.X-b.X)+abs32(a.Z-b.Z) != 1 {
			t.Fatal("chain is not cardinally connected", chain)
		}
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
	for _, c := range chain {
		if avoid[c] {
			t.Fatal("conduit on a lane or reserved cell", c)
		}
	}
	// Probe lists every candidate behind the firing cells.
	firing, _ := layout.Probe()
	if !reflect.DeepEqual(firing, cells(9, 23, 8, 23, 10, 23, 5, 23, 13, 23, 2, 23, 16, 23)) {
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
func TestDefenseTurretsPreferTheRowBehindTheFiringLine(t *testing.T) {
	// A candidate behind the shooters that sees the lane comes before the
	// flanks: the lane-aligned cell first, then the cells three to either
	// side, each verified only by a known line to a lane cell. The probe
	// runs to the hard cap, so both flanks follow.
	r := turretFixture()
	r.Lines = nil
	for _, c := range cells(5, 23, 13, 23) {
		r.Lines = append(r.Lines, DefenseLine{From: c, To: domain.Cell{X: 9, Z: 14}, LineOfSight: domain.Known(true)})
	}
	for _, c := range behindRow {
		r.Lines = append(r.Lines, DefenseLine{From: c, To: domain.Cell{X: 9, Z: 14}, LineOfSight: domain.Known(false)})
	}
	r.Lines = append(r.Lines, DefenseLine{From: domain.Cell{X: 9, Z: 26}, To: domain.Cell{X: 9, Z: 15}, LineOfSight: domain.Known(true)})
	r.Lines = append(r.Lines, DefenseLine{From: domain.Cell{X: 12, Z: 26}, To: domain.Cell{X: 9, Z: 17}, LineOfSight: domain.Known(true)})
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(layout.Turrets, []TurretPosition{{Cell: domain.Cell{X: 9, Z: 26}, Verified: true}, {Cell: domain.Cell{X: 6, Z: 26}}, {Cell: domain.Cell{X: 12, Z: 26}, Verified: true}, {Cell: domain.Cell{X: 5, Z: 23}, Verified: true}, {Cell: domain.Cell{X: 13, Z: 23}, Verified: true}, {Cell: domain.Cell{X: 2, Z: 23}}}) {
		t.Fatalf("%+v", layout.Turrets)
	}
	tier, _ := layout.Tier(TierTurrets)
	turrets := turretCells(tier, "Turret_MiniTurret")
	if !reflect.DeepEqual(turrets, cells(9, 26, 12, 26)) {
		t.Fatal(turrets)
	}
	for _, c := range turrets {
		for _, f := range layout.Firing {
			if chebyshev(c, f.Cell) < turretSpacing {
				t.Fatal("turret beside a shooter", c, f.Cell)
			}
		}
		for _, tr := range layout.Tiers {
			if tr.Name == TierTurrets {
				continue
			}
			for _, res := range tr.Reserved {
				if res == c {
					t.Fatal("turret on a reserved cell", c)
				}
			}
		}
	}
	// The probe asks for the behind-row lines before the flanks.
	firing, _ := layout.Probe()
	if !reflect.DeepEqual(firing, cells(9, 23, 8, 23, 10, 23, 9, 26, 6, 26, 12, 26, 5, 23, 13, 23, 2, 23)) {
		t.Fatal(firing)
	}
}
func TestDefenseTurretsGateOnObservedResearchPowerAndStock(t *testing.T) {
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
	r.Protected = cells(5, 23)
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(layout.Turrets[0].Cell, domain.Cell{X: 13, Z: 23}) || layout.Turrets[1].Cell != (domain.Cell{X: 2, Z: 23}) {
		t.Fatalf("%+v", layout.Turrets)
	}
	r = turretFixture()
	for i := range r.Cells {
		if r.Cells[i].Cell == (domain.Cell{X: 13, Z: 23}) {
			r.Cells[i].Walkable = domain.Unknown[bool]()
		}
	}
	if layout, err = DefenseLayouts(r); err != nil {
		t.Fatal(err)
	}
	for _, c := range layout.Turrets {
		if c.Cell == (domain.Cell{X: 13, Z: 23}) {
			t.Fatal("unknown cell became a turret site")
		}
	}
	// A geometry whose firing row runs into the lanes places nothing on
	// them: every candidate is a free, unreserved, off-lane cell.
	layout, err = DefenseLayouts(turretFixture())
	if err != nil {
		t.Fatal(err)
	}
	lanes := map[domain.Cell]bool{}
	for _, c := range append(append([]domain.Cell{}, layout.TrapLane...), layout.SafeLane...) {
		lanes[c] = true
	}
	for _, c := range layout.Turrets {
		if lanes[c.Cell] {
			t.Fatal("turret on a lane", c)
		}
	}
	r = turretFixture()
	r.Turret.Max = 65
	if _, err = DefenseLayouts(r); err == nil {
		t.Fatal("turret bound")
	}
}
func TestDefenseTurretsSeeTheKillZoneNotTheEntry(t *testing.T) {
	// The funnel walls hide the entry from the flanks: a candidate whose
	// line to the entry is blocked but which sees a lane cell is verified;
	// one whose every lane line is known blocked is skipped.
	r := turretFixture()
	r.Lines = nil
	entry := domain.Cell{X: 9, Z: 14}
	for _, c := range cells(5, 23, 13, 23) {
		r.Lines = append(r.Lines, DefenseLine{From: c, To: entry, LineOfSight: domain.Known(false)})
	}
	r.Lines = append(r.Lines, DefenseLine{From: domain.Cell{X: 5, Z: 23}, To: domain.Cell{X: 9, Z: 19}, LineOfSight: domain.Known(true)})
	for _, z := range []int32{15, 16, 17, 18, 19} {
		r.Lines = append(r.Lines, DefenseLine{From: domain.Cell{X: 13, Z: 23}, To: domain.Cell{X: 9, Z: z}, LineOfSight: domain.Known(false)})
	}
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	tier, _ := layout.Tier(TierTurrets)
	if turrets := turretCells(tier, "Turret_MiniTurret"); !reflect.DeepEqual(turrets, cells(5, 23)) {
		t.Fatal(turrets)
	}
	for _, c := range layout.Turrets {
		if c.Cell == (domain.Cell{X: 13, Z: 23}) {
			t.Fatal("blocked flank kept as a candidate", layout.Turrets)
		}
	}
	if g := layout.Geometry(); !reflect.DeepEqual(g.Approach, layout.TrapLane) {
		t.Fatal(g.Approach)
	}
}
