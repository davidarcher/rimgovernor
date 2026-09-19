package policy

import (
	"crypto/sha256"
	"errors"
	"fmt"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type TemperatureMethod string

const (
	TemperatureUnknown       TemperatureMethod = "unknown"
	TemperatureNoMethod      TemperatureMethod = "no_deficit"
	TemperatureShelterNeeded TemperatureMethod = "enclosed_sleeping_room_needed"
	TemperatureWait          TemperatureMethod = "waiting_for_native_temperature"
	TemperatureHeat          TemperatureMethod = "Campfire"
	TemperatureCool          TemperatureMethod = "PassiveCooler"
	// TemperatureCoolPowered places one powered Cooler through a vented
	// wall of the hottest sleeping room, its cold side facing the room;
	// it outranks the passive cooler when the research is done and a
	// power network has the spare capacity to run it (#406).
	TemperatureCoolPowered TemperatureMethod = "Cooler"
)

type TemperatureProposal struct {
	Method TemperatureMethod
	Key    domain.MethodID
	Room   string
	Cells  []domain.Cell
	// Cell and Rotation are the exact wall cell and facing of a powered
	// cooler (TemperatureCoolPowered); the passive methods search Cells.
	Cell     domain.Cell
	Rotation domain.Rotation
}

// TemperatureCooling is the evidence the powered cooler method reads beside
// the room census: the Cooler planning definition's availability and draw,
// the colony power topology and the site cells the wall search walks. The
// zero value (everything unknown) confines the method to passive coolers.
type TemperatureCooling struct {
	CoolerAvailable domain.Fact[bool]
	CoolerDrawW     domain.Fact[float64]
	Power           domain.Fact[PowerTopology]
	Cells           []SiteCell
}

// SpareW is the largest surplus of nominal producer capacity over consumer
// demand across the topology's connected power networks, the same
// capacity-versus-demand reading the power family sizes its budget by.
// Unknown when any connected building's draw or network is unknown, or when
// no network has a producer.
func (t PowerTopology) SpareW() domain.Fact[float64] {
	type net struct{ capacity, demand float64 }
	nets := map[string]*net{}
	for _, b := range t.Buildings {
		connected, ck := b.Connected.Value()
		if !ck {
			return domain.Unknown[float64]()
		}
		if !connected {
			continue
		}
		w, wk := b.BaseW.Value()
		id, nk := b.Network.Value()
		if !wk || !nk {
			return domain.Unknown[float64]()
		}
		n := nets[id]
		if n == nil {
			n = &net{}
			nets[id] = n
		}
		if w > 0 {
			n.capacity += w
		} else {
			n.demand -= w
		}
	}
	spare, have := 0.0, false
	for _, n := range nets {
		if n.capacity <= 0 {
			continue
		}
		if !have || n.capacity-n.demand > spare {
			spare = n.capacity - n.demand
		}
		have = true
	}
	if !have {
		return domain.Unknown[float64]()
	}
	return domain.Known(spare)
}

// wallCoolerBeside reports a Cooler in the power census standing on a cell
// four-adjacent to one of the room's cells: a cooler placed through the
// room's wall.
func (c TemperatureCooling) wallCoolerBeside(room Room) bool {
	topology, known := c.Power.Value()
	if !known {
		return false
	}
	inside := map[domain.Cell]bool{}
	for _, cell := range room.Cells {
		inside[cell] = true
	}
	for _, b := range topology.Buildings {
		if b.Definition != "Cooler" {
			continue
		}
		for _, rotation := range []domain.Rotation{domain.North, domain.East, domain.South, domain.West} {
			if inside[step(b.Cell, facing(rotation), 1)] {
				return true
			}
		}
	}
	return false
}

// poweredCoolerReady reports whether a powered cooler can be proposed at
// all: research finished, draw known and a network with that much spare.
func (c TemperatureCooling) poweredCoolerReady() bool {
	available, ak := c.CoolerAvailable.Value()
	draw, dk := c.CoolerDrawW.Value()
	topology, tk := c.Power.Value()
	if !ak || !available || !dk || !tk {
		return false
	}
	if blackout, known := topology.Blackout.Value(); known && blackout {
		return false
	}
	spare, sk := topology.SpareW().Value()
	return sk && spare >= draw
}

func (v RoomObservation) Validate() error {
	if len(v.Rooms) > 256 {
		return errors.New("temperature room census exceeds bound")
	}
	if beds, known := v.EligibleBeds.Value(); known {
		if len(beds) > 256 {
			return errors.New("sleeping bed census exceeds bound")
		}
		seen := map[string]bool{}
		for _, id := range beds {
			if !foodID(id) || seen[id] {
				return errors.New("invalid sleeping bed census")
			}
			seen[id] = true
		}
	}
	rooms, beds, cells := map[string]bool{}, map[string]bool{}, map[domain.Cell]bool{}
	for _, room := range v.Rooms {
		if !foodID(room.ID) || rooms[room.ID] || len(room.Beds) > 256 || len(room.Cells) > 4096 {
			return errors.New("invalid temperature room")
		}
		rooms[room.ID] = true
		if temperature, known := room.Temperature.Value(); known && (math.IsNaN(temperature) || math.IsInf(temperature, 0)) {
			return errors.New("invalid room temperature")
		}
		for _, id := range room.Beds {
			if !foodID(id) || beds[id] {
				return errors.New("ambiguous sleeping room")
			}
			beds[id] = true
		}
		for _, c := range room.Cells {
			if c.X < 0 || c.Z < 0 || c.X >= 4096 || c.Z >= 4096 || cells[c] {
				return errors.New("invalid or overlapping room footprint")
			}
			cells[c] = true
		}
		if len(cells) > 65536 {
			return errors.New("temperature geometry exceeds bound")
		}
		if contents, known := room.Contents.Value(); known {
			if len(contents) > 256 {
				return errors.New("room contents exceed bound")
			}
			seen := map[Resource]bool{}
			for _, q := range contents {
				if !validResource(q.Resource) || q.Count < 0 || seen[q.Resource] {
					return errors.New("invalid room contents")
				}
				seen[q.Resource] = true
			}
		}
	}
	return nil
}

// TemperatureRange uses actual sleeping rooms, including beds whose safe
// reachability disappears during a temperature emergency. Missing room evidence
// cannot become a comfortable temperature or a reason to claim recovery.
func TemperatureRange(fact domain.Fact[RoomObservation]) (minimum, maximum domain.Fact[float64]) {
	v, known := fact.Value()
	if !known || v.Validate() != nil {
		return
	}
	eligible, known := v.EligibleBeds.Value()
	if !known || len(eligible) == 0 {
		return
	}
	wanted := map[string]bool{}
	for _, id := range eligible {
		wanted[id] = true
	}
	seen := map[string]bool{}
	low, high, have := 0.0, 0.0, false
	for _, room := range v.Rooms {
		selected := false
		for _, id := range room.Beds {
			if wanted[id] {
				selected = true
				seen[id] = true
			}
		}
		if !selected {
			continue
		}
		temperature, tk := room.Temperature.Value()
		enclosed, ek := room.Enclosed.Value()
		if !tk || !ek || !enclosed {
			return
		}
		if !have || temperature < low {
			low = temperature
		}
		if !have || temperature > high {
			high = temperature
		}
		have = true
	}
	if len(seen) == len(wanted) && have {
		return domain.Known(low), domain.Known(high)
	}
	return
}

// SelectTemperatureMethod reuses existing thermal facilities before proposing
// one ordinary campfire, or one cooler for the hottest sleeping room: a
// powered Cooler through a vented wall when cooling reports the research
// done and a network with spare capacity for its draw, otherwise a passive
// cooler inside the room. Native temperature proves recovery.
func SelectTemperatureMethod(fact domain.Fact[RoomObservation], cooling TemperatureCooling, limits RoutinePolicy, latches RoutineLatches) (TemperatureProposal, error) {
	if err := limits.Validate(); err != nil {
		return TemperatureProposal{}, err
	}
	if len(cooling.Cells) > 65536 {
		return TemperatureProposal{}, errors.New("temperature site census exceeds bound")
	}
	v, known := fact.Value()
	if !known {
		return TemperatureProposal{Method: TemperatureUnknown}, nil
	}
	if err := v.Validate(); err != nil {
		return TemperatureProposal{}, err
	}
	eligible, known := v.EligibleBeds.Value()
	if !known {
		return TemperatureProposal{Method: TemperatureUnknown}, nil
	}
	if len(eligible) == 0 {
		return TemperatureProposal{Method: TemperatureShelterNeeded}, nil
	}
	wanted := map[string]bool{}
	for _, id := range eligible {
		wanted[id] = true
	}
	type candidate struct {
		room        Room
		bed         string
		method      TemperatureMethod
		temperature float64
	}
	var choices []candidate
	unknown, missing := false, false
	seen := map[string]bool{}
	for _, room := range v.Rooms {
		var beds []string
		for _, id := range room.Beds {
			if wanted[id] {
				beds = append(beds, id)
				seen[id] = true
			}
		}
		if len(beds) == 0 {
			continue
		}
		sort.Strings(beds)
		temperature, tk := room.Temperature.Value()
		enclosed, ek := room.Enclosed.Value()
		if !tk || !ek {
			unknown = true
			continue
		}
		if !enclosed || len(room.Cells) == 0 {
			missing = true
			continue
		}
		method := TemperatureNoMethod
		if temperature < limits.ColdEnter || latches.Cold && temperature < limits.ColdExit {
			method = TemperatureHeat
		} else if temperature > limits.HotEnter || latches.Hot && temperature > limits.HotExit {
			method = TemperatureCool
		}
		if method != TemperatureNoMethod {
			choices = append(choices, candidate{room, beds[0], method, temperature})
		}
	}
	// Heat before cool; the coldest room first among the cold, the hottest
	// first among the hot; the lowest bed breaks ties.
	sort.Slice(choices, func(i, j int) bool {
		a, b := choices[i], choices[j]
		if a.method != b.method {
			return a.method == TemperatureHeat
		}
		if a.temperature != b.temperature {
			if a.method == TemperatureHeat {
				return a.temperature < b.temperature
			}
			return a.temperature > b.temperature
		}
		return a.bed < b.bed
	})
	powered := cooling.poweredCoolerReady()
	var cellRoom map[domain.Cell]string
	var cells map[domain.Cell]SiteCell
	if powered {
		cellRoom, cells = map[domain.Cell]string{}, map[domain.Cell]SiteCell{}
		for _, room := range v.Rooms {
			for _, c := range room.Cells {
				cellRoom[c] = room.ID
			}
		}
		for _, c := range cooling.Cells {
			cells[c.Cell] = c
		}
	}
	for _, choice := range choices {
		contents, known := choice.room.Contents.Value()
		if !known {
			unknown = true
			continue
		}
		exists := false
		for _, q := range contents {
			if q.Count == 0 {
				continue
			}
			if choice.method == TemperatureHeat {
				exists = exists || q.Resource == "Campfire" || q.Resource == "Heater"
			} else {
				exists = exists || q.Resource == "PassiveCooler" || q.Resource == "Cooler"
			}
		}
		// A wall cooler stands in the room's boundary, outside its cell
		// set and its contents: the power census's Cooler rows beside
		// the room are the facility the room already has.
		if choice.method == TemperatureCool && !exists {
			exists = cooling.wallCoolerBeside(choice.room)
		}
		if exists {
			continue
		}
		if choice.method == TemperatureCool && powered {
			// A vented wall cell hosts the powered cooler; a room with no
			// such wall (rock-bound, or ringed by other rooms) keeps the
			// passive cooler.
			if cell, rotation, ok := ventedWall(choice.room, cellRoom, cells, map[domain.Cell]bool{}); ok {
				digest := sha256.Sum256([]byte(choice.bed + "/" + string(TemperatureCoolPowered)))
				return TemperatureProposal{Method: TemperatureCoolPowered, Key: domain.MethodID(fmt.Sprintf("thermal-%x", digest[:12])), Room: choice.room.ID, Cells: append([]domain.Cell{}, choice.room.Cells...), Cell: cell, Rotation: rotation}, nil
			}
		}
		digest := sha256.Sum256([]byte(choice.bed + "/" + string(choice.method)))
		return TemperatureProposal{Method: choice.method, Key: domain.MethodID(fmt.Sprintf("thermal-%x", digest[:12])), Room: choice.room.ID, Cells: append([]domain.Cell{}, choice.room.Cells...)}, nil
	}
	if len(seen) != len(wanted) || unknown {
		return TemperatureProposal{Method: TemperatureUnknown}, nil
	}
	if missing {
		return TemperatureProposal{Method: TemperatureShelterNeeded}, nil
	}
	if len(choices) > 0 {
		return TemperatureProposal{Method: TemperatureWait}, nil
	}
	return TemperatureProposal{Method: TemperatureNoMethod}, nil
}
