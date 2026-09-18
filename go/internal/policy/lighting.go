package policy

import (
	"crypto/sha256"
	"errors"
	"fmt"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainLighting keeps the cells colonists stand on while working lit
// (issue #6 slice 3). It reasons from the measured native glow at each
// bench's interaction cell rather than from fixture counts: a roofed work
// cell whose ground glow is below LitGlow is dark whatever lamps sit nearby.
// The method first looks for an existing lamp that should cover the cell and
// defers to power/refuel/repair work when that lamp is merely unserviced;
// otherwise it places an affordable lamp beside the cell and lets the next
// measured census release the latch.
const MaintainLighting GoalID = "MaintainLighting"

type LightingPolicy struct {
	// LitGlow is the ground glow at or above which a cell counts as lit;
	// RimWorld's own darkness penalties use 0.3.
	LitGlow float64
	// PlacementRadius bounds the Chebyshev distance from the dark cell at
	// which a new lamp may stand.
	PlacementRadius int32
	// Lamps ranks the fixture definitions to consider; powered ones are
	// only chosen while the colony has an active power source.
	Lamps []LampDefinition
}

type LampDefinition struct {
	Name    string
	Powered bool
}

func DefaultLightingPolicy() LightingPolicy {
	return LightingPolicy{LitGlow: 0.3, PlacementRadius: 2, Lamps: []LampDefinition{{Name: "StandingLamp", Powered: true}, {Name: "TorchLamp"}}}
}

func (p LightingPolicy) valid() bool {
	if !(p.LitGlow > 0 && p.LitGlow <= 1) || p.PlacementRadius < 1 || p.PlacementRadius > 8 || len(p.Lamps) == 0 {
		return false
	}
	for _, l := range p.Lamps {
		if !foodID(l.Name) {
			return false
		}
	}
	return true
}

// LightingObservation is the native lighting census: every colonist bench's
// interaction cell with its measured glow, and every glowing fixture.
type LightingObservation struct {
	WorkCells []WorkLightCell
	Lamps     []Lamp
}
type WorkLightCell struct {
	Bench, Definition string
	Cell              domain.Cell
	Glow              float64
	Roofed            bool
	Room              domain.Fact[string]
	// LightSensitive is native's word that the cell's room grows a plant
	// that dies to light (cave fungus); such a room is protected and the
	// cell is never a lighting deficit however dark it measures.
	LightSensitive bool
}
type Lamp struct {
	ID, Definition string
	Cell           domain.Cell
	Radius         float64
	// Lit is native's own "currently emits light" flag, false while the
	// fixture is unpowered, out of fuel, switched off or broken down.
	Lit                                                   bool
	Room                                                  domain.Fact[string]
	Powered, Connected, SwitchedOn, OutOfFuel, BrokenDown domain.Fact[bool]
	FuelDefinitions                                       []string
}

func (v LightingObservation) Validate() error {
	if len(v.WorkCells) > 256 || len(v.Lamps) > 256 {
		return errors.New("lighting census exceeds bound")
	}
	seen := map[string]bool{}
	for _, c := range v.WorkCells {
		if !foodID(c.Bench) || seen[c.Bench] || math.IsNaN(c.Glow) || c.Glow < 0 || c.Glow > 1 {
			return errors.New("invalid work light cell")
		}
		seen[c.Bench] = true
	}
	lamps := map[string]bool{}
	for _, l := range v.Lamps {
		if !foodID(l.ID) || lamps[l.ID] || math.IsNaN(l.Radius) || l.Radius < 0 {
			return errors.New("invalid lamp")
		}
		lamps[l.ID] = true
	}
	return nil
}

// LightingReview is the per-bench latch: Dark lists the roofed work cells
// measured below LitGlow, sorted by bench ID. Known is false when the
// census was unknown, in which case Dark repeats the previous latch.
type LightingReview struct {
	Active bool
	Dark   []string
	Known  bool
}

// ReviewLighting measures every roofed work cell against LitGlow. There is
// no hysteresis: a lamp within reach lifts the cell well above the
// threshold, and unroofed cells are skipped because sky glow would flap the
// latch with the day. A light-sensitive cell (its room grows cave fungus) is
// never dark: lighting it would kill the crop. An unknown census keeps the
// previous dark set.
func ReviewLighting(fact domain.Fact[LightingObservation], previous []string, p LightingPolicy) (LightingReview, error) {
	if !p.valid() {
		return LightingReview{}, errors.New("invalid lighting policy")
	}
	v, known := fact.Value()
	if !known {
		dark := append([]string(nil), previous...)
		sort.Strings(dark)
		return LightingReview{Active: len(dark) > 0, Dark: dark}, nil
	}
	if err := v.Validate(); err != nil {
		return LightingReview{}, err
	}
	r := LightingReview{Known: true}
	for _, c := range v.WorkCells {
		if c.Roofed && !c.LightSensitive && c.Glow < p.LitGlow {
			r.Dark = append(r.Dark, c.Bench)
		}
	}
	sort.Strings(r.Dark)
	r.Active = len(r.Dark) > 0
	return r, nil
}

type LightingMethod string

const (
	LightingUnknown        LightingMethod = "unknown"
	LightingNoMethod       LightingMethod = "no_deficit"
	LightingPowerNeeded    LightingMethod = "lamp_power_needed"
	LightingFuelNeeded     LightingMethod = "lamp_fuel_needed"
	LightingRepairNeeded   LightingMethod = "lamp_repair_needed"
	LightingSwitchedOff    LightingMethod = "lamp_switched_off"
	LightingBlocked        LightingMethod = "lamp_lit_but_cell_dark"
	LightingResearchNeeded LightingMethod = "lamp_research_needed"
	LightingNoSpace        LightingMethod = "no_lamp_cell"
	LightingBuild          LightingMethod = "build_lamp"
)

type LightingProposal struct {
	Method LightingMethod
	Key    domain.MethodID
	// Bench and Target name the dark work cell served; Definition and
	// Cells are the chosen lamp and its candidate cells nearest first, each
	// to be validated by a native placement preview before admission.
	Bench      string
	Target     domain.Cell
	Definition string
	Cells      []domain.Cell
}

// LightingFacts is what SelectLightingMethod needs beyond the census.
type LightingFacts struct {
	Rooms []Room
	Cells []SiteCell
	// Available maps lamp definition names to their planning availability
	// as read with the census; a lamp absent here is unknown.
	Available map[string]domain.Fact[bool]
	// PoweredSource reports whether any power network currently has an
	// active source; unknown defers powered lamps.
	PoweredSource domain.Fact[bool]
}

// SelectLightingMethod resolves the lowest-sorted dark bench. An existing
// lamp whose radius reaches the cell but which is not lit is somebody
// else's job (power, refueling, repair, flicking) and yields a deferring
// outcome. A lit lamp already standing within PlacementRadius that still
// leaves the cell dark is reported as blocked rather than doubled; a lit
// lamp further away whose radius only grazes the cell is partial coverage
// (glow falls off with distance and stops at walls), and the cell gets its
// own lamp. Otherwise the first affordable definition is placed on the
// nearest free walkable cell of the same room within PlacementRadius.
func SelectLightingMethod(review LightingReview, fact domain.Fact[LightingObservation], facts LightingFacts, p LightingPolicy) (LightingProposal, error) {
	if !p.valid() {
		return LightingProposal{}, errors.New("invalid lighting policy")
	}
	if !review.Active {
		return LightingProposal{Method: LightingNoMethod}, nil
	}
	v, known := fact.Value()
	if !known {
		return LightingProposal{Method: LightingUnknown}, nil
	}
	if err := v.Validate(); err != nil {
		return LightingProposal{}, err
	}
	cells := map[domain.Cell]SiteCell{}
	for _, c := range facts.Cells {
		cells[c.Cell] = c
	}
	cellRoom := map[domain.Cell]string{}
	for _, room := range facts.Rooms {
		for _, c := range room.Cells {
			cellRoom[c] = room.ID
		}
	}
	work := map[string]WorkLightCell{}
	occupied := map[domain.Cell]bool{}
	for _, c := range v.WorkCells {
		work[c.Bench] = c
		occupied[c.Cell] = true
	}
	for _, l := range v.Lamps {
		occupied[l.Cell] = true
	}
	var deferred LightingMethod
	for _, bench := range review.Dark {
		target, ok := work[bench]
		if !ok || !target.Roofed || target.Glow >= p.LitGlow {
			// The census no longer lists the bench as dark: the latch is
			// stale, and the next review releases it.
			continue
		}
		var serving []Lamp
		for _, l := range v.Lamps {
			if distance(l.Cell, target.Cell) <= l.Radius {
				serving = append(serving, l)
			}
		}
		sort.Slice(serving, func(i, j int) bool { return serving[i].ID < serving[j].ID })
		if method := servingOutcome(serving, target.Cell, p.PlacementRadius); method != "" {
			deferred = firstLightingReason(deferred, method)
			continue
		}
		definition, method := selectLampDefinition(facts, p)
		if method != "" {
			deferred = firstLightingReason(deferred, method)
			continue
		}
		candidates := lampCells(target.Cell, cellRoom, cells, occupied, p.PlacementRadius)
		if len(candidates) == 0 {
			deferred = firstLightingReason(deferred, LightingNoSpace)
			continue
		}
		digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%s/%d,%d", bench, definition, target.Cell.X, target.Cell.Z)))
		return LightingProposal{Method: LightingBuild, Key: domain.MethodID(fmt.Sprintf("lighting-%x", digest[:12])), Bench: bench, Target: target.Cell, Definition: definition, Cells: candidates}, nil
	}
	if deferred == "" {
		return LightingProposal{Method: LightingNoMethod}, nil
	}
	return LightingProposal{Method: deferred}, nil
}

// servingOutcome classifies the lamps whose radius reaches a dark cell:
// the lowest-ID unlit one names the service it waits for; with every lamp
// lit, one already within the placement radius means the cell is blocked;
// lit lamps only reaching from further away leave the cell partially lit
// and yield "" so a lamp of its own is placed.
func servingOutcome(serving []Lamp, target domain.Cell, radius int32) LightingMethod {
	near := false
	for _, l := range serving {
		if !l.Lit {
			return lampService(l)
		}
		near = near || chebyshev(l.Cell, target) <= radius
	}
	if near {
		return LightingBlocked
	}
	return ""
}

func firstLightingReason(current, next LightingMethod) LightingMethod {
	if current == "" || current == LightingUnknown {
		return next
	}
	return current
}

// lampService classifies why an in-range lamp is dark, most specific first.
// Unknown service facts fall through to the generic power outcome.
func lampService(l Lamp) LightingMethod {
	if broken, known := l.BrokenDown.Value(); known && broken {
		return LightingRepairNeeded
	}
	if out, known := l.OutOfFuel.Value(); known && out {
		return LightingFuelNeeded
	}
	if on, known := l.SwitchedOn.Value(); known && !on {
		return LightingSwitchedOff
	}
	if _, known := l.OutOfFuel.Value(); known {
		// A fueled lamp with fuel that still does not glow is refuel
		// scheduling, not construction.
		return LightingFuelNeeded
	}
	return LightingPowerNeeded
}

// selectLampDefinition returns the first policy lamp that is available and,
// when powered, backed by an active power source. Unknown availability or
// source facts defer rather than guess.
func selectLampDefinition(facts LightingFacts, p LightingPolicy) (string, LightingMethod) {
	deferred := LightingMethod("")
	for _, lamp := range p.Lamps {
		ok, known := facts.Available[lamp.Name].Value()
		if !known {
			deferred = firstLightingReason(deferred, LightingUnknown)
			continue
		}
		if !ok {
			deferred = firstLightingReason(deferred, LightingResearchNeeded)
			continue
		}
		if lamp.Powered {
			source, sk := facts.PoweredSource.Value()
			if !sk {
				deferred = firstLightingReason(deferred, LightingUnknown)
				continue
			}
			if !source {
				continue
			}
		}
		return lamp.Name, ""
	}
	if deferred == "" {
		deferred = LightingResearchNeeded
	}
	return "", deferred
}

// lampCells lists the free, walkable, unoccupied, unzoned site cells of the
// target's room within radius of the target (never the target itself),
// nearest first and then in cell order, so the planner previews a stable
// sequence.
func lampCells(target domain.Cell, cellRoom map[domain.Cell]string, cells map[domain.Cell]SiteCell, occupied map[domain.Cell]bool, radius int32) []domain.Cell {
	room, inRoom := cellRoom[target]
	var out []domain.Cell
	for dz := -radius; dz <= radius; dz++ {
		for dx := -radius; dx <= radius; dx++ {
			c := domain.Cell{X: target.X + dx, Z: target.Z + dz}
			if c == target || occupied[c] {
				continue
			}
			if inRoom && cellRoom[c] != room {
				continue
			}
			site, known := cells[c]
			if !known {
				continue
			}
			walkable, wk := site.Walkable.Value()
			busy, ok := site.Occupied.Value()
			if !wk || !walkable || !ok || busy {
				continue
			}
			if zone, zk := site.Zone.Value(); zk && zone {
				continue
			}
			out = append(out, c)
		}
	}
	sort.Slice(out, func(i, j int) bool {
		di, dj := chebyshev(out[i], target), chebyshev(out[j], target)
		if di != dj {
			return di < dj
		}
		if out[i].Z != out[j].Z {
			return out[i].Z < out[j].Z
		}
		return out[i].X < out[j].X
	})
	return out
}

func distance(a, b domain.Cell) float64 {
	dx, dz := float64(a.X-b.X), float64(a.Z-b.Z)
	return math.Sqrt(dx*dx + dz*dz)
}

func chebyshev(a, b domain.Cell) int32 {
	return max(abs32(a.X-b.X), abs32(a.Z-b.Z))
}

func abs32(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}
