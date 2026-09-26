package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The master layout plan (#727) replaces the grid centred on the starter
// shell: the whole map is scored once at settle time, the grid's origin
// and orientation are the best-scoring placement, and every module inside
// the plan's radius is reserved for a concrete late-game role (20-30+
// pawns, most of the map). Site searches fill the reserved slots instead of
// searching a whole district wedge; the plan is replanned only on a
// deliberate trigger (ReplanNeeded) and a replan keeps every slot that is
// still sound (Replan).

// SurveyCell is one map cell of the settle-time survey. Absent cells are
// unknown and score as unbuildable.
type SurveyCell struct {
	Cell domain.Cell
	// Walkable is open ground a wall can stand on; Rock is natural rock a
	// module is mined out of; Marsh is marsh, mud or shallow water no
	// module is built on.
	Walkable, Rock, Marsh bool
	// ThickRoof is overhead mountain: no drop pods, no roof collapse from
	// mining, cold storage.
	ThickRoof bool
	// Fertility is the soil's growing multiplier (0 for rock and floors).
	Fertility float64
}

// MapSurvey is the whole map, scored once at settle time.
type MapSurvey struct {
	Bounds Bounds
	Cells  []SurveyCell
}

// ModuleRole is a master-plan module's reserved future role.
type ModuleRole string

const (
	ModulePlaza    ModuleRole = "plaza"
	ModuleHousing  ModuleRole = "housing"
	ModuleHospital ModuleRole = "hospital"
	ModulePrison   ModuleRole = "prison"
	ModuleKitchen  ModuleRole = "kitchen"
	ModuleFreezer  ModuleRole = "freezer"
	ModuleStorage  ModuleRole = "storage"
	ModuleWorkshop ModuleRole = "workshop"
	ModuleFields   ModuleRole = "fields"
	// ModuleKillbox is a defense-ring module an open approach reaches: the
	// natural choke the plan funnels raids through.
	ModuleKillbox ModuleRole = "killbox"
	// ModuleWall is a defense-ring module rock or unbuildable ground
	// already seals; it holds a wall line only.
	ModuleWall ModuleRole = "wall"
	// ModuleReserve is sound ground held for growth.
	ModuleReserve ModuleRole = "reserve"
	// ModuleUnusable is ground no module is built on (marsh, the map edge).
	ModuleUnusable ModuleRole = "unusable"
)

const (
	// MasterPlanEdgeMargin is how far from the map edge nothing is built:
	// raiders and the map edge's fog make the band useless.
	MasterPlanEdgeMargin int32 = 10
	// MasterPlanRadius is the defense ring's Chebyshev module distance: a
	// 7x7-module plan, 112 cells a side, holds a 30-pawn colony.
	MasterPlanRadius int32 = 3
	// masterPlanStep is the origin search's stride in cells.
	masterPlanStep int32 = 4
	// masterPlanSound is the buildable fraction a module needs to hold a
	// room.
	masterPlanSound = 0.75
	// masterPlanSealRock is how many rock cells between a defense module
	// and the map edge seal that approach (a mountain-backed edge).
	masterPlanSealRock = 6
	// masterPlanPawnsPerHousing is the bedrooms one housing module holds
	// (four 5x5 sub-cells).
	masterPlanPawnsPerHousing = 4
)

// PlanModule is one reserved module of the master plan.
type PlanModule struct {
	// U, V are module coordinates: the plaza is (0,0).
	U, V int32
	Role ModuleRole
}

// MasterPlan is the grid and its reserved modules.
type MasterPlan struct {
	Grid    ColonyGrid
	Radius  int32
	Modules []PlanModule
	// Score is the placement score the origin search maximised.
	Score float64
}

// ColonyGridFromSurvey sets the origin from the master plan's whole-map
// score.
const ColonyGridFromSurvey ColonyGridSource = "map_survey"

// planAxes are the four right-handed map-aligned orientations.
var planAxes = [4][2]domain.Cell{
	{{X: 1}, {Z: 1}},
	{{Z: 1}, {X: -1}},
	{{X: -1}, {Z: -1}},
	{{Z: -1}, {X: 1}},
}

// surveyPlanes holds summed-area tables over the survey, so any module's
// totals cost four lookups.
type surveyPlanes struct {
	w, h                  int32
	build, fertile, thick []float64
	rockRow, rockCol      []float64 // per-row and per-column running rock counts
	edge                  int32
}

func newSurveyPlanes(s MapSurvey) surveyPlanes {
	w, h := s.Bounds.Width, s.Bounds.Height
	p := surveyPlanes{w: w, h: h, edge: MasterPlanEdgeMargin}
	n := int((w + 1) * (h + 1))
	p.build, p.fertile, p.thick = make([]float64, n), make([]float64, n), make([]float64, n)
	p.rockRow, p.rockCol = make([]float64, int((w+1)*h)), make([]float64, int((h+1)*w))
	build, fertile, thick, rock := make([]float64, w*h), make([]float64, w*h), make([]float64, w*h), make([]float64, w*h)
	for _, c := range s.Cells {
		x, z := c.Cell.X, c.Cell.Z
		if x < 0 || z < 0 || x >= w || z >= h {
			continue
		}
		i := z*w + x
		inside := x >= p.edge && z >= p.edge && x < w-p.edge && z < h-p.edge
		switch {
		case !inside || c.Marsh:
		case c.Rock:
			build[i] = 0.8
		case c.Walkable:
			build[i] = 1
		}
		if c.Walkable && !c.ThickRoof && !c.Marsh {
			fertile[i] = c.Fertility
		}
		if c.ThickRoof {
			thick[i] = 1
		}
		if c.Rock {
			rock[i] = 1
		}
	}
	sat := func(dst, src []float64) {
		for z := int32(0); z < h; z++ {
			for x := int32(0); x < w; x++ {
				dst[(z+1)*(w+1)+x+1] = src[z*w+x] + dst[z*(w+1)+x+1] + dst[(z+1)*(w+1)+x] - dst[z*(w+1)+x]
			}
		}
	}
	sat(p.build, build)
	sat(p.fertile, fertile)
	sat(p.thick, thick)
	for z := int32(0); z < h; z++ {
		for x := int32(0); x < w; x++ {
			p.rockRow[z*(w+1)+x+1] = p.rockRow[z*(w+1)+x] + rock[z*w+x]
			p.rockCol[x*(h+1)+z+1] = p.rockCol[x*(h+1)+z] + rock[z*w+x]
		}
	}
	return p
}

// sum totals a table over a rectangle, clipped to the map.
func (p surveyPlanes) sum(t []float64, r Rectangle) float64 {
	x0, z0, x1, z1 := max(r.X, 0), max(r.Z, 0), min(r.X+r.Width, p.w), min(r.Z+r.Height, p.h)
	if x0 >= x1 || z0 >= z1 {
		return 0
	}
	W := p.w + 1
	return t[z1*W+x1] - t[z0*W+x1] - t[z1*W+x0] + t[z0*W+x0]
}

// moduleStats are a module's buildable, fertile and thick-roof fractions.
type moduleStats struct{ build, fertile, thick float64 }

func (p surveyPlanes) stats(r Rectangle) moduleStats {
	area := float64(r.Width * r.Height)
	return moduleStats{p.sum(p.build, r) / area, p.sum(p.fertile, r) / area, p.sum(p.thick, r) / area}
}

// sealed reports whether the approach from a module outward along dir to
// the map edge crosses enough rock to count as mountain-backed.
func (p surveyPlanes) sealed(r Rectangle, dir domain.Cell) bool {
	cx, cz := r.X+r.Width/2, r.Z+r.Height/2
	if cx < 0 || cz < 0 || cx >= p.w || cz >= p.h {
		return true
	}
	var rock float64
	switch {
	case dir.X > 0:
		rock = p.rockRow[cz*(p.w+1)+p.w] - p.rockRow[cz*(p.w+1)+min(r.X+r.Width, p.w)]
	case dir.X < 0:
		rock = p.rockRow[cz*(p.w+1)+max(r.X, 0)]
	case dir.Z > 0:
		rock = p.rockCol[cx*(p.h+1)+p.h] - p.rockCol[cx*(p.h+1)+min(r.Z+r.Height, p.h)]
	default:
		rock = p.rockCol[cx*(p.h+1)+max(r.Z, 0)]
	}
	return rock >= masterPlanSealRock
}

// moduleRect is module (mu, mv)'s exterior rectangle.
func (g ColonyGrid) moduleRect(mu, mv int32) Rectangle {
	return g.rectangle(mu*g.Pitch, mv*g.Pitch, ColonyGridModule, ColonyGridModule)
}

// outward lists a ring module's outward map directions.
func (g ColonyGrid) outward(mu, mv, radius int32) []domain.Cell {
	axes := g.axes()
	var out []domain.Cell
	for i, m := range [2]int32{mu, mv} {
		if m == radius {
			out = append(out, axes[i])
		} else if m == -radius {
			out = append(out, domain.Cell{X: -axes[i].X, Z: -axes[i].Z})
		}
	}
	return out
}

// scorePlacement scores one grid over the survey: sound modules near the
// plaza, fertile open ground, thick roof for cold storage, and a defense
// ring that rock seals except at a few chokes.
func scorePlacement(p surveyPlanes, g ColonyGrid, radius int32) (float64, bool) {
	plaza := p.stats(g.moduleRect(0, 0))
	if plaza.build < 0.9 {
		return 0, false
	}
	score := 4 * plaza.build
	var fertile []float64
	open := 0
	for mu := -radius; mu <= radius; mu++ {
		for mv := -radius; mv <= radius; mv++ {
			ring := max(mu, -mu, mv, -mv)
			if ring == 0 {
				continue
			}
			r := g.moduleRect(mu, mv)
			s := p.stats(r)
			if ring == radius {
				sealed := true
				for _, d := range g.outward(mu, mv, radius) {
					sealed = sealed && p.sealed(r, d)
				}
				if !sealed && s.build >= masterPlanSound {
					open++
				}
				continue
			}
			if s.build >= masterPlanSound {
				score += s.build / float64(ring)
				score += 0.5 * s.thick / float64(ring)
			}
			fertile = append(fertile, s.fertile)
		}
	}
	sort.Sort(sort.Reverse(sort.Float64Slice(fertile)))
	for i := 0; i < len(fertile) && i < 8; i++ {
		score += 0.5 * fertile[i]
	}
	// Two open approaches is a natural choke; every one past that is a
	// wall the colony must hold.
	score -= 0.5 * float64(max(open-2, 0))
	if open == 0 {
		// A fully sealed ring still needs one way out for caravans; do not
		// reward a pocket over a choke.
		score -= 0.25
	}
	return score, true
}

// DeriveMasterPlan scores every placement of a radius-MasterPlanRadius
// grid over the survey (origins on a masterPlanStep stride, four
// orientations) and assigns the best one's modules. It is unknown when no
// placement's plaza is sound. Identical surveys give identical plans.
func DeriveMasterPlan(survey MapSurvey, pawns int) domain.Fact[MasterPlan] {
	p := newSurveyPlanes(survey)
	best, found := MasterPlan{}, false
	for z := p.edge; z < p.h-p.edge; z += masterPlanStep {
		for x := p.edge; x < p.w-p.edge; x += masterPlanStep {
			for _, axes := range planAxes {
				g := ColonyGrid{Origin: domain.Cell{X: x, Z: z}, Pitch: GridPitch, Axes: axes, Source: ColonyGridFromSurvey}
				score, ok := scorePlacement(p, g, MasterPlanRadius)
				if ok && (!found || score > best.Score) {
					best, found = MasterPlan{Grid: g, Radius: MasterPlanRadius, Score: score}, true
				}
			}
		}
	}
	if !found {
		return domain.Unknown[MasterPlan]()
	}
	best.Modules = assignModules(p, best.Grid, best.Radius, pawns, nil)
	return domain.Known(best)
}

// assignModules reserves each module's role. keep holds roles a replan
// carries over; they are assigned first and never moved.
func assignModules(p surveyPlanes, g ColonyGrid, radius int32, pawns int, keep map[[2]int32]ModuleRole) []PlanModule {
	role := map[[2]int32]ModuleRole{{0, 0}: ModulePlaza}
	stats := map[[2]int32]moduleStats{}
	var interior [][2]int32
	for mu := -radius; mu <= radius; mu++ {
		for mv := -radius; mv <= radius; mv++ {
			m := [2]int32{mu, mv}
			r := g.moduleRect(mu, mv)
			s := p.stats(r)
			stats[m] = s
			switch ring := max(mu, -mu, mv, -mv); {
			case ring == 0:
			case s.build < masterPlanSound:
				role[m] = ModuleUnusable
			case ring == radius:
				sealed := true
				for _, d := range g.outward(mu, mv, radius) {
					sealed = sealed && p.sealed(r, d)
				}
				role[m] = ModuleKillbox
				if sealed {
					role[m] = ModuleWall
				}
			default:
				interior = append(interior, m)
			}
		}
	}
	for m, r := range keep {
		if _, ok := stats[m]; ok && role[m] == "" && r != ModuleReserve {
			role[m] = r
		}
	}
	dist := func(m [2]int32) int32 { return m[0]*m[0] + m[1]*m[1] }
	adjacent := func(m [2]int32, want ModuleRole) bool {
		for _, d := range [4][2]int32{{1, 0}, {-1, 0}, {0, 1}, {0, -1}} {
			if role[[2]int32{m[0] + d[0], m[1] + d[1]}] == want {
				return true
			}
		}
		return false
	}
	has := func(want ModuleRole) int {
		n := 0
		for _, r := range role {
			if r == want {
				n++
			}
		}
		return n
	}
	// pick reserves count more modules for want, best key first (ties by
	// distance to the plaza, then coordinates).
	pick := func(want ModuleRole, count int, key func(m [2]int32) float64) {
		for has(want) < count {
			var free [][2]int32
			for _, m := range interior {
				if role[m] == "" {
					free = append(free, m)
				}
			}
			if len(free) == 0 {
				return
			}
			sort.Slice(free, func(i, j int) bool {
				a, b := free[i], free[j]
				if ka, kb := key(a), key(b); ka != kb {
					return ka > kb
				}
				if dist(a) != dist(b) {
					return dist(a) < dist(b)
				}
				return a[0] < b[0] || a[0] == b[0] && a[1] < b[1]
			})
			role[free[0]] = want
		}
	}
	near := func(m [2]int32) float64 { return -float64(dist(m)) }
	next := func(to ...ModuleRole) func(m [2]int32) float64 {
		return func(m [2]int32) float64 {
			k := near(m)
			weight := 1000.0
			for _, r := range to {
				if adjacent(m, r) {
					k += weight
				}
				weight = 100
			}
			return k
		}
	}
	housing := max((pawns+masterPlanPawnsPerHousing-1)/masterPlanPawnsPerHousing, 6)
	pick(ModuleStorage, 2, func(m [2]int32) float64 { return 10*stats[m].thick + near(m) })
	pick(ModuleFreezer, 1, next(ModuleStorage))
	pick(ModuleKitchen, 1, next(ModuleFreezer, ModulePlaza))
	pick(ModuleWorkshop, 3, next(ModuleStorage, ModuleWorkshop))
	pick(ModuleFields, 8, func(m [2]int32) float64 { return 100*stats[m].fertile - 10*stats[m].thick })
	pick(ModuleHospital, 1, next(ModulePlaza))
	pick(ModuleHousing, housing, next(ModulePlaza, ModuleHousing, ModuleHospital))
	pick(ModulePrison, 1, func(m [2]int32) float64 { return float64(dist(m)) })
	out := make([]PlanModule, 0, len(stats))
	for m := range stats {
		r := role[m]
		if r == "" {
			r = ModuleReserve
		}
		out = append(out, PlanModule{U: m[0], V: m[1], Role: r})
	}
	sort.Slice(out, func(i, j int) bool {
		return out[i].U < out[j].U || out[i].U == out[j].U && out[i].V < out[j].V
	})
	return out
}

// Rect is the module's exterior rectangle on the map.
func (p MasterPlan) Rect(m PlanModule) Rectangle { return p.Grid.moduleRect(m.U, m.V) }

// Plaza is the plan's first module: the starter hut and emergency shelter
// are sited inside it.
func (p MasterPlan) Plaza() Rectangle { return p.Grid.moduleRect(0, 0) }

// ModuleFor is the reserved role a room role fills.
func ModuleFor(role RoomRole) ModuleRole {
	switch role {
	case RoomRoleBedroom, RoomRoleBarracks, RoomRoleNursery, RoomRoleDeathrestChamber:
		return ModuleHousing
	case RoomRoleHospital:
		return ModuleHospital
	case RoomRolePrisonCell, RoomRolePrisonBarracks, RoomRoleContainmentCell:
		return ModulePrison
	case RoomRoleKitchen:
		return ModuleKitchen
	case RoomRoleStoreroom:
		return ModuleStorage
	case RoomRoleWorkshop, RoomRoleLaboratory:
		return ModuleWorkshop
	case RoomRoleBarn:
		return ModuleFields
	}
	return ModulePlaza
}

// Anchor is the centre of the first reserved module for want that free
// accepts (every one when free is nil), nearest the plaza first. A full
// role falls back to reserve modules; false means the plan holds no slot.
func (p MasterPlan) Anchor(want ModuleRole, free func(module Rectangle) bool) (domain.Cell, bool) {
	for _, role := range []ModuleRole{want, ModuleReserve} {
		var slots []PlanModule
		for _, m := range p.Modules {
			if m.Role == role {
				slots = append(slots, m)
			}
		}
		sort.SliceStable(slots, func(i, j int) bool {
			return slots[i].U*slots[i].U+slots[i].V*slots[i].V < slots[j].U*slots[j].U+slots[j].V*slots[j].V
		})
		for _, m := range slots {
			r := p.Rect(m)
			if free == nil || free(r) {
				return domain.Cell{X: r.X + r.Width/2, Z: r.Z + r.Height/2}, true
			}
		}
	}
	return domain.Cell{}, false
}

// ReplanNeeded reports a deliberate replan trigger: a reserved room module
// no longer sound (large mining or terrain change: a module mined out
// counts as sound, so only marsh, water or a collapsed edge trips it), or
// more pawns than the housing modules hold.
func (p MasterPlan) ReplanNeeded(survey MapSurvey, pawns int) bool {
	planes := newSurveyPlanes(survey)
	for _, m := range p.Modules {
		switch m.Role {
		case ModuleUnusable, ModuleReserve, ModuleWall, ModuleKillbox, ModulePlaza:
			continue
		}
		if planes.stats(p.Rect(m)).build < masterPlanSound {
			return true
		}
	}
	return p.Outgrown(pawns)
}

// Outgrown reports more pawns than the housing modules hold: the replan
// trigger a review checks without a survey.
func (p MasterPlan) Outgrown(pawns int) bool {
	housing := 0
	for _, m := range p.Modules {
		if m.Role == ModuleHousing {
			housing++
		}
	}
	return pawns > housing*masterPlanPawnsPerHousing
}

// Replan keeps the grid and every still-sound reserved module, widens the
// radius by one ring when the colony has outgrown its housing, and assigns
// the rest afresh. The whole map is not rescored.
func (p MasterPlan) Replan(survey MapSurvey, pawns int) MasterPlan {
	planes := newSurveyPlanes(survey)
	out := MasterPlan{Grid: p.Grid, Radius: p.Radius, Score: p.Score}
	housing := 0
	keep := map[[2]int32]ModuleRole{}
	for _, m := range p.Modules {
		if m.Role == ModuleHousing {
			housing++
		}
		if planes.stats(p.Rect(m)).build >= masterPlanSound && max(m.U, -m.U, m.V, -m.V) < p.Radius {
			keep[[2]int32{m.U, m.V}] = m.Role
		}
	}
	if pawns > housing*masterPlanPawnsPerHousing {
		out.Radius++
	}
	out.Modules = assignModules(planes, out.Grid, out.Radius, pawns, keep)
	return out
}
