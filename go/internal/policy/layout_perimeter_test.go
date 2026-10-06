package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

func perimeterPlan(t *testing.T, cell func(x, z int32) SurveyCell) LayoutPlan {
	t.Helper()
	return perimeterPlanAt(t, nil, cell)
}

// perimeterPlanAt is perimeterPlan with the plan generated from seed (the
// core candidates' centroid when nil), so a fixture can set where the base
// stands in its valley.
func perimeterPlanAt(t *testing.T, seed *domain.Cell, cell func(x, z int32) SurveyCell) LayoutPlan {
	t.Helper()
	s := zoningSurvey(200, cell)
	plan := corePlan(Zone(s), 3, BuildTierCamp)
	if seed != nil {
		plan = newCoreGrid(Zone(s), nil).generate(LayoutPlan{Zones: Zone(s)}, *seed, 3, 1, BuildTierCamp)
	}
	p := PlanPerimeter(plan, s)
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
		// A stepped outline splits its wall into several rectangles, so a
		// gate may span two of them: every gate cell is a wall cell.
		in := true
		for _, c := range rectCells(g) {
			cell := false
			for _, w := range walls {
				cell = cell || contains(w, c)
			}
			in = in && cell
		}
		if !in {
			t.Fatal("gate off the wall", g)
		}
	}
	kb := reserved(p, ReserveKillbox)
	if len(kb) != 1 || len(reserved(p, ReserveKillboxApproach)) != 2 {
		t.Fatal("killbox", kb)
	}
	// The approach leads into the killbox, so the defense layout reads a
	// funnel from it.
	if _, _, _, ok := LayoutKillbox(p, Bounds{Width: 200, Height: 200}); !ok {
		t.Fatal("approach does not lead into the killbox", kb, reserved(p, ReserveKillboxApproach))
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
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	p := perimeterPlan(t, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	checkPerimeter(t, p)
	// Replanning replaces, never duplicates.
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	if again := PlanPerimeter(p, s); len(again.Reservations) != len(p.Reservations) {
		t.Fatal("replan duplicated")
	}
}

// The opening carries a fence flush with the wall (#2231): three cells
// across the lane, the killbox's width in the wall, and nothing else of the
// plan changes. The census reads a Fence cell as Passable (PassThroughOnly,
// not Impassable), so the arrival flood and LayoutKillbox, which read the
// plan's walls and approach legs only, are the same with the fence in place.
func TestPerimeterFencesTheOpening(t *testing.T) {
	p := perimeterPlan(t, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	fences := reserved(p, ReserveKillboxFence)
	if len(fences) != 1 || fences[0].Width*fences[0].Height != 3 {
		t.Fatal("fence", fences)
	}
	walls := reservedCells(p, ReservePerimeter)
	for c := range reservedCells(p, ReserveKillboxFence) {
		if walls[c] {
			t.Fatal("fence on a wall cell", c)
		}
	}
	bounds := Bounds{Width: 200, Height: 200}
	k, region, home, ok := LayoutKillbox(p, bounds)
	if !ok {
		t.Fatal("no killbox")
	}
	bare := p
	bare.Reservations = nil
	for _, r := range p.Reservations {
		if r.Kind != ReserveKillboxFence {
			bare.Reservations = append(bare.Reservations, r)
		}
	}
	k2, region2, home2, _ := LayoutKillbox(bare, bounds)
	if !reflect.DeepEqual(k, k2) || region != region2 || home != home2 {
		t.Fatal("fence changed the killbox read", k, k2)
	}
	sections, err := FenceSections(p, "Fence", "WoodLog")
	if err != nil || len(sections) != 1 || len(sections[0].Buildings) != 3 || !IsPerimeterTier(sections[0].Name) || IsCorePerimeterTier(sections[0].Name) {
		t.Fatal(sections, err)
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
	// Raiders drift sideways crossing the valley floor between the mouth
	// (z 40) and the wall, so the killbox may sit that far off it.
	drift := kb.Z - 40
	if c := kb.X + kb.Width/2; c < 95-drift || c > 105+drift {
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
	p := PlanPerimeter(corePlan(Zone(s), 3, BuildTierCamp), s)
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
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
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
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
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
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
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
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
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
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
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
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
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
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	ring := plainsRing(t)
	z0 := ring.Z + ring.Height - 12
	cell := func(x, z int32) SurveyCell {
		if z >= z0 && z < z0+4 {
			return SurveyCell{Walkable: true, Footing: FootingNone}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	}
	s := zoningSurvey(200, cell)
	p := PlanPerimeter(corePlan(Zone(s), 3, BuildTierCamp), s)
	if len(reserved(p, ReservePerimeterGap)) == 0 {
		t.Fatal("ground nothing closes is flagged")
	}
}

// A bridged stretch is laid by its own section just before its wall's: a
// wooden wall on a plain bridge, stone (stuff left empty) on a heavy one.
func TestPerimeterSectionsBridgeBeforeWall(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	ring := plainsRing(t)
	z0 := ring.Z + ring.Height - 12
	p, _ := wetPerimeter(t, func(x, z int32) (SurveyCell, bool) {
		return shallowWater, z >= z0 && z < z0+4
	})
	bridged := reservedCells(p, ReserveBridge)
	for _, tc := range []struct{ bridge, stuff string }{{PerimeterBridge, PerimeterLightStuff}, {PerimeterHeavyBridge, ""}} {
		sections, err := PerimeterSections(p, "Wall", "Door", tc.bridge, nil)
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
					if !ok || at >= i || b.Stuff() != tc.stuff {
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
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
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

// A rich patch beside the core stays outside the ring: the ring bounds are
// the core, its yard and the killbox only, and no inner wall or gate is
// planned (#1591).
func TestPerimeterLeavesRichPatchOutside(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	ground := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true} }
	s := zoningSurvey(200, ground)
	plan := corePlan(Zone(s), 3, BuildTierCamp)
	core := footprintBox(plan, 200, 200)
	right, top := core.X+core.Width-1, core.Z+core.Height-1
	off := perimeterGap + perimeterThick + 3
	patch := map[domain.Cell]bool{}
	zone := LayoutZone{Kind: ZoneField}
	for z := top - 10; z < top+30; z++ {
		zone.Runs = append(zone.Runs, RowRun{Z: z, X: right + off, Length: 30})
		for x := right + off; x < right+off+30; x++ {
			patch[domain.Cell{X: x, Z: z}] = true
		}
	}
	plan.Zones = append(plan.Zones, zone)
	s = zoningSurvey(200, func(x, z int32) SurveyCell {
		c := ground(x, z)
		if patch[domain.Cell{X: x, Z: z}] {
			c.Fertility = 1.4
		}
		return c
	})
	p := PlanPerimeter(plan, s)
	checkPerimeter(t, p)
	var ring Rectangle
	for _, r := range reserved(p, ReservePerimeter) {
		ring = unionRect(ring, r)
	}
	if lim := pad(core, perimeterGap+perimeterThick); ring.Width == 0 || unionRect(lim, ring) != lim {
		t.Fatal("ring", ring, "reaches past the core and its yard", lim)
	}
	wi, ok := planInterior(p, 0)
	if !ok {
		t.Fatal("no ring read back")
	}
	for c := range patch {
		if i, ok := wi.at(c); ok && wi.in[i] {
			t.Fatal("patch cell inside the ring", c)
		}
	}
	for _, r := range p.Reservations {
		if r.Kind == "inner_wall" || r.Kind == "inner_gate" {
			t.Fatal("inner reservation planned", r)
		}
	}
}

// On an all-soil map the opening once fell on the turbine lane: the
// killbox keeps clear of every utility reservation.
func TestPerimeterKillboxClearOfUtilities(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	p, ok := DeriveLayoutPlan(s, 3, BuildTierCamp, nil, 30, 0).Value()
	if !ok {
		t.Fatal("no plan")
	}
	k := reserved(p, ReserveKillbox)
	if len(k) != 1 || min(k[0].Width, k[0].Height) != 2*killboxHalf+1 || max(k[0].Width, k[0].Height) != killboxRows {
		t.Fatal("killbox", k)
	}
	for _, r := range p.Reservations {
		if !perimeterKinds[r.Kind] && rectsOverlap(k[0], r.Area) {
			t.Fatal(r.Kind, r.Area, "inside the killbox", k[0])
		}
	}
}

// Plain soil is not walled in: on a map of 100% soil the ring stays near
// the core instead of running out to the edge margin.
func TestPerimeterPlainSoilStaysNearCore(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	p, ok := DeriveLayoutPlan(s, 3, BuildTierCamp, nil, 30, 0).Value()
	if !ok {
		t.Fatal("no plan")
	}
	var rooms, ring Rectangle
	for _, r := range p.AllRooms() {
		rooms = unionRect(rooms, r.Interior)
	}
	// The herd sites sit inside the ring beside the few essential rooms.
	for _, r := range p.Reservations {
		if !perimeterKinds[r.Kind] {
			rooms = unionRect(rooms, r.Area)
		}
	}
	for _, r := range reserved(p, ReservePerimeter) {
		ring = unionRect(ring, r)
	}
	if lim := pad(rooms, 2*perimeterGap+perimeterThick); ring.Width == 0 || unionRect(lim, ring) != lim {
		t.Fatal("ring", ring, "runs far past the rooms", rooms)
	}
}

// A mouth at the valley's corner draws the opening to the ring's corner;
// the killbox stays whole across its opening there, not clipped sideways
// off the approach.
func TestPerimeterKillboxWholeAtACorner(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	// The base stands near the valley's corner, so its ring reaches it.
	seed := domain.Cell{X: 70, Z: 80}
	for _, mouth := range []int32{0, 2, 4, 6, 8, 10, 14} {
		p := perimeterPlanAt(t, &seed, func(x, z int32) SurveyCell {
			open := x >= 30 && x < 170 && z >= 40 && z < 170 || x >= 30+mouth && x <= 36+mouth && z < 40
			if open {
				return SurveyCell{Walkable: true, Fertility: 1}
			}
			return SurveyCell{Rock: true}
		})
		checkPerimeter(t, p)
	}
}
