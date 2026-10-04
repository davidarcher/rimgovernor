package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Housing blocks for the new generator (#1956, epic #1938). A wing (bedroom
// wing or suite block) is sited as one straight unit: which east-west
// hallway it hangs off, its column and its direction (north or south of
// that hallway), its length fixed at siting (a bedroom wing wingMaxRooms
// rooms, a suite block at most suiteMaxRooms). Several are sited, each on
// the best ground left: the most rooms at full size first, then the least
// soil under it, then the shortest walk from dining (every bedroom of the
// wing counts its trip to dining in the walking term, so a wing near
// dining is the cheaper one). Each wing's corridor opens off its hallway,
// which CheckRoutes proves reachable.

// hallGround is whether a rectangle is core ground clear of rooms' walls
// and of the hallways' bands (a north-south crossing keeps a room's depth
// beyond each end, so rooms can still be placed along it).
func (g coreGrid) hallGround(spine []SpineSegment, rooms []LayoutRoom) func(Rectangle) bool {
	taken := map[domain.Cell]bool{}
	for _, r := range rooms {
		for _, c := range rectCells(roomWalls(r)) {
			taken[c] = true
		}
	}
	half := SpineWidth/2 + coreMaxDepth + 2
	for _, s := range spine {
		band := spineRects([]SpineSegment{s})[0]
		if !alongX(s) {
			band.Z, band.Height = band.Z-half, band.Height+2*half
		}
		for _, c := range rectCells(band) {
			taken[c] = true
		}
	}
	return func(r Rectangle) bool {
		for _, c := range rectCells(r) {
			if !g.core[c] || taken[c] {
				return false
			}
		}
		return true
	}
}

// blockSite is where a wing goes: its frame and the index of the hallway
// it hangs off.
type blockSite struct {
	f   wingFrame
	seg int
	n   int
}

// siteBlock picks the best site for one wing among every east-west
// hallway's columns and both directions. count is how many rooms the wing
// fits at a frame; a frame fitting none is no site. near is the cell the
// wing wants to be close to (dining's door).
func (g coreGrid) siteBlock(spine []SpineSegment, rooms []LayoutRoom, near domain.Cell, count func(wingFrame) int) (blockSite, bool) {
	walls := map[domain.Cell]bool{}
	for _, r := range rooms {
		for _, c := range rectCells(roomWalls(r)) {
			walls[c] = true
		}
	}
	var best blockSite
	var bestCost int
	var bestD int64
	found := false
	for i, s := range spine {
		if !alongX(s) {
			continue
		}
		z0 := s.From.Z
		lo, hi := min(s.From.X, s.To.X), max(s.From.X, s.To.X)
		reaches := func(cx int32) bool {
			for x := min(cx-1, lo); x <= max(cx+1, hi); x++ {
				for dz := -SpineWidth / 2; dz <= SpineWidth/2; dz++ {
					c := domain.Cell{X: x, Z: z0 + dz}
					if !g.core[c] || walls[c] {
						return false
					}
				}
			}
			return true
		}
		for cx := lo - spineMaxLen/2; cx <= hi+spineMaxLen/2; cx++ {
			if !reaches(cx) {
				continue
			}
			for _, sign := range []int32{1, -1} {
				try := wingFrame{cx: cx, z0: z0, sign: sign}
				n := count(try)
				if n == 0 {
					continue
				}
				dx, dz := int64(cx-near.X), int64(try.z(2)-near.Z)
				d := dx*dx + dz*dz
				cost := g.soilCost(try.ground(int32(n), coreMaxDepth))
				if !found || n > best.n || n == best.n && (cost < bestCost || cost == bestCost && d < bestD) {
					best, bestCost, bestD, found = blockSite{f: try, seg: i, n: n}, cost, d, true
				}
			}
		}
	}
	return best, found
}

// openBlock stretches hallway seg to reach f's corridor.
func openBlock(spine []SpineSegment, seg int, f wingFrame) []SpineSegment {
	spine = append([]SpineSegment(nil), spine...)
	s := &spine[seg]
	lo, hi := min(s.From.X, s.To.X, f.cx-1), max(s.From.X, s.To.X, f.cx+1)
	s.From.X, s.To.X = lo, hi
	return spine
}

// diningAnchor is dining's door, else the storage room's (#1178), else the
// first hallway's middle: the cell housing wants to be near.
func diningAnchor(spine []SpineSegment, rooms []LayoutRoom) domain.Cell {
	for _, role := range []ModuleRole{ModuleDining, ModuleStorage} {
		for _, r := range rooms {
			if r.Role == role {
				return r.Door
			}
		}
	}
	if len(spine) > 0 {
		return domain.Cell{X: (spine[0].From.X + spine[0].To.X) / 2, Z: spine[0].From.Z}
	}
	return domain.Cell{}
}

// siteBedWings sites bedroom wings until they hold pawns rooms: each a
// straight block of wingMaxRooms standard rooms of tier's size (#1214),
// sited by siteBlock. Existing wings are never touched; rooms that do not
// fit are left out. g is the core before any bedroom wing's ground is
// carved out of it.
func (g coreGrid) siteBedWings(spine []SpineSegment, rooms []LayoutRoom, wings []Wing, pawns int, tier BuildTier) ([]SpineSegment, []Wing) {
	owed := pawns
	for _, i := range bedroomWings(wings) {
		owed -= len(wings[i].Rooms)
	}
	size := WingRoomSize(tier)
	for owed > 0 {
		o := g.clone()
		o.carveBedroomWings(wings)
		o.carveSuiteWings(wings)
		free := o.hallGround(spine, rooms)
		fits := func(f wingFrame, k int) bool {
			return free(roomWalls(f.room(k))) && free(f.corridor(int32(k/2)+1))
		}
		site, ok := o.siteBlock(spine, rooms, diningAnchor(spine, rooms), func(try wingFrame) int {
			try.size = size
			n := 0
			for n < wingMaxRooms && fits(try, n) {
				n++
			}
			return n
		})
		if !ok {
			break
		}
		site.f.size = size
		grown := openBlock(spine, site.seg, site.f)
		next := o.planWing(grown, rooms, wings, site.f, fits)
		if len(next) == len(wings) {
			break
		}
		spine, wings = grown, next
		owed -= len(wings[len(wings)-1].Rooms)
	}
	return spine, wings
}

// siteSuiteBlocks sites suite blocks for the targets past the suites the
// plan holds (index-aligned with its suites), each block of at most
// suiteMaxRooms suites sized by SuiteSize at siting (#1951). Existing
// suites never move or change; suites that do not fit are left out. g is
// the core with the existing wings' ground carved out.
func (g coreGrid) siteSuiteBlocks(spine []SpineSegment, rooms []LayoutRoom, wings []Wing, targets []float64) ([]SpineSegment, []Wing) {
	have := 0
	for _, i := range suiteWings(wings) {
		have += len(wings[i].Rooms)
	}
	wings = append([]Wing(nil), wings...)
	for have < len(targets) {
		chunk := targets[have:min(have+suiteMaxRooms, len(targets))]
		free := g.hallGround(spine, rooms)
		fitting := func(f wingFrame) []LayoutRoom {
			var placed []LayoutRoom
			for _, r := range packSuites(f, chunk) {
				v0, w := f.along(r)
				if !free(roomWalls(r)) || !free(f.corridorTo(v0+w)) {
					break
				}
				placed = append(placed, r)
			}
			return placed
		}
		site, ok := g.siteBlock(spine, rooms, diningAnchor(spine, rooms), func(try wingFrame) int { return len(fitting(try)) })
		if !ok {
			break
		}
		f := site.f
		grown := openBlock(spine, site.seg, f)
		base := domain.Cell{X: f.cx, Z: f.z(2)}
		w := Wing{Purpose: WingSuites, Corridor: SpineSegment{From: base, To: base}}
		for _, r := range fitting(f) {
			r.Dug = g.dug(r)
			trial := w
			trial.Rooms = append(append([]LayoutRoom(nil), w.Rooms...), r)
			trial.Corridor.To = f.reach(trial.Rooms)
			next := append(append([]Wing(nil), wings...), trial)
			if _, err := CheckRoutes(LayoutPlan{Spine: grown, Entrances: spineEntrances(grown), Rooms: rooms, Wings: next}); err != nil {
				break
			}
			w = trial
		}
		if len(w.Rooms) == 0 {
			break
		}
		spine, wings = grown, append(wings, w)
		g.carve(wingReserve(w))
		have += len(w.Rooms)
	}
	return spine, wings
}
