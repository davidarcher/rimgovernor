package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// burnFuelCells are four stools at the corners inside the seal.
var burnFuelCells = []domain.Cell{{X: 6, Z: 9}, {X: 6, Z: 15}, {X: 12, Z: 9}, {X: 12, Z: 15}}

// burnView is molotovHive with the given burn census, less the scarabs by
// the hive, so the three riflemen are not outmatched and never wait. The
// carrier a stands north of the hive, so the corridor leads out north;
// the megaspiders r1 and r2 sit south of it.
func burnView(tick domain.Tick, temp float64, site domain.Fact[BurnSite]) CombatView {
	view := molotovHive(tick, temp)
	view.Threats = slices.DeleteFunc(view.Threats, func(t SquadThreatFacts) bool { return t.ID[0] == 'h' })
	view.Positional = slices.DeleteFunc(view.Positional, func(t DefensiveThreatFacts) bool { return t.ID[0] == 'h' })
	for id, c := range map[PawnID]domain.Cell{"r1": {X: 9, Z: 5}, "r2": {X: 10, Z: 4}} {
		s, d := combatRaider(id, c)
		view.Threats, view.Positional = append(view.Threats, s), append(view.Positional, d)
		view.Pawns = append(view.Pawns, CombatPawnState{ID: domain.PawnID(id), Cell: domain.Known(c), Kind: "Megaspider", Stance: StanceMoving})
	}
	view.Burn = site
	return view
}

// ready is a standing, roofed burn with its four stools.
func ready() domain.Fact[BurnSite] {
	return domain.Known(BurnSite{Roofed: true, Fuel: burnFuelCells})
}

// downInsects marks r1 and r2 downed.
func downInsects(view CombatView) CombatView {
	for i := range view.Pawns {
		if id := view.Pawns[i].ID; id == "r1" || id == "r2" {
			view.Pawns[i].Downed = true
		}
	}
	return view
}

// molotovAt is a's attack-ground cell, if any.
func molotovAt(orders []CombatOrder) (domain.Cell, bool) {
	for _, o := range orders {
		if o.Pawn == "a" && o.Kind == OrderAttackGround {
			return o.Cell, true
		}
	}
	return domain.Cell{}, false
}

// {a molotov carrier north of the hive} -> the burn-out plans its corridor
// north with the retreat past the last door; while the census shows any
// seal wall, corridor door or stool missing (or reads unknown) nothing is
// thrown; once none is missing, a throws its molotov at the stool
// farthest from the insects, never at the hive.
func TestBurnOutSealsThenIgnites(t *testing.T) {
	var m CombatMemory
	for i, site := range []domain.Fact[BurnSite]{domain.Known(BurnSite{Missing: 7, Roofed: true}), domain.Known(BurnSite{Missing: 1, Roofed: true, Fuel: burnFuelCells}), domain.Unknown[BurnSite]()} {
		var orders []CombatOrder
		orders, m = decideStop(t, burnView(domain.Tick(100+i), 30, site), StopEvent{}, m)
		if !m.Burn.Fueling() || m.Burn.Carrier != "a" || m.Burn.Hive != hiveCell || m.Burn.Exit != (domain.Cell{Z: 1}) {
			t.Fatalf("burn %+v", m.Burn)
		}
		if _, ok := molotovAt(orders); ok {
			t.Fatalf("threw with %+v: %+v", site, orders)
		}
	}
	doors := m.Burn.CorridorDoors()
	last := doors[len(doors)-1]
	for _, c := range rectCells(m.Burn.Room) {
		if c.Z <= last.Z {
			t.Fatalf("retreat %+v not past the last door %v", m.Burn.Room, last)
		}
	}
	orders, m := decideStop(t, burnView(200, 30, ready()), StopEvent{}, m)
	at, ok := molotovAt(orders)
	if !ok || m.Burn.Lit != 200 || at != (domain.Cell{X: 6, Z: 15}) {
		t.Fatalf("ignition at %v %v: %+v %+v", at, ok, orders, m.Burn)
	}
}

// {the seal room reads unroofed} -> the burn ends unlit.
func TestBurnOutNeedsRoof(t *testing.T) {
	orders, m := decideStop(t, burnView(100, 30, domain.Known(BurnSite{Fuel: burnFuelCells})), StopEvent{}, CombatMemory{})
	if m.Burn == nil || !m.Burn.Done || m.Burn.Lit != 0 {
		t.Fatalf("burn %+v", m.Burn)
	}
	if _, ok := molotovAt(orders); ok {
		t.Fatalf("threw: %+v", orders)
	}
}

// {every stool within reach of an insect} -> no molotov: a player's flame
// on an insect may send the hive to assault.
func TestBurnOutSparesInsects(t *testing.T) {
	view := burnView(100, 30, domain.Known(BurnSite{Roofed: true, Fuel: []domain.Cell{{X: 9, Z: 6}}}))
	orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	if _, ok := molotovAt(orders); ok || m.Burn.Lit != 0 {
		t.Fatalf("threw by an insect: %+v %+v", orders, m.Burn)
	}
}

// {the burn-out lit} -> everyone else moves past the last corridor door
// at once behind its closed, forbidden doors; the carrier follows after
// its throw; a molotov tops the fire up only while the hive reads under
// 150 C and never at 200 C; the retreat holds, whatever the clock, until
// every insect reads downed; then the wait releases and the fighters go
// for the downed insects.
func TestBurnOutHoldsUntilInsectsDown(t *testing.T) {
	_, m := decideStop(t, burnView(100, 30, ready()), StopEvent{}, CombatMemory{})
	if m.Burn == nil || m.Burn.Lit == 0 || !m.Wait {
		t.Fatalf("not lit: %+v", m)
	}
	inRetreat := func(m CombatMemory, pawn domain.PawnID) bool {
		for _, r := range m.Roles {
			if r.Pawn == pawn {
				return r.Cell != nil && (CombatRoom{Interior: m.Burn.Room}).contains(*r.Cell) && r.Retreat
			}
		}
		return false
	}
	if !inRetreat(m, "b") || !inRetreat(m, "c") || inRetreat(m, "a") {
		t.Fatalf("retreat %+v", m.Roles)
	}
	forbidden := map[domain.Cell]bool{}
	for _, d := range m.WaitDoors {
		forbidden[d.Cell] = forbidden[d.Cell] || d.Mode == DoorForbid
	}
	for _, d := range m.Burn.CorridorDoors() {
		if !forbidden[d] {
			t.Fatalf("door %v open: %+v", d, m.WaitDoors)
		}
	}
	lit := m.Burn.Lit
	for _, step := range []struct {
		tick  domain.Tick
		temp  float64
		throw bool
	}{
		{lit + burnThrowTicks, 120, false},        // too soon after the last
		{lit + burnFireTicks, 180, false},         // hot enough
		{lit + burnFireTicks + 100, 210, false},   // never above the cap
		{lit + 2*burnFireTicks, 140, true},        // cooling: top up
		{lit + 2*burnFireTicks + 100, 140, false}, // one molotov per burnFireTicks
		{lit + 2*burnFireTicks + burnThrowTicks, 140, false},
		{lit + heatStrokeTicks + 5*burnFireTicks, 30, true}, // cold, not out: a cold hive with fuel is relit, not left
	} {
		var orders []CombatOrder
		orders, m = decideStop(t, burnView(step.tick, step.temp, ready()), StopEvent{}, m)
		if _, got := molotovAt(orders); got != step.throw {
			t.Fatalf("tick %d at %v C: throw %v, want %v: %+v", step.tick, step.temp, got, step.throw, m.Burn)
		}
		if m.Burn.Done || !m.Wait {
			t.Fatalf("left before the insects were down: %+v", m)
		}
		if step.tick-m.Burn.Thrown >= burnThrowTicks && !inRetreat(m, "a") {
			t.Fatalf("carrier did not follow: %+v", m.Roles)
		}
	}
	tick := lit + heatStrokeTicks + 6*burnFireTicks
	orders, m := decideStop(t, downInsects(burnView(tick, 150, ready())), StopEvent{}, m)
	if !m.Burn.Done {
		t.Fatalf("insects down, still burning: %+v", m.Burn)
	}
	attacked := map[domain.PawnID]bool{}
	for _, o := range orders {
		if o.Kind == OrderAttack {
			attacked[o.Target] = true
		}
	}
	if !attacked["r1"] && !attacked["r2"] {
		t.Fatalf("downed insects not finished: %+v", orders)
	}
	_, m = decideStop(t, downInsects(burnView(tick+100, 150, ready())), StopEvent{}, m)
	if m.Wait {
		t.Fatalf("wait held after the insects went down: %+v", m)
	}
}

// {a lit burn with no fuel left and a cold hive} -> the fire is out: the
// burn is done though insects stand.
func TestBurnOutFizzles(t *testing.T) {
	_, m := decideStop(t, burnView(100, 30, ready()), StopEvent{}, CombatMemory{})
	_, m = decideStop(t, burnView(100+burnFireTicks, 30, domain.Known(BurnSite{Roofed: true})), StopEvent{}, m)
	if !m.Burn.Done {
		t.Fatalf("burn %+v", m.Burn)
	}
}

// openCensus is roofed open floor around the hive out to r, the hive
// standing.
func openCensus(r int32) map[domain.Cell]WaitDoorCell {
	census := map[domain.Cell]WaitDoorCell{hiveCell: {Edifice: "Hive", Walkable: true, Roofed: true}}
	for x := hiveCell.X - r; x <= hiveCell.X+r; x++ {
		for z := hiveCell.Z - r; z <= hiveCell.Z+r; z++ {
			if c := (domain.Cell{X: x, Z: z}); c != hiveCell {
				census[c] = WaitDoorCell{Walkable: true, Roofed: true}
			}
		}
	}
	return census
}

// {open floor around the hive, one ring cell natural rock, one corridor
// cell a wooden door and one a stone door} -> the seal walls every open
// ring cell but the corridor's gap, walls both flanks of the doors past
// it, and asks for stone doors in a 1-wide line out of the gap: the gap
// and the wooden door's cell, not the stone door's.
func TestBurnSealPlan(t *testing.T) {
	b := CombatBurn{Hive: hiveCell, Exit: domain.Cell{Z: 1}}
	census := openCensus(10)
	rock := domain.Cell{X: hiveCell.X - burnSealRadius, Z: hiveCell.Z}
	census[rock] = WaitDoorCell{Edifice: "Granite"}
	doors := b.CorridorDoors()
	census[doors[1]] = WaitDoorCell{Edifice: BurnDoorDef, Stuff: "WoodLog", Walkable: true}
	census[doors[2]] = WaitDoorCell{Edifice: BurnDoorDef, Stuff: "BlocksSlate", Walkable: true}
	got, err := BurnSeal(b, census, "BlocksGranite", CoreItemFacts())
	if err != nil {
		t.Fatal(err)
	}
	walls, built := map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for _, x := range got {
		if x.Stuff() != "BlocksGranite" {
			t.Fatalf("not stone: %+v", x)
		}
		switch x.Definition() {
		case BurnWallDef:
			walls[x.Cell()] = true
		case BurnDoorDef:
			built[x.Cell()] = true
		}
	}
	for x := hiveCell.X - burnSealRadius; x <= hiveCell.X+burnSealRadius; x++ {
		for z := hiveCell.Z - burnSealRadius; z <= hiveCell.Z+burnSealRadius; z++ {
			c := domain.Cell{X: x, Z: z}
			if chebyshev(c, hiveCell) != burnSealRadius {
				continue
			}
			if want := c != doors[0] && c != rock; walls[c] != want {
				t.Fatalf("ring %v walled %v, want %v", c, walls[c], want)
			}
		}
	}
	for k, d := range doors {
		if d != (domain.Cell{X: hiveCell.X, Z: hiveCell.Z + burnSealRadius + int32(k)}) {
			t.Fatalf("door %d at %v", k, d)
		}
		if k > 0 && (!walls[domain.Cell{X: d.X - 1, Z: d.Z}] || !walls[domain.Cell{X: d.X + 1, Z: d.Z}]) {
			t.Fatalf("corridor wider than 1 at %v", d)
		}
	}
	if len(doors) != burnCorridorDoors || !built[doors[0]] || !built[doors[1]] || built[doors[2]] {
		t.Fatalf("doors %v built %v", doors, built)
	}
	if n := len(walls); n != 8*burnSealRadius-2+2*(burnCorridorDoors-1) {
		t.Fatalf("%d walls", n)
	}
}

// {the seal standing but one stool short} -> the survey still counts the
// stool missing, so the fight does not light; with the stool, nothing is
// missing and all four stand as fuel. One unroofed cell inside the seal
// reads the room unroofed.
func TestBurnSurvey(t *testing.T) {
	b := CombatBurn{Hive: hiveCell, Exit: domain.Cell{Z: 1}}
	census := openCensus(10)
	items := CoreItemFacts()
	seal, err := BurnSeal(b, census, "BlocksGranite", items)
	if err != nil {
		t.Fatal(err)
	}
	for _, x := range seal {
		census[x.Cell()] = WaitDoorCell{Edifice: x.Definition(), Stuff: x.Stuff(), Walkable: x.Definition() == BurnDoorDef, Roofed: true}
	}
	fuel, err := BurnFuel(hiveCell, census)
	if err != nil || len(fuel) != burnFuelStools {
		t.Fatalf("%v %+v", err, fuel)
	}
	for _, x := range fuel[1:] {
		census[x.Cell()] = WaitDoorCell{Edifice: BurnFuelDef, Stuff: BurnFuelStuff, Walkable: true, Roofed: true}
	}
	if got, _ := BurnSurvey(b, census, items); got.Missing != 1 || !got.Roofed || len(got.Fuel) != burnFuelStools-1 {
		t.Fatalf("survey %+v", got)
	}
	census[fuel[0].Cell()] = WaitDoorCell{Edifice: BurnFuelDef, Walkable: true, Roofed: true}
	if got, _ := BurnSurvey(b, census, items); got.Missing != 0 || len(got.Fuel) != burnFuelStools {
		t.Fatalf("survey %+v", got)
	}
	census[domain.Cell{X: hiveCell.X + 1, Z: hiveCell.Z}] = WaitDoorCell{Walkable: true}
	if got, _ := BurnSurvey(b, census, items); got.Roofed {
		t.Fatalf("survey %+v", got)
	}
}

// {open floor around the hive with one stool standing} -> the fuel tier
// adds three stools inside the seal, none next to the hive, spread out:
// the four stand at the corners.
func TestBurnFuelSpreadsStools(t *testing.T) {
	census := openCensus(4)
	census[domain.Cell{X: 6, Z: 9}] = WaitDoorCell{Edifice: BurnFuelDef, Walkable: true, Roofed: true}
	got, err := BurnFuel(hiveCell, census)
	if err != nil || len(got) != burnFuelStools-1 {
		t.Fatalf("%v %+v", err, got)
	}
	var cells []domain.Cell
	for _, b := range got {
		if chebyshev(b.Cell(), hiveCell) < burnFuelClear || chebyshev(b.Cell(), hiveCell) > BurnFuelRadius {
			t.Fatalf("stool at %v", b.Cell())
		}
		cells = append(cells, b.Cell())
	}
	slices.SortFunc(cells, cellOrder)
	if want := []domain.Cell{{X: 6, Z: 15}, {X: 12, Z: 9}, {X: 12, Z: 15}}; !slices.Equal(cells, want) {
		t.Fatalf("stools %v, want %v", cells, want)
	}
}
