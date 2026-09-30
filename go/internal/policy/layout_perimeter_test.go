package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func perimeterPlan(t *testing.T, cell func(x, z int32) SurveyCell) LayoutPlan {
	t.Helper()
	s := zoningSurvey(200, cell)
	p := PlanPerimeter(PlanCore(Zone(s), 3, BuildTierCamp), s)
	if !p.Valid() {
		t.Fatal("invalid plan")
	}
	if c, leak := perimeterLeak(p, s); leak {
		t.Fatal("raiders reach the core at", c)
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
			t.Fatal("gate spans the 3-cell wall", g)
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

// perimeterLeak floods from the map edge over walkable cells, the wall and
// killbox closed and no corner cut between two closed cells, and reports
// a room cell it reaches: the ring leaks there (#949).
func perimeterLeak(p LayoutPlan, s MapSurvey) (domain.Cell, bool) {
	open := map[domain.Cell]bool{}
	for _, c := range s.Cells {
		open[c.Cell] = c.Walkable && !c.Rock
	}
	for _, kind := range []ReservationKind{ReservePerimeter, ReserveKillbox} {
		for _, r := range reserved(p, kind) {
			for _, c := range rectCells(r) {
				open[c] = false
			}
		}
	}
	w, h := s.Bounds.Width, s.Bounds.Height
	seen := map[domain.Cell]bool{}
	var queue []domain.Cell
	for _, c := range s.Cells {
		if open[c.Cell] && (c.Cell.X == 0 || c.Cell.Z == 0 || c.Cell.X == w-1 || c.Cell.Z == h-1) {
			seen[c.Cell] = true
			queue = append(queue, c.Cell)
		}
	}
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		for _, r := range p.Rooms {
			if contains(r.Interior, c) {
				return c, true
			}
		}
		for _, d := range neighbours8 {
			n := addCell(c, d)
			if !open[n] || seen[n] || d.X != 0 && d.Z != 0 && (!open[domain.Cell{X: n.X, Z: c.Z}] || !open[domain.Cell{X: c.X, Z: n.Z}]) {
				continue
			}
			seen[n] = true
			queue = append(queue, n)
		}
	}
	return domain.Cell{}, false
}

func reservedCells(p LayoutPlan, kind ReservationKind) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, r := range reserved(p, kind) {
		for _, c := range rectCells(r) {
			out[c] = true
		}
	}
	return out
}

// wetPerimeter plans over plains with wet ground where soft says, and
// checks the ring holds: no leak, every wall cell standable (firm, or
// light footing marked light, or bridged water).
func wetPerimeter(t *testing.T, soft func(x, z int32) (SurveyCell, bool)) (LayoutPlan, MapSurvey) {
	t.Helper()
	cell := func(x, z int32) SurveyCell {
		if c, ok := soft(x, z); ok {
			return c
		}
		// Plain ground, not field soil: an all-fertile map is one field (#1281)
		// and would pull the ring out to the map edge.
		return SurveyCell{Walkable: true, Fertility: 0.7}
	}
	s := zoningSurvey(200, cell)
	p := PlanPerimeter(PlanCore(Zone(s), 3, BuildTierCamp), s)
	if !p.Valid() {
		t.Fatal("invalid plan")
	}
	checkPerimeter(t, p)
	if c, leak := perimeterLeak(p, s); leak {
		t.Fatal("raiders reach the core at", c)
	}
	light, bridges := reservedCells(p, ReservePerimeterLight), reservedCells(p, ReserveBridge)
	for c := range reservedCells(p, ReservePerimeter) {
		sc := cell(c.X, c.Z)
		switch {
		case sc.Footing == FootingFirm && !light[c] && !bridges[c]:
		case sc.Footing == FootingLight && light[c] && !bridges[c]:
		case sc.Footing == FootingNone && sc.Bridgeable && bridges[c] && !light[c]:
		default:
			t.Fatalf("wall at %v on %+v light=%v bridge=%v", c, sc, light[c], bridges[c])
		}
	}
	return p, s
}

// The ring on open plains, for placing water across it.
func plainsRing(t *testing.T) Rectangle {
	t.Helper()
	var ring Rectangle
	for _, r := range reserved(perimeterPlan(t, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 0.7} }), ReservePerimeter) {
		ring = unionRect(ring, r)
	}
	return ring
}

var shallowWater = SurveyCell{Walkable: true, Footing: FootingNone, Bridgeable: true, Dries: true}

func TestPerimeterShallowPondDetours(t *testing.T) {
	ring := plainsRing(t)
	// A pond lapping the ring's outer face: the wall steps inside it.
	face, cz := ring.X+ring.Width-1, ring.Z+ring.Height/2+6
	pond := func(x, z int32) (SurveyCell, bool) {
		return shallowWater, x >= face && x <= face+6 && z >= cz-3 && z <= cz+3
	}
	p, _ := wetPerimeter(t, pond)
	if len(reserved(p, ReserveBridge)) != 0 || len(reserved(p, ReservePerimeterGap)) != 0 {
		t.Fatal("a pond at the wall is walled around, not bridged", reserved(p, ReserveBridge))
	}
	inside := false
	for c := range reservedCells(p, ReservePerimeter) {
		inside = inside || c.X == face-perimeterThick && c.Z == cz
	}
	if !inside {
		t.Fatal("no detour wall inside the ring beside the pond")
	}
	// A pond deep into the ring costs more than perimeterDetour and is
	// bridged instead.
	p, _ = wetPerimeter(t, func(x, z int32) (SurveyCell, bool) {
		return shallowWater, x >= face-4 && x <= face+6 && z >= cz-3 && z <= cz+3
	})
	if len(reserved(p, ReserveBridge)) == 0 {
		t.Fatal("a deep pond is bridged")
	}
}

func TestPerimeterRiverIsBridged(t *testing.T) {
	ring := plainsRing(t)
	z0 := ring.Z + ring.Height - 12
	p, _ := wetPerimeter(t, func(x, z int32) (SurveyCell, bool) {
		c := shallowWater
		c.Dries = false
		return c, z >= z0 && z < z0+4
	})
	if len(reserved(p, ReserveBridge)) == 0 {
		t.Fatal("a river across the ring is bridged")
	}
}

func TestPerimeterMarshySoilTakesWood(t *testing.T) {
	ring := plainsRing(t)
	z0 := ring.Z + ring.Height - 12
	p, _ := wetPerimeter(t, func(x, z int32) (SurveyCell, bool) {
		return SurveyCell{Walkable: true, Footing: FootingLight, Bridgeable: true, Dries: true, Fertility: 1}, z >= z0 && z < z0+4
	})
	if len(reserved(p, ReservePerimeterLight)) == 0 || len(reserved(p, ReserveBridge)) != 0 {
		t.Fatal("marshy soil takes a wooden wall, no bridge")
	}
}

var marsh = SurveyCell{Walkable: true, Footing: FootingLight, Bridgeable: true, Dries: true, Fertility: 1}

// Moisture pump sites stand inside the wall on firm ground and cover every
// soft ring cell that dries; ground that never dries gets none (#954).
func TestPerimeterPumpsCoverDryingRing(t *testing.T) {
	ring := plainsRing(t)
	z0 := ring.Z + ring.Height - 12
	wet := func(x, z int32) bool { return z >= z0 && z < z0+4 }
	p, _ := wetPerimeter(t, func(x, z int32) (SurveyCell, bool) { return marsh, wet(x, z) })
	pumps, light := reservedCells(p, ReserveMoisturePump), reservedCells(p, ReservePerimeterLight)
	if len(pumps) == 0 || len(light) == 0 {
		t.Fatal("pumps", pumps, "light", len(light))
	}
	inner := pad(ring, -perimeterThick)
	for c := range pumps {
		if !contains(inner, c) || wet(c.X, c.Z) {
			t.Fatal("pump off firm ground inside the wall", c)
		}
	}
	for c := range light {
		covered := false
		for pump := range pumps {
			covered = covered || squaredDistance(c, pump) <= pumpRadiusSq
		}
		if !covered {
			t.Fatal("no pump dries", c)
		}
	}
	// Pumps standing on their sites keep them.
	s := zoningSurvey(200, func(x, z int32) SurveyCell {
		if pumps[domain.Cell{X: x, Z: z}] {
			return SurveyCell{Built: true, Fertility: 1}
		}
		if wet(x, z) {
			return marsh
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	})
	if again := PlanPerimeter(p, s); !samePerimeter(p, again) {
		t.Fatal("standing pumps moved", reservedCells(again, ReserveMoisturePump))
	}
	// Each pump section is the pump and a conduit run joining the network
	// cardinally, ending within connector reach of the pump.
	core := p.Rooms[0].Interior
	transmitter := domain.Cell{X: core.X, Z: core.Z}
	if none, _ := PerimeterPumps(p, "MoisturePump", "HiddenConduit", nil); len(none) != 0 {
		t.Fatal("pumps without a network")
	}
	sections, err := PerimeterPumps(p, "MoisturePump", "HiddenConduit", []domain.Cell{transmitter})
	if err != nil || len(sections) != len(pumps) {
		t.Fatal("pump sections", len(sections), err)
	}
	net := map[domain.Cell]bool{transmitter: true}
	for _, s := range sections {
		site := s.Buildings[0]
		if site.Definition() != "MoisturePump" || !pumps[site.Cell()] {
			t.Fatal("section without its pump", s.Name)
		}
		for _, b := range s.Buildings[1:] {
			if b.Definition() != "HiddenConduit" {
				t.Fatal("not a conduit", b)
			}
			net[b.Cell()] = true
		}
		reach := false
		for c := range net {
			reach = reach || chebyshev(c, site.Cell()) <= conduitReach
		}
		if !reach {
			t.Fatal("pump out of reach", site.Cell())
		}
	}
	seen, queue := map[domain.Cell]bool{transmitter: true}, []domain.Cell{transmitter}
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		for _, d := range []domain.Cell{{X: 1}, {X: -1}, {Z: 1}, {Z: -1}} {
			if n := (domain.Cell{X: c.X + d.X, Z: c.Z + d.Z}); net[n] && !seen[n] {
				seen[n] = true
				queue = append(queue, n)
			}
		}
	}
	if len(seen) != len(net) {
		t.Fatal("conduits off the network", len(net)-len(seen))
	}
	river := shallowWater
	river.Dries = false
	p, _ = wetPerimeter(t, func(x, z int32) (SurveyCell, bool) { return river, wet(x, z) })
	if len(reserved(p, ReserveMoisturePump)) != 0 {
		t.Fatal("pumps by ground that never dries")
	}
}

// The ring plans over the colony's own buildings as ground: walls standing
// on the planned cells change nothing.
func TestPerimeterReadsBuildingsAsGround(t *testing.T) {
	open := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} }
	p := perimeterPlan(t, open)
	walls := reservedCells(p, ReservePerimeter)
	s := zoningSurvey(200, func(x, z int32) SurveyCell {
		if walls[domain.Cell{X: x, Z: z}] {
			return SurveyCell{Built: true, Fertility: 1}
		}
		return open(x, z)
	})
	if !samePerimeter(p, PlanPerimeter(p, s)) {
		t.Fatal("standing walls moved the ring")
	}
}

func TestPerimeterDeepWaterSeals(t *testing.T) {
	ring := plainsRing(t)
	x0 := ring.X + ring.Width - 6
	p, _ := wetPerimeter(t, func(x, z int32) (SurveyCell, bool) {
		return SurveyCell{Footing: FootingNone}, x >= x0
	})
	for _, r := range reserved(p, ReservePerimeter) {
		if r.X+r.Width > x0 {
			t.Fatal("wall on deep water", r)
		}
	}
}

func TestPerimeterUnbridgeableGapIsFlagged(t *testing.T) {
	ring := plainsRing(t)
	z0 := ring.Z + ring.Height - 12
	cell := func(x, z int32) SurveyCell {
		if z >= z0 && z < z0+4 {
			return SurveyCell{Walkable: true, Footing: FootingNone}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	}
	s := zoningSurvey(200, cell)
	p := PlanPerimeter(PlanCore(Zone(s), 3, BuildTierCamp), s)
	if len(reserved(p, ReservePerimeterGap)) == 0 {
		t.Fatal("ground nothing closes is flagged")
	}
}

// A bridged stretch is laid by its own section just before its wall's: a
// wooden wall on a plain bridge, stone (stuff left empty) on a heavy one.
func TestPerimeterSectionsBridgeBeforeWall(t *testing.T) {
	ring := plainsRing(t)
	z0 := ring.Z + ring.Height - 12
	p, _ := wetPerimeter(t, func(x, z int32) (SurveyCell, bool) {
		return shallowWater, z >= z0 && z < z0+4
	})
	bridged := reservedCells(p, ReserveBridge)
	for _, tc := range []struct{ bridge, stuff string }{{PerimeterBridge, PerimeterLightStuff}, {PerimeterHeavyBridge, ""}} {
		sections, err := PerimeterSections(p, "Wall", "Door", tc.bridge)
		if err != nil {
			t.Fatal(err)
		}
		laid, walled := map[domain.Cell]int{}, 0
		for i, s := range sections {
			for _, b := range s.Buildings {
				switch {
				case b.Definition() == tc.bridge:
					laid[b.Cell()] = i
				case bridged[b.Cell()]:
					at, ok := laid[b.Cell()]
					if !ok || at != i-1 || b.Stuff() != tc.stuff {
						t.Fatalf("%s: wall at %v in section %d, bridge section %d (%v), stuff %q", tc.bridge, b.Cell(), i, at, ok, b.Stuff())
					}
					walled++
				}
			}
		}
		if walled != len(bridged) || len(laid) != len(bridged) {
			t.Fatal(tc.bridge, "walled", walled, "laid", len(laid), "of", len(bridged))
		}
	}
}

// A gate stands on each hallway's axis where the wall meets it: the main
// hallway's row on the east and west sides, each crossing's column on the
// north and south sides (#952).
func TestPerimeterGatesOnHallwayAxes(t *testing.T) {
	p := perimeterPlan(t, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	gates := reserved(p, ReserveGate)
	onAxis := func(vertical bool, a int32) bool {
		for _, g := range gates {
			if vertical && g.Height == 1 && g.Z == a || !vertical && g.Width == 1 && g.X == a {
				return true
			}
		}
		return false
	}
	if len(p.Spine) < 2 {
		t.Fatal("no crossing", p.Spine)
	}
	for _, s := range p.Spine {
		if alongX(s) && !onAxis(true, s.From.Z) || !alongX(s) && !onAxis(false, s.From.X) {
			t.Fatal("no gate on the axis of", s, gates)
		}
	}
}

// patchPerimeter plans the ring on plain ground with one field patch
// dx..dx+30 east of the core's right edge, from 10 below its top to 30
// above it, and reports the core box, the patch and the cells a raider
// reaches from the map edge (#1286). ground is the survey; nil is plain.
func patchPerimeter(t *testing.T, dx int32, ground func(x, z int32) SurveyCell) (core Rectangle, patch map[domain.Cell]bool, p LayoutPlan, outside map[domain.Cell]bool) {
	t.Helper()
	if ground == nil {
		ground = func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 0.7} }
	}
	s := zoningSurvey(200, ground)
	plan := PlanCore(Zone(s), 3, BuildTierCamp)
	for _, r := range plan.AllRooms() {
		core = unionRect(core, pad(r.Interior, 1))
	}
	for _, sg := range plan.Hallways() {
		core = unionRect(core, pad(rectOf(sg.From, sg.To), SpineWidth/2))
	}
	right, top := core.X+core.Width-1, core.Z+core.Height-1
	patch = map[domain.Cell]bool{}
	zone := LayoutZone{Kind: ZoneField}
	for z := top - 10; z < top+30; z++ {
		zone.Runs = append(zone.Runs, RowRun{Z: z, X: right + dx, Length: 30})
		for x := right + dx; x < right+dx+30; x++ {
			patch[domain.Cell{X: x, Z: z}] = true
		}
	}
	plan.Zones = append(plan.Zones, zone)
	p = PlanPerimeter(plan, s)
	checkPerimeter(t, p)
	if c, leak := perimeterLeak(p, s); leak {
		t.Fatal("raiders reach the core at", c)
	}
	// The raiders' flood, as perimeterLeak runs it.
	closed := reservedCells(p, ReservePerimeter)
	for c := range reservedCells(p, ReserveKillbox) {
		closed[c] = true
	}
	outside = map[domain.Cell]bool{}
	var queue []domain.Cell
	for x := int32(0); x < 200; x++ {
		for _, c := range []domain.Cell{{X: x}, {X: x, Z: 199}, {Z: x}, {X: 199, Z: x}} {
			if !outside[c] {
				outside[c] = true
				queue = append(queue, c)
			}
		}
	}
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		for _, d := range neighbours8 {
			n := addCell(c, d)
			if n.X < 0 || n.Z < 0 || n.X >= 200 || n.Z >= 200 || closed[n] || outside[n] || d.X != 0 && d.Z != 0 && (closed[domain.Cell{X: n.X, Z: c.Z}] || closed[domain.Cell{X: c.X, Z: n.Z}]) {
				continue
			}
			outside[n] = true
			queue = append(queue, n)
		}
	}
	// The wall itself, not the killbox (sited by #1287), stays a cell off.
	walls := reservedCells(p, ReservePerimeter)
	for c := range patch {
		for _, d := range append(neighbours8[:], domain.Cell{}) {
			if walls[addCell(c, d)] {
				t.Fatal("the wall crosses or touches the patch at", addCell(c, d))
			}
		}
	}
	return core, patch, p, outside
}

// A patch beside the core is taken in whole and the ring follows the L:
// the corner below the patch, which a rectangle would enclose, stays out.
func TestPerimeterFollowsEnclosedPatch(t *testing.T) {
	core, patch, p, outside := patchPerimeter(t, 5, nil)
	for c := range patch {
		if outside[c] {
			t.Fatal("patch cell outside the wall", c)
		}
	}
	right := core.X + core.Width - 1
	// Below the patch's own yard and ring, clear of the core's.
	top := core.Z + core.Height - 1
	if notch := (domain.Cell{X: right + 30, Z: top - 10 - perimeterGap - perimeterThick - 2}); !outside[notch] {
		t.Fatal("the ring is a rectangle: the notch below the patch is enclosed", notch)
	}
	var ring Rectangle
	for _, r := range reserved(p, ReservePerimeter) {
		ring = unionRect(ring, r)
	}
	if ring.X+ring.Width-1 < right+5+30+perimeterGap {
		t.Fatal("the ring stops short of the patch", ring)
	}
}

// A patch just beyond reach but inside the ring's clearance is taken in
// whole; one farther out stays wholly outside, clear of the wall.
func TestPerimeterNeverCrossesPatch(t *testing.T) {
	_, patch, p, outside := patchPerimeter(t, perimeterFieldReach+1, nil)
	for c := range patch {
		if outside[c] {
			t.Fatal("patch within the ring's clearance is split at", c)
		}
	}
	// The killbox stands off the patch (#1287).
	for c := range reservedCells(p, ReserveKillbox) {
		if patch[c] {
			t.Fatal("killbox on the patch at", c)
		}
	}
	_, patch, _, outside = patchPerimeter(t, perimeterGap+perimeterThick+3, nil)
	for c := range patch {
		if !outside[c] {
			t.Fatal("patch beyond reach enclosed at", c)
		}
	}
}

// The ring read back from the plan is the traced L, not its bounds: the
// patch lies inside, the notch below it does not (#1287).
func TestPerimeterInteriorIsTraced(t *testing.T) {
	core, patch, p, _ := patchPerimeter(t, 5, nil)
	wi, ok := planInterior(p, 0)
	if !ok {
		t.Fatal("no ring read back")
	}
	for c := range patch {
		if !wi.inside(c) {
			t.Fatal("patch cell not inside", c)
		}
	}
	right, top := core.X+core.Width-1, core.Z+core.Height-1
	if notch := (domain.Cell{X: right + 30, Z: top - 10 - perimeterGap - perimeterThick - 2}); wi.inside(notch) {
		t.Fatal("the notch below the patch reads as inside", notch)
	}
	for c := range reservedCells(p, ReservePerimeter) {
		if wi.inside(c) {
			t.Fatal("a wall cell reads as inside", c)
		}
	}
}

// On an L-shaped ring every dry face long enough for a gate holds one, and
// the killbox sits where the one way in meets the ring: a corridor from
// the east map edge onto the patch's face (#1287).
func TestPerimeterLGatesEveryFaceKillboxOnApproach(t *testing.T) {
	core, _, p, _ := patchPerimeter(t, 5, nil)
	plan := p
	plan.Reservations = nil
	enc := planEnclosure(plan, core, 200, 200)
	sides, _ := enc.sides()
	gates := reservedCells(p, ReserveGate)
	kb := reservedCells(p, ReserveKillbox)
	for _, sd := range sides {
		if sd.hi-sd.lo+1 < perimeterGatePitch {
			continue
		}
		hit := false
		for q := sd.lo; q <= sd.hi && !hit; q++ {
			hit = gates[sd.base(q)] || kb[sd.cell(q, perimeterThick)]
		}
		if !hit {
			t.Fatal("no gate on the face", sd)
		}
	}

	right, top := core.X+core.Width-1, core.Z+core.Height-1
	mouth := top + 10 // the patch runs top-10..top+29
	_, _, p, _ = patchPerimeter(t, 5, func(x, z int32) SurveyCell {
		if x >= 15 && x < 185 && z >= 15 && z < 185 || x >= 185 && z >= mouth-3 && z <= mouth+3 {
			return SurveyCell{Walkable: true, Fertility: 0.7}
		}
		return SurveyCell{Rock: true}
	})
	k := reserved(p, ReserveKillbox)
	if len(k) != 1 {
		t.Fatal("killbox", k)
	}
	if k[0].X < right+5+30 || k[0].Z > mouth+killboxHalf || k[0].Z+k[0].Height-1 < mouth-killboxHalf {
		t.Fatal("killbox off the east mouth", k[0], "mouth z", mouth)
	}
}

// On an all-fertile map the patch is enclosed as far as the edge margin
// allows: the ring runs along the margin line, crossing the patch there,
// and the opening falls back onto the patch rather than none (#1287).
func TestPerimeterAllFertileStopsAtMargin(t *testing.T) {
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1.4} })
	plan := PlanCore(Zone(s), 3, BuildTierCamp)
	zone := LayoutZone{Kind: ZoneField}
	for z := int32(0); z < 200; z++ {
		zone.Runs = append(zone.Runs, RowRun{Z: z, X: 0, Length: 200})
	}
	plan.Zones = append(plan.Zones, zone)
	p := PlanPerimeter(plan, s)
	checkPerimeter(t, p)
	onLine := false
	for _, kind := range []ReservationKind{ReservePerimeter, ReserveGate} {
		for c := range reservedCells(p, kind) {
			if c.X < LayoutEdgeMargin || c.Z < LayoutEdgeMargin || c.X >= 200-LayoutEdgeMargin || c.Z >= 200-LayoutEdgeMargin {
				t.Fatal("wall inside the edge margin at", c)
			}
			onLine = onLine || c.X == LayoutEdgeMargin || c.Z == LayoutEdgeMargin
		}
	}
	if !onLine {
		t.Fatal("the ring stops short of the margin line")
	}
}
