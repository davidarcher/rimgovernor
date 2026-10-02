package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestTurretRungsFollowTheArmoryTier(t *testing.T) {
	t.Parallel()
	for tier, want := range map[ArmoryTier][]string{
		ArmoryTierUnknown:     {TurretMini},
		ArmoryTierNeolithic:   {TurretMini},
		ArmoryTierSmithing:    {TurretMini},
		ArmoryTierMachining:   {TurretAutocannon, TurretMini},
		ArmoryTierFabrication: {TurretSniper, TurretAutocannon, TurretMini},
	} {
		if got := TurretRungs(tier); !reflect.DeepEqual(got, want) {
			t.Errorf("%v: %v want %v", tier, got, want)
		}
	}
	if TurretRank("Turret_FoamTurret") != -1 || TurretRank(TurretSniper) != 2 {
		t.Fatal("foam turret is off the ladder (#1228)")
	}
}

// autocannonFixture asks the turret fixture for the 2x2 autocannon, with a
// known line to the kill zone from every cell behind the fence bar.
func autocannonFixture() DefenseRequest {
	r := turretFixture()
	r.UnitCosts[TurretAutocannon] = []Amount{{Resource: "Steel", Count: 100}, {Resource: "ComponentIndustrial", Count: 4}}
	r.Turret.Definition, r.Turret.Size = TurretAutocannon, Bounds{Width: 2, Height: 2}
	for x := int32(0); x < 31; x++ {
		for z := int32(22); z < 29; z++ {
			r.Lines = append(r.Lines, DefenseLine{From: domain.Cell{X: x, Z: z}, To: fixtureExit, LineOfSight: domain.Known(true)})
		}
	}
	return r
}

func TestDefenseTurretsSiteTheWholeFootprint(t *testing.T) {
	t.Parallel()
	r := autocannonFixture()
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	tier, ok := layout.Tier(TierTurrets)
	turrets := turretCells(tier, TurretAutocannon)
	if !ok || len(turrets) == 0 {
		t.Fatal(layout.Tiers)
	}
	// The kill zone widens for the 2x2 footprint (columns -5..5): each
	// slot is the first whose footprint keeps three cells from the
	// shooters (x 14..16) edge to edge; the back row's footprints would
	// reach into the back wall.
	if !reflect.DeepEqual(turrets, cells(10, 23, 19, 23)) {
		t.Fatal(turrets)
	}
	s, _ := newDefenseSite(r)
	var fps [][]domain.Cell
	var reserved []domain.Cell
	for _, a := range turrets {
		fp := TurretFootprint(a, r.Turret.Size)
		if len(fp) != 4 {
			t.Fatal(fp)
		}
		for _, c := range fp {
			if !s.free(c) {
				t.Fatal("footprint on occupied ground", c)
			}
		}
		for _, f := range layout.Firing {
			if footprintGap(fp, []domain.Cell{f.Cell}) < turretSpacing {
				t.Fatal("footprint beside a shooter", a, f.Cell)
			}
		}
		for _, o := range fps {
			if footprintGap(fp, o) < turretSpacing {
				t.Fatal("footprints within chain-explosion range", a)
			}
		}
		fps = append(fps, fp)
		reserved = append(reserved, fp...)
	}
	if !reflect.DeepEqual(tier.Reserved, reserved) {
		t.Fatal(tier.Reserved, reserved)
	}
	for _, c := range turretCells(tier, "PowerConduit") {
		if slices.Contains(reserved, c) {
			t.Fatal("conduit under a turret", c)
		}
	}
}

func TestTurretReplacementRebuildsOneInPlace(t *testing.T) {
	t.Parallel()
	// Four mini turrets widen the kill zone to columns -6..6.
	minis := turretFixture()
	minis.Turret.Max = 4
	layout, err := DefenseLayouts(minis)
	if err != nil {
		t.Fatal(err)
	}
	g := layout.Geometry()
	r := autocannonFixture()
	r.Turret.Max = 2
	mini := func(c domain.Cell) StandingTurret {
		b, _ := domain.NewBuilding(TurretMini, c, domain.North, "")
		return StandingTurret{Building: b, Cells: []domain.Cell{c}}
	}
	west, east := domain.Cell{X: 11, Z: 23}, domain.Cell{X: 19, Z: 23}
	standing := []StandingTurret{mini(west), mini(east)}
	old, next, ok, err := TurretReplacement(r, g, standing)
	// The west footprint would crowd the firing line; the east one fits.
	if err != nil || !ok || old.Cell() != east || old.Definition() != TurretMini || next.Cell() != east || next.Definition() != TurretAutocannon {
		t.Fatal(old, next, ok, err)
	}
	// A turret already at the rung is never replaced.
	b, _ := domain.NewBuilding(TurretAutocannon, east, domain.North, "")
	if _, _, ok, _ := TurretReplacement(r, g, []StandingTurret{mini(west), {Building: b, Cells: TurretFootprint(east, r.Turret.Size)}}); ok {
		t.Fatal("replaced a turret at the rung")
	}
	// Closed gates replace nothing: no stock, no spare watts, the bottom rung.
	broke := r
	broke.Turret.Stock = domain.Known(map[Resource]int64{})
	dark := r
	dark.Turret.SpareW = domain.Known(10.0)
	bottom := turretFixture()
	for _, q := range []DefenseRequest{broke, dark, bottom} {
		if _, _, ok, _ := TurretReplacement(q, g, standing); ok {
			t.Fatal("replaced behind a closed gate", q.Turret)
		}
	}
}

func TestDefenseRearmTurretsCoversLadderTurrets(t *testing.T) {
	t.Parallel()
	for _, def := range []string{TurretAutocannon, TurretSniper} {
		c := domain.Cell{X: 10, Z: 10}
		turrets := []DefenseTurretFacts{{ID: def + "1", Definition: def, Cell: c, Powered: domain.Known(true), OutOfFuel: domain.Known(true), Fuel: domain.Known(0.0), TargetFuel: domain.Known(80.0), FuelDefinitions: []Resource{"Steel"}}}
		got := DefenseRearmTurrets(turrets, []WorkPawn{rearmWorker("keen", true, 1, false)}, domain.Known(map[Resource]int64{"Steel": 120}), false)
		if !reflect.DeepEqual(got.Rearm, []DefenseRearm{{Turret: def + "1", Cell: c, Pawn: "keen", Fuel: "Steel"}}) {
			t.Fatalf("%s: %+v", def, got)
		}
	}
}
