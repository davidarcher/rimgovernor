package policy

import (
	"crypto/sha256"
	"errors"
	"fmt"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainRefrigeration makes the rooms that hold warm, soon-to-rot perishable
// food cold, where MaintainFoodStorage moves such food into better storage.
// Both read the same FoodStorageObservation: a stock is "warm at risk" when it
// is roofed (so a stockpile exists to cool), perishable, warmer than
// ChilledMaxC and closer than SafeRotDays to rotting. The review latches on
// warm at-risk nutrition and releases once every such room is measured at or
// below ChilledExitC (or the food is gone); the method then reuses an
// existing cooler before proposing one on a vented wall of the room.

type RefrigerationReview struct {
	Active bool
	// WarmNutrition is the total nutrition of warm at-risk roofed stock;
	// unknown when the census or any at-risk stock's storage facts are
	// unknown, which preserves the previous latch.
	WarmNutrition domain.Fact[float64]
	// Rooms are the distinct native room IDs holding warm at-risk stock,
	// sorted, and only meaningful while WarmNutrition is known.
	Rooms []string
}

// warmAtRisk reports a stock's contribution to the review: known perishable,
// unrotted, roofed, with a known room, whose temperature exceeds limit and
// whose rot runway is shorter than SafeRotDays. The second result is false
// when a needed fact is unknown.
func (s FoodStorageStock) warmAtRisk(p FoodStoragePolicy, limit float64) (bool, bool) {
	perishable, pk := s.Stock.Perishable.Value()
	ticks, tk := s.Stock.RotTicks.Value()
	if !pk || !tk {
		return false, false
	}
	if !perishable || ticks <= 0 {
		return false, true
	}
	roofed, rk := s.Stock.Roofed.Value()
	if !rk {
		return false, false
	}
	if !roofed {
		return false, true
	}
	temperature, tmk := s.Stock.TemperatureC.Value()
	_, roomKnown := s.Stock.Room.Value()
	if !tmk || !roomKnown {
		return false, false
	}
	return temperature > limit && float64(ticks) < p.SafeRotDays*ticksPerDay, true
}

// ReviewRefrigeration enters on warm at-risk nutrition at or above
// AtRiskNutritionThreshold and, once active, holds until no roofed at-risk
// stock remains warmer than ChilledExitC. Unknown storage facts on any
// at-risk stock preserve the previous latch rather than asserting recovery.
func ReviewRefrigeration(fact FoodStorageObservation, active bool, p FoodStoragePolicy) (RefrigerationReview, error) {
	if !p.valid() {
		return RefrigerationReview{}, errors.New("invalid refrigeration policy")
	}
	if err := fact.Validate(); err != nil {
		return RefrigerationReview{}, err
	}
	stocks, known := fact.Stocks.Value()
	if !known {
		return RefrigerationReview{Active: active, WarmNutrition: domain.Unknown[float64]()}, nil
	}
	limit := p.ChilledMaxC
	if active {
		limit = p.ChilledExitC
	}
	total := 0.0
	rooms := map[string]bool{}
	for _, entry := range stocks {
		warm, known := entry.warmAtRisk(p, limit)
		if !known {
			return RefrigerationReview{Active: active, WarmNutrition: domain.Unknown[float64]()}, nil
		}
		if !warm {
			continue
		}
		nutrition, nk := entry.Stock.Nutrition.Value()
		if !nk {
			return RefrigerationReview{Active: active, WarmNutrition: domain.Unknown[float64]()}, nil
		}
		total += nutrition
		room, _ := entry.Stock.Room.Value()
		rooms[room] = true
	}
	r := RefrigerationReview{WarmNutrition: domain.Known(total)}
	for room := range rooms {
		r.Rooms = append(r.Rooms, room)
	}
	sort.Strings(r.Rooms)
	if active {
		r.Active = total > 0
	} else {
		r.Active = total >= p.AtRiskNutritionThreshold
	}
	if !r.Active {
		r.Rooms = nil
	}
	return r, nil
}

// RefrigerationCooler is one observed Cooler building: geometry from the
// typed building read, power state from the same-tick power census. Cold
// is the cell behind the cooler (Position - facing), Hot the cell in front.
type RefrigerationCooler struct {
	ID       string
	Position domain.Cell
	Rotation domain.Rotation
	// Token is the exact CAS snapshot token the building-temperature patch
	// needs; empty when the typed settings read did not supply one.
	Token              string
	Target             domain.Fact[float64]
	Connected, PowerOn domain.Fact[bool]
	// HotIndoors is the typed thermal-side fact when the reader supplies
	// it; otherwise the hot cell is classified from the site cells/rooms.
	HotIndoors domain.Fact[bool]
}

func (c RefrigerationCooler) Cold() domain.Cell { return step(c.Position, facing(c.Rotation), -1) }
func (c RefrigerationCooler) Hot() domain.Cell  { return step(c.Position, facing(c.Rotation), 1) }

func step(from, direction domain.Cell, times int32) domain.Cell {
	return domain.Cell{X: from.X + direction.X*times, Z: from.Z + direction.Z*times}
}

// facing is the cell a building's front points at for each rotation; a
// cooler pushes heat out its front and cools the cell behind it.
func facing(r domain.Rotation) domain.Cell {
	switch r {
	case domain.North:
		return domain.Cell{Z: 1}
	case domain.South:
		return domain.Cell{Z: -1}
	case domain.East:
		return domain.Cell{X: 1}
	default:
		return domain.Cell{X: -1}
	}
}

type RefrigerationObservation struct {
	Rooms   []Room
	Coolers []RefrigerationCooler
	// CoolerAvailable is the Cooler planning definition's availability
	// (research AirConditioning); unknown when the definition was not read.
	CoolerAvailable domain.Fact[bool]
	Cells           []SiteCell
	// Blackout is the power topology's solar-flare read: every cooler is off
	// while it is true, so no cooler method is proposed. Unknown when the
	// environment census was not read; the method then proceeds as before.
	Blackout domain.Fact[bool]
}

type RefrigerationMethod string

const (
	RefrigerationUnknown              RefrigerationMethod = "unknown"
	RefrigerationNoMethod             RefrigerationMethod = "no_deficit"
	RefrigerationEnclosureNeeded      RefrigerationMethod = "enclosed_storage_room_needed"
	RefrigerationResearchNeeded       RefrigerationMethod = "cooler_research_needed"
	RefrigerationPowerNeeded          RefrigerationMethod = "cooler_power_needed"
	RefrigerationHeatRejectionBlocked RefrigerationMethod = "cooler_heat_rejection_blocked"
	RefrigerationNoWall               RefrigerationMethod = "no_vented_wall_cell"
	RefrigerationSetTarget            RefrigerationMethod = "set_cooler_target"
	RefrigerationWait                 RefrigerationMethod = "waiting_for_native_cooling"
	RefrigerationWaitBlackout         RefrigerationMethod = "solar_flare"
	RefrigerationBuild                RefrigerationMethod = "Cooler"
)

type RefrigerationProposal struct {
	Method RefrigerationMethod
	Key    domain.MethodID
	Room   string
	// Cell and Rotation place a new cooler in the room's wall with its cold
	// side facing the room; Cooler, Token and TargetC patch an existing one.
	Cell     domain.Cell
	Rotation domain.Rotation
	Cooler   string
	Token    string
	TargetC  float64
}

func (v RefrigerationObservation) Validate() error {
	if err := (RoomObservation{Rooms: v.Rooms}).Validate(); err != nil {
		return err
	}
	if len(v.Coolers) > 256 || len(v.Cells) > 65536 {
		return errors.New("refrigeration census exceeds bound")
	}
	seen := map[string]bool{}
	for _, c := range v.Coolers {
		if !foodID(c.ID) || seen[c.ID] {
			return errors.New("invalid cooler identity")
		}
		seen[c.ID] = true
		switch c.Rotation {
		case domain.North, domain.East, domain.South, domain.West:
		default:
			return errors.New("invalid cooler rotation")
		}
		if target, known := c.Target.Value(); known && (math.IsNaN(target) || math.IsInf(target, 0)) {
			return errors.New("invalid cooler target")
		}
	}
	return nil
}

// SelectRefrigerationMethod serves the lowest-sorted candidate room first.
// A known solar flare defers every room. Deterministic order per room:
// enclosure, then an existing cooler serving
// the room (power, heat rejection, target, wait), then one new cooler on a
// wall cell whose outside is not indoors. A second cooler is proposed only
// when allowance is true: the caller's bounded native cooling allowance for
// the existing one has already elapsed without recovery.
func SelectRefrigerationMethod(review RefrigerationReview, fact domain.Fact[RefrigerationObservation], p FoodStoragePolicy, allowance bool) (RefrigerationProposal, error) {
	if !review.Active {
		return RefrigerationProposal{Method: RefrigerationNoMethod}, nil
	}
	v, known := fact.Value()
	if !known {
		return RefrigerationProposal{Method: RefrigerationUnknown}, nil
	}
	if err := v.Validate(); err != nil {
		return RefrigerationProposal{}, err
	}
	// Under a solar flare no cooler runs and none is built for an outage
	// measured in hours; the room keeps its deficit and waits for the
	// power to return (same outcome as PowerWaitBlackout).
	if blackout, known := v.Blackout.Value(); known && blackout {
		return RefrigerationProposal{Method: RefrigerationWaitBlackout}, nil
	}
	rooms := map[string]Room{}
	cellRoom := map[domain.Cell]string{}
	for _, room := range v.Rooms {
		rooms[room.ID] = room
		for _, c := range room.Cells {
			cellRoom[c] = room.ID
		}
	}
	cells := map[domain.Cell]SiteCell{}
	for _, c := range v.Cells {
		cells[c.Cell] = c
	}
	var deferred RefrigerationMethod
	for _, id := range review.Rooms {
		room, ok := rooms[id]
		if !ok {
			return RefrigerationProposal{Method: RefrigerationUnknown}, nil
		}
		enclosed, ek := room.Enclosed.Value()
		if !ek {
			return RefrigerationProposal{Method: RefrigerationUnknown}, nil
		}
		if !enclosed || len(room.Cells) == 0 {
			deferred = firstReason(deferred, RefrigerationEnclosureNeeded)
			continue
		}
		// Existing coolers whose cold side is inside this room, sorted by ID.
		var serving []RefrigerationCooler
		for _, cooler := range v.Coolers {
			if cellRoom[cooler.Cold()] == id {
				serving = append(serving, cooler)
			}
		}
		sort.Slice(serving, func(i, j int) bool { return serving[i].ID < serving[j].ID })
		if len(serving) > 0 {
			proposal, method := serveRoom(id, serving, rooms, cells, p)
			if proposal.Method != "" {
				return proposal, nil
			}
			if method != RefrigerationWait || !allowance {
				deferred = firstReason(deferred, method)
				continue
			}
		}
		available, ak := v.CoolerAvailable.Value()
		if !ak {
			return RefrigerationProposal{Method: RefrigerationUnknown}, nil
		}
		if !available {
			deferred = firstReason(deferred, RefrigerationResearchNeeded)
			continue
		}
		taken := map[domain.Cell]bool{}
		for _, cooler := range v.Coolers {
			taken[cooler.Position] = true
		}
		cell, rotation, ok := ventedWall(room, cellRoom, cells, taken)
		if !ok {
			deferred = firstReason(deferred, RefrigerationNoWall)
			continue
		}
		digest := sha256.Sum256([]byte(fmt.Sprintf("%s/cooler/%d", id, len(serving))))
		return RefrigerationProposal{Method: RefrigerationBuild, Key: domain.MethodID(fmt.Sprintf("refrigeration-%x", digest[:12])), Room: id, Cell: cell, Rotation: rotation}, nil
	}
	if deferred == "" {
		return RefrigerationProposal{Method: RefrigerationNoMethod}, nil
	}
	return RefrigerationProposal{Method: deferred}, nil
}

// firstReason keeps the first non-actionable outcome so an earlier room's
// blocker is reported over a later room's.
func firstReason(current, next RefrigerationMethod) RefrigerationMethod {
	if current != "" {
		return current
	}
	return next
}

// serveRoom checks the coolers already serving a room in ID order and
// returns either an actionable proposal (set target) or the reason the room
// is waiting on something else: power, heat rejection or native cooling.
func serveRoom(room string, coolers []RefrigerationCooler, rooms map[string]Room, cells map[domain.Cell]SiteCell, p FoodStoragePolicy) (RefrigerationProposal, RefrigerationMethod) {
	waiting := RefrigerationMethod("")
	for _, cooler := range coolers {
		connected, ck := cooler.Connected.Value()
		on, ok := cooler.PowerOn.Value()
		if !ck || !ok {
			return RefrigerationProposal{Method: RefrigerationUnknown}, ""
		}
		if !connected || !on {
			waiting = firstReason(waiting, RefrigerationPowerNeeded)
			continue
		}
		if blocked, known := heatRejectionBlocked(cooler, rooms, cells); !known {
			return RefrigerationProposal{Method: RefrigerationUnknown}, ""
		} else if blocked {
			waiting = firstReason(waiting, RefrigerationHeatRejectionBlocked)
			continue
		}
		target, tk := cooler.Target.Value()
		if !tk {
			return RefrigerationProposal{Method: RefrigerationUnknown}, ""
		}
		if target > p.FreezerTargetC {
			if cooler.Token == "" {
				return RefrigerationProposal{Method: RefrigerationUnknown}, ""
			}
			digest := sha256.Sum256([]byte(cooler.ID + "/target"))
			return RefrigerationProposal{Method: RefrigerationSetTarget, Key: domain.MethodID(fmt.Sprintf("refrigeration-%x", digest[:12])), Room: room, Cooler: cooler.ID, Token: cooler.Token, TargetC: p.FreezerTargetC}, ""
		}
		waiting = firstReason(waiting, RefrigerationWait)
	}
	// A working cooler outranks a blocked one: the room is cooling.
	for _, cooler := range coolers {
		if connected, _ := cooler.Connected.Value(); !connected {
			continue
		}
		if on, _ := cooler.PowerOn.Value(); !on {
			continue
		}
		if blocked, known := heatRejectionBlocked(cooler, rooms, cells); known && !blocked {
			return RefrigerationProposal{}, RefrigerationWait
		}
	}
	return RefrigerationProposal{}, waiting
}

// heatRejectionBlocked is true when the cooler's hot side is impassable, or
// sits indoors in a room measured no warmer than the room it cools would be
// useful at: heat pushed into another enclosed room stops the exchange once
// that room saturates. Outdoors or unroofed hot sides are never blocked.
func heatRejectionBlocked(cooler RefrigerationCooler, rooms map[string]Room, cells map[domain.Cell]SiteCell) (bool, bool) {
	hot := cooler.Hot()
	if c, ok := cells[hot]; ok {
		if walkable, known := c.Walkable.Value(); known && !walkable {
			return true, true
		}
	}
	if indoors, known := cooler.HotIndoors.Value(); known {
		return indoors, true
	}
	if c, ok := cells[hot]; ok {
		if indoors, known := c.Indoors.Value(); known {
			return indoors, true
		}
		if roofed, known := c.Roofed.Value(); known && !roofed {
			return false, true
		}
	}
	for _, room := range rooms {
		for _, c := range room.Cells {
			if c == hot {
				enclosed, known := room.Enclosed.Value()
				return enclosed, known
			}
		}
	}
	return false, false
}

// ventedWall picks the lowest-sorted wall cell of the room whose outward
// neighbour is a known non-indoor cell, and the rotation that points the
// cooler's front outward so its cold side faces the room. Wall cells are
// non-walkable site cells adjacent to a room cell along a straight line
// with the outside: inside -> wall -> outside.
func ventedWall(room Room, cellRoom map[domain.Cell]string, cells map[domain.Cell]SiteCell, taken map[domain.Cell]bool) (domain.Cell, domain.Rotation, bool) {
	type option struct {
		cell     domain.Cell
		rotation domain.Rotation
	}
	var options []option
	for _, inside := range room.Cells {
		for _, rotation := range []domain.Rotation{domain.North, domain.East, domain.South, domain.West} {
			wall := step(inside, facing(rotation), 1)
			outside := step(inside, facing(rotation), 2)
			if taken[wall] || cellRoom[wall] == room.ID || cellRoom[outside] == room.ID {
				continue
			}
			w, ok := cells[wall]
			if !ok {
				continue
			}
			if walkable, known := w.Walkable.Value(); !known || walkable {
				continue
			}
			if occupied, known := w.Occupied.Value(); known && !occupied {
				continue
			}
			o, ok := cells[outside]
			if !ok {
				continue
			}
			indoors, ik := o.Indoors.Value()
			roofed, rk := o.Roofed.Value()
			if !(ik && !indoors || rk && !roofed) {
				continue
			}
			if walkable, known := o.Walkable.Value(); !known || !walkable {
				continue
			}
			options = append(options, option{wall, rotation})
		}
	}
	if len(options) == 0 {
		return domain.Cell{}, "", false
	}
	sort.Slice(options, func(i, j int) bool {
		a, b := options[i], options[j]
		if a.cell != b.cell {
			return a.cell.X < b.cell.X || a.cell.X == b.cell.X && a.cell.Z < b.cell.Z
		}
		return a.rotation < b.rotation
	})
	return options[0].cell, options[0].rotation, true
}
