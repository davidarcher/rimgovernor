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
)

type TemperatureProposal struct {
	Method TemperatureMethod
	Key    domain.MethodID
	Room   string
	Cells  []domain.Cell
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
// one ordinary campfire or passive cooler. Native temperature proves recovery.
func SelectTemperatureMethod(fact domain.Fact[RoomObservation], limits RoutinePolicy, latches RoutineLatches) (TemperatureProposal, error) {
	if err := limits.Validate(); err != nil {
		return TemperatureProposal{}, err
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
		room   Room
		bed    string
		method TemperatureMethod
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
			choices = append(choices, candidate{room, beds[0], method})
		}
	}
	sort.Slice(choices, func(i, j int) bool {
		if choices[i].method != choices[j].method {
			return choices[i].method == TemperatureHeat
		}
		return choices[i].bed < choices[j].bed
	})
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
		if exists {
			continue
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
