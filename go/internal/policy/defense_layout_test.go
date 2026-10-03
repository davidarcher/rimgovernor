package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// defenseFixture is a 31x30 census: open ground outside (z 0..4), a
// 3-thick rock ring (z 5..7) with a 3-wide opening (x 14..16) and a colony
// door at (2,6), and an open enclosure behind it (z 8..28, x 1..29) closed
// by rock at z 29.
func defenseFixture() DefenseRequest {
	r := DefenseRequest{
		Bounds:      Bounds{Width: 250, Height: 250},
		Region:      Rectangle{X: 0, Z: 0, Width: 31, Height: 30},
		Home:        domain.Cell{X: 15, Z: 27},
		Entrances:   []domain.Cell{{X: 15, Z: 28}},
		Definitions: DefenseDefinitions{Sandbag: "Sandbags", Wall: "Wall", WallStuff: "BlocksGranite", Fence: "Fence", FenceStuff: "WoodLog", Trap: "TrapSpike", TrapStuff: "WoodLog", Door: "Door", DoorStuff: "WoodLog", Floor: "WoodPlankFloor"},
		MinRange:    domain.Known(25.9),
		Defenders:   3,
		Killbox:     DefenseKillbox{Entry: domain.Cell{X: 15, Z: 5}, Toward: domain.North, Width: 3, Depth: killboxRows},
		UnitCosts: map[string][]Amount{
			"Sandbags": {{Resource: "Cloth", Count: 5}}, "Wall": {{Resource: "BlocksGranite", Count: 5}},
			"Fence": {{Resource: "WoodLog", Count: 2}}, "TrapSpike": {{Resource: "WoodLog", Count: 45}}, "Door": {{Resource: "WoodLog", Count: 25}},
		},
	}
	for x := int32(0); x < 31; x++ {
		for z := int32(0); z < 30; z++ {
			gate := x == 2 && z == 6
			open := z < 5 || z <= 7 && (x >= 14 && x <= 16 || x == 2) || z >= 8 && z <= 28 && x >= 1 && x <= 29
			c := DefenseCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(open), Passable: domain.Known(open), BlocksSight: domain.Known(!open || gate),
				PlayerOwned: domain.Known(gate), NaturalRock: domain.Known(!open), EdgeReachable: domain.Known(open), HomeArea: domain.Known(open && z >= 8), Door: domain.Known(gate), CoverFill: domain.Known(0.0)}
			if !open {
				c.CoverFill, c.Edifice = domain.Known(1.0), "Granite"
			}
			if gate {
				c.CoverFill, c.Edifice = domain.Known(1.0), "Door"
			}
			r.Cells = append(r.Cells, c)
		}
	}
	return r
}

// Rock across the corridor is dug, not routed around (#1588): DefenseDig
// lists the rock on the lane and the defenders' ground, the layout waits on
// it, and proceeds once it is open. Open ground needs no dig.
func TestDefenseDigListsRockOnTheCorridor(t *testing.T) {
	r := defenseFixture()
	if dig, err := DefenseDig(r); err != nil || len(dig) != 0 {
		t.Fatal("open ground dug", dig, err)
	}
	inRock := func(c domain.Cell) bool { return c.X >= 11 && c.X <= 19 && c.Z >= 8 && c.Z <= 10 }
	for i, c := range r.Cells {
		if inRock(c.Cell) {
			c.Walkable, c.Passable, c.NaturalRock, c.Edifice = domain.Known(false), domain.Known(false), domain.Known(true), "Granite"
			r.Cells[i] = c
		}
	}
	if _, err := DefenseLayouts(r); err == nil {
		t.Fatal("layout stood on rock")
	}
	dig, err := DefenseDig(r)
	if err != nil || len(dig) == 0 {
		t.Fatal("no dig for rock on the corridor", dig, err)
	}
	for _, c := range dig {
		if !inRock(c) {
			t.Fatal("dug a cell that is not rock", c)
		}
	}
	for i, c := range r.Cells {
		if inRock(c.Cell) {
			c.Walkable, c.Passable, c.NaturalRock, c.Edifice = domain.Known(true), domain.Known(true), domain.Known(false), ""
			r.Cells[i] = c
		}
	}
	if dig, _ = DefenseDig(r); len(dig) != 0 {
		t.Fatal("dug after the rock was cleared", dig)
	}
	if _, err = DefenseLayouts(r); err != nil {
		t.Fatal("layout waits after the dig:", err)
	}
}

func cells(xs ...int32) []domain.Cell {
	out := make([]domain.Cell, 0, len(xs)/2)
	for i := 0; i+1 < len(xs); i += 2 {
		out = append(out, domain.Cell{X: xs[i], Z: xs[i+1]})
	}
	return out
}
func placed(t *testing.T, tier DefenseTier) map[domain.Cell]string {
	t.Helper()
	out := map[domain.Cell]string{}
	for _, b := range tier.Buildings {
		if _, dup := out[b.Cell()]; dup {
			t.Fatal("duplicate placement", b.Cell())
		}
		out[b.Cell()] = b.Definition()
	}
	return out
}

// The fixture's killbox (#1544): 3 defenders and no turret size the kill
// zone to columns -4..4 (x 11..19, side walls at x 10 and 20), and its
// 19-row depth holds two legs. The ring narrows to the 1-tile entrance at
// x 15; leg 0 (z 8..10) runs to x 11, the U-turn (z 11..13) leads into leg
// 1 (z 14..16) back to x 19, and the exit gap (x 17..19, z 17..19) opens
// on the kill row (z 20) at x 18.
func TestDefenseLayoutKillboxShape(t *testing.T) {
	layout, err := DefenseLayouts(defenseFixture())
	if err != nil {
		t.Fatal(err)
	}
	if layout.Chokepoint != (domain.Cell{X: 15, Z: 5}) || layout.Toward != domain.North || layout.Width != 3 || layout.Entry != layout.Chokepoint {
		t.Fatalf("%+v", layout)
	}
	if !reflect.DeepEqual(layout.TrapLane[:3], cells(15, 5, 15, 6, 15, 7)) || layout.KillZone() != (domain.Cell{X: 18, Z: 19}) {
		t.Fatal(layout.TrapLane)
	}
	funnel, _ := layout.Tier(TierFunnel)
	walls := placed(t, funnel)
	// The 1-tile entrance: the plan's 3-wide opening is walled beside it.
	for _, c := range cells(14, 5, 14, 6, 14, 7, 16, 5, 16, 6, 16, 7) {
		if walls[c] != "Wall" {
			t.Fatal("opening not narrowed at", c)
		}
	}
	// The 2-thick back wall spans the kill zone and its sides, with one
	// doorway of doors at column 0.
	for z := int32(25); z <= 26; z++ {
		for x := int32(10); x <= 20; x++ {
			want := "Wall"
			if x == 15 {
				want = "Door"
			}
			if got := walls[domain.Cell{X: x, Z: z}]; got != want {
				t.Fatalf("back wall at (%d,%d): %q", x, z, got)
			}
		}
	}
	corridor, _ := layout.Tier(TierTrapCorridor)
	built := placed(t, corridor)
	// The fence T: a continuous bar across the kill zone in front of the
	// sandbags and a stem toward the exit.
	for x := int32(11); x <= 19; x++ {
		if built[domain.Cell{X: x, Z: 21}] != "Fence" {
			t.Fatal("fence bar gap at", x)
		}
	}
	if built[domain.Cell{X: 18, Z: 20}] != "Fence" {
		t.Fatal("no fence stem before the exit", built)
	}
	// No door anywhere on the raiders' route.
	lane := map[domain.Cell]bool{}
	for _, c := range layout.TrapLane {
		lane[c] = true
	}
	for _, tier := range layout.Tiers {
		for _, b := range tier.Buildings {
			if b.Definition() == "Door" && (tier.Name != TierFunnel || lane[b.Cell()]) {
				t.Fatal("door on the corridor", b)
			}
		}
	}
	traps := 0
	for c, def := range built {
		if def == "TrapSpike" {
			traps++
			if !lane[c] {
				t.Fatal("trap off the corridor", c)
			}
		}
	}
	if traps < 3 {
		t.Fatal("traps", traps)
	}
	choke, _ := layout.Tier(TierChokepoint)
	if len(choke.Buildings) != 0 || len(choke.Reserved) <= len(layout.TrapLane) {
		t.Fatal(choke)
	}
	firing, _ := layout.Tier(TierFiringLine)
	if len(layout.Firing) != 3 || len(firing.Buildings) != 6 || layout.LinesVerified {
		t.Fatalf("%+v", layout.Firing)
	}
	// Each shooter cell is floored so nothing grows onto the position (#224).
	got := placed(t, firing)
	want := map[domain.Cell]string{}
	for _, c := range cells(15, 22, 14, 22, 16, 22) {
		want[c] = "Sandbags"
	}
	for _, c := range cells(15, 23, 14, 23, 16, 23) {
		want[c] = "WoodPlankFloor"
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("firing line %v", got)
	}
	wantFiring := []FiringPosition{
		{Cell: domain.Cell{X: 15, Z: 23}, Cover: domain.Cell{X: 15, Z: 22}, Retreat: domain.Cell{X: 15, Z: 24}},
		{Cell: domain.Cell{X: 14, Z: 23}, Cover: domain.Cell{X: 14, Z: 22}, Retreat: domain.Cell{X: 14, Z: 24}},
		{Cell: domain.Cell{X: 16, Z: 23}, Cover: domain.Cell{X: 16, Z: 22}, Retreat: domain.Cell{X: 16, Z: 24}},
	}
	if !reflect.DeepEqual(layout.Firing, wantFiring) {
		t.Fatalf("%+v", layout.Firing)
	}
	f, a := layout.Probe()
	if !reflect.DeepEqual(f, cells(15, 23, 14, 23, 16, 23)) || !reflect.DeepEqual(a, cells(18, 19)) {
		t.Fatal(f, a)
	}
	if v, k := funnel.Costs.Value(); !k || len(v) != 2 {
		t.Fatal(funnel.Costs)
	}
	again, _ := DefenseLayouts(defenseFixture())
	if !reflect.DeepEqual(layout, again) {
		t.Fatal("layout is not deterministic")
	}
}

// killRow is the kill zone's first row, the raiders' goal: the row past
// the exit, across the side walls' span.
func killRow(l DefenseLayout) []domain.Cell {
	var out []domain.Cell
	exit := l.KillZone()
	for x := int32(-10); x <= 10; x++ {
		out = append(out, domain.Cell{X: exit.X + x, Z: exit.Z + 1})
	}
	return out
}

// standingCosts is the pathing effect of every tier the layout places, as
// DefenseLayouts prices it.
func standingCosts(l DefenseLayout, walled []domain.Cell) (corridorCosts, []domain.Cell) {
	k := newCorridorCosts()
	for _, c := range walled {
		k.closed[c] = true
	}
	var walls []domain.Cell
	for _, tier := range l.Tiers {
		for _, b := range tier.Buildings {
			c := b.Cell()
			switch b.Definition() {
			case "Wall":
				k.closed[c] = true
				walls = append(walls, c)
			case "Door":
				k.cost[c], k.full[c] = pathWoodDoorCost, true
				walls = append(walls, c)
			case "Fence":
				k.cost[c] = pathFenceCost
			case "TrapSpike":
				k.traps[c] = true
			}
		}
	}
	return k, walls
}

// Raiders' cheapest route walks every leg and U-turn of the snake: with
// every planned wall bashable at the wall-bash price, no cheapest route
// breaks one. Thinner leg walls would be bashed.
func TestDefenseLayoutRaidersWalkEveryLeg(t *testing.T) {
	r := defenseFixture()
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	s, _ := newDefenseSite(r)
	k, walls := standingCosts(layout, nil)
	if !s.raiderWalksCorridor(k, layout, walls, killRow(layout)) {
		t.Fatal("raiders bash through the corridor")
	}
	raider := corridorCosts{closed: map[domain.Cell]bool{}, full: k.full, cost: map[domain.Cell]int{}}
	for c, v := range k.cost {
		raider.cost[c] = v
	}
	for c := range k.closed {
		raider.cost[c], raider.full[c] = pathWallBashCost, true
	}
	route, ok := s.cheapestRoutes(raider, layout.Entry, killRow(layout))
	if !ok {
		t.Fatal("no raider route")
	}
	// Every band of the snake: leg 0, the U-turn, leg 1, the exit gap.
	for _, band := range [][2]int32{{8, 10}, {11, 13}, {14, 16}, {17, 19}} {
		seen := false
		for c := range route {
			seen = seen || c.Z >= band[0] && c.Z <= band[1]
		}
		if !seen {
			t.Fatal("raider route skips rows", band)
		}
	}
	// Across the widest kill zone too; there a 1-thick wall between the
	// last leg and the kill zone would be cheaper to bash than the walk.
	r.Defenders, r.Turret = 8, DefenseTurretRequest{Definition: TurretMini, Conduit: "PowerConduit", Max: 4}
	if layout, err = DefenseLayouts(r); err != nil {
		t.Fatal(err)
	}
	s, _ = newDefenseSite(r)
	k, walls = standingCosts(layout, nil)
	if !s.raiderWalksCorridor(k, layout, walls, killRow(layout)) {
		t.Fatal("raiders bash through the widest corridor")
	}
	opened := func(c domain.Cell) bool { return (c.Z == 18 || c.Z == 19) && c.X > 8 && c.X < 22 }
	thin := corridorCosts{closed: map[domain.Cell]bool{}, full: k.full, traps: k.traps, cost: k.cost}
	var thinWalls []domain.Cell
	for _, c := range walls {
		if !opened(c) {
			thinWalls = append(thinWalls, c)
		}
	}
	for c := range k.closed {
		if !opened(c) {
			thin.closed[c] = true
		}
	}
	if s.raiderWalksCorridor(thin, layout, thinWalls, killRow(layout)) {
		t.Fatal("open rows read as a bash-proof wall")
	}
}

// Every trap lies on the raiders' cheapest line, raiders not knowing the
// player's traps; colonists, who price their known traps, cross none
// walking the hallway from the kill zone out or from Home to the edge, and
// every trap is in reach of that route without stepping on another.
func TestDefenseLayoutTrapsOnRaidersLineOffColonistsRoute(t *testing.T) {
	r := defenseFixture()
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	s, _ := newDefenseSite(r)
	k, _ := standingCosts(layout, nil)
	raiderLine, ok := s.cheapestRoutes(raiderCosts(k, nil), layout.Entry, []domain.Cell{layout.KillZone()})
	if !ok || len(k.traps) == 0 {
		t.Fatal("no raider line or no traps", ok)
	}
	for trap := range k.traps {
		if !raiderLine[trap] {
			t.Fatal("trap off the raiders' cheapest line", trap)
		}
	}
	lane := map[domain.Cell]bool{}
	for _, c := range layout.TrapLane {
		lane[c] = true
	}
	hall := k
	hall.within = lane
	route, ok := s.cheapestRoutes(hall, layout.KillZone(), []domain.Cell{layout.Entry})
	if !ok || crossesAny(route, k.traps) {
		t.Fatal("colonist route through the corridor crosses a trap", ok)
	}
	if !s.colonistRouteAvoidsTraps(k, r.Home) {
		t.Fatal("colonist route from home crosses a trap")
	}
	reach, _ := s.paths(layout.Entry, func() map[domain.Cell]bool {
		closed := map[domain.Cell]bool{}
		for c := range k.closed {
			closed[c] = true
		}
		for c := range k.traps {
			closed[c] = true
		}
		return closed
	}())
	for c := range route {
		if _, ok := reach[c]; !ok {
			t.Fatal("route cell unreachable without a trap", c)
		}
	}
	for trap := range k.traps {
		ok := false
		for _, d := range pathSteps {
			_, near := reach[addCell(trap, d)]
			ok = ok || near
		}
		if !ok {
			t.Fatal("trap out of the colonists' reach", trap)
		}
		for _, d := range pathSteps {
			if k.traps[addCell(trap, d)] {
				t.Fatal("adjacent traps", trap)
			}
		}
	}
}

// Raider-side shaping is walls and fences: every sandbag is a firing
// position's cover, and raiders reach none without climbing the fence bar.
func TestDefenseLayoutSandbagsOnlyOnTheDefendersLine(t *testing.T) {
	r := defenseFixture()
	r.Defenders = 8
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	cover := map[domain.Cell]bool{}
	for _, f := range layout.Firing {
		cover[f.Cover] = true
	}
	k, _ := standingCosts(layout, nil)
	closed := map[domain.Cell]bool{}
	for c := range k.closed {
		closed[c] = true
	}
	for c, v := range k.cost {
		if v == pathFenceCost || k.full[c] {
			closed[c] = true
		}
	}
	s, _ := newDefenseSite(r)
	raiders, _ := s.paths(layout.Entry, closed)
	for _, tier := range layout.Tiers {
		for _, b := range tier.Buildings {
			if b.Definition() != "Sandbags" && b.Definition() != "Barricade" {
				continue
			}
			if !cover[b.Cell()] {
				t.Fatal("sandbag off the defenders' line", b)
			}
			for _, d := range pathSteps {
				if _, ok := raiders[addCell(b.Cell(), d)]; ok {
					t.Fatal("raiders reach a sandbag without crossing the fence bar", b)
				}
			}
		}
	}
}

// The kill zone grows with the defenders and turrets, the legs span it,
// and the killbox's depth sets the number of legs.
func TestDefenseLayoutSizedFromDefendersTurretsAndDepth(t *testing.T) {
	wide := defenseFixture()
	wide.Defenders = 8
	wide.Turret = DefenseTurretRequest{Definition: TurretMini, Conduit: "PowerConduit", Max: 4}
	layout, err := DefenseLayouts(wide)
	if err != nil {
		t.Fatal(err)
	}
	funnel, _ := layout.Tier(TierFunnel)
	walls := placed(t, funnel)
	// 8 defenders and 4 turrets want columns -10..10; the plan's killbox
	// caps them at -6..6, side walls at x 8 and 22.
	for _, c := range cells(8, 22, 22, 22, 8, 10, 22, 10) {
		if walls[c] != "Wall" {
			t.Fatal("side wall missing at", c, walls)
		}
	}
	if len(layout.Firing) != 8 {
		t.Fatal(layout.Firing)
	}
	shallow := defenseFixture()
	shallow.Killbox.Depth = killboxLegPitch + killboxZoneRows
	layout, err = DefenseLayouts(shallow)
	if err != nil {
		t.Fatal(err)
	}
	if exit := layout.KillZone(); exit.Z != 5+perimeterThick+killboxLegPitch-1 {
		t.Fatal("one leg's exit", exit)
	}
	shallow.Killbox.Depth = killboxLegPitch + killboxZoneRows - 1
	if _, err = DefenseLayouts(shallow); err == nil {
		t.Fatal("a killbox too shallow for one leg")
	}
}
func TestDefenseLayoutLinesOfFire(t *testing.T) {
	r := defenseFixture()
	exit := domain.Cell{X: 18, Z: 19}
	r.Lines = []DefenseLine{
		{From: domain.Cell{X: 15, Z: 23}, To: exit, LineOfSight: domain.Known(false)},
		{From: domain.Cell{X: 14, Z: 23}, To: exit, LineOfSight: domain.Known(true)},
		{From: domain.Cell{X: 16, Z: 23}, To: exit, LineOfSight: domain.Known(true)},
		{From: domain.Cell{X: 13, Z: 23}, To: exit, LineOfSight: domain.Known(true)},
	}
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	if !layout.LinesVerified || !reflect.DeepEqual([]domain.Cell{layout.Firing[0].Cell, layout.Firing[1].Cell, layout.Firing[2].Cell}, cells(14, 23, 16, 23, 13, 23)) {
		t.Fatalf("%+v", layout.Firing)
	}
	r.Lines[3].LineOfSight = domain.Unknown[bool]()
	if layout, err = DefenseLayouts(r); err != nil || layout.LinesVerified || layout.Firing[2].Verified {
		t.Fatal(layout.Firing, err)
	}
}
func TestDefenseLayoutRangeAndDefenders(t *testing.T) {
	r := defenseFixture()
	r.MinRange = domain.Unknown[float64]()
	layout, err := DefenseLayouts(r)
	if tier, _ := layout.Tier(TierFiringLine); err != nil || len(layout.Firing) != 0 || len(tier.Buildings) != 0 {
		t.Fatal(layout.Firing, err)
	}
	r.MinRange = domain.Known(3.0)
	if layout, err = DefenseLayouts(r); err != nil || len(layout.Firing) != 0 {
		t.Fatal("firing cells beyond range", layout.Firing, err)
	}
	r.MinRange, r.Defenders = domain.Known(25.9), 0
	if layout, err = DefenseLayouts(r); err != nil || len(layout.Firing) != 0 {
		t.Fatal(layout.Firing, err)
	}
	r.Defenders = 8
	if layout, err = DefenseLayouts(r); err != nil || len(layout.Firing) != 8 {
		t.Fatal(layout.Firing, err)
	}
	r.Defenders = 9
	if _, err = DefenseLayouts(r); err == nil {
		t.Fatal("defender bound")
	}
}
func TestDefenseLayoutRefusesProtectedAndCutOff(t *testing.T) {
	r := defenseFixture()
	r.Protected = cells(10, 15)
	if _, err := DefenseLayouts(r); err == nil {
		t.Fatal("wall on a protected walkway")
	}
	r = defenseFixture()
	r.Protected = cells(15, 9)
	if layout, err := DefenseLayouts(r); err != nil {
		t.Fatal(err)
	} else if corridor, _ := layout.Tier(TierTrapCorridor); placed(t, corridor)[domain.Cell{X: 15, Z: 9}] != "" {
		t.Fatal("trap on a protected walkway")
	}
	r = defenseFixture()
	for i := range r.Cells {
		if r.Cells[i].Cell == (domain.Cell{X: 25, Z: 6}) {
			r.Cells[i].Passable, r.Cells[i].Walkable, r.Cells[i].EdgeReachable = domain.Known(true), domain.Known(true), domain.Known(false)
		}
	}
	r.Entrances = cells(25, 6)
	if _, err := DefenseLayouts(r); err == nil {
		t.Fatal("entrance without a route accepted")
	}
	r = defenseFixture()
	r.UnitCosts = nil
	layout, err := DefenseLayouts(r)
	if funnel, _ := layout.Tier(TierFunnel); err != nil {
		t.Fatal(err)
	} else if _, known := funnel.Costs.Value(); known {
		t.Fatal("cost invented without unit costs")
	}
}
func TestDefenseLayoutUnknownFactsBlock(t *testing.T) {
	r := defenseFixture()
	for i := range r.Cells {
		if r.Cells[i].Cell == (domain.Cell{X: 10, Z: 16}) {
			r.Cells[i].Walkable = domain.Unknown[bool]()
		}
	}
	if _, err := DefenseLayouts(r); err == nil {
		t.Fatal("unknown walkability became a wall site")
	}
	r = defenseFixture()
	r.Cells = r.Cells[:len(r.Cells)/2]
	if _, err := DefenseLayouts(r); err == nil {
		t.Fatal("missing census became a route")
	}
}
func TestDefenseLayoutWaitsForAKillbox(t *testing.T) {
	r := defenseFixture()
	r.Killbox = DefenseKillbox{}
	if _, err := DefenseLayouts(r); err == nil {
		t.Fatal("a layout without the plan's killbox opening")
	}
}

// The funnel leaves the perimeter wall's cells to the perimeter tier.
func TestDefenseLayoutLeavesThePerimeterWall(t *testing.T) {
	r := defenseFixture()
	r.Killbox.Walled = cells(14, 5)
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	funnel, _ := layout.Tier(TierFunnel)
	got := placed(t, funnel)
	if _, ok := got[domain.Cell{X: 14, Z: 5}]; ok {
		t.Fatal(got)
	}
	if _, ok := got[domain.Cell{X: 16, Z: 5}]; !ok {
		t.Fatal(got)
	}
}

// A funnel wall whose eight neighbours are all rock or wall can never have
// its frame touched once they finish, so the plan would never close (#1248).
func TestDefenseLayoutFunnelHasNoSealedFrames(t *testing.T) {
	r := defenseFixture()
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	funnel, _ := layout.Tier(TierFunnel)
	walls := map[domain.Cell]bool{}
	for _, b := range funnel.Buildings {
		walls[b.Cell()] = true
	}
	site, err := newDefenseSite(r)
	if err != nil {
		t.Fatal(err)
	}
	for c := range walls {
		if site.enclosed(c, func(n domain.Cell) bool { return walls[n] }) {
			t.Errorf("funnel wall %v is sealed by its neighbours", c)
		}
	}
}

func TestDefenseLayoutRejectsInvalidRequests(t *testing.T) {
	for name, edit := range map[string]func(*DefenseRequest){
		"region outside bounds": func(r *DefenseRequest) { r.Region.Width = 300 },
		"home outside region":   func(r *DefenseRequest) { r.Home = domain.Cell{X: 40, Z: 40} },
		"duplicate cell":        func(r *DefenseRequest) { r.Cells = append(r.Cells, r.Cells[0]) },
		"missing definition":    func(r *DefenseRequest) { r.Definitions.Trap = "" },
		"negative range":        func(r *DefenseRequest) { r.MinRange = domain.Known(-1.0) },
		"cover over one":        func(r *DefenseRequest) { r.Cells[0].CoverFill = domain.Known(2.0) },
		"entrance outside":      func(r *DefenseRequest) { r.Entrances = cells(99, 99) },
		"line outside":          func(r *DefenseRequest) { r.Lines = []DefenseLine{{From: domain.Cell{X: 99, Z: 0}}} },
	} {
		r := defenseFixture()
		edit(&r)
		if _, err := DefenseLayouts(r); err == nil {
			t.Fatal(name, "accepted")
		}
	}
}
