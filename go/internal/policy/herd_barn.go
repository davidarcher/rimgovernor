package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// BarnAreaKey is the bot area key of the Barn allowed area: the interior of
// the plan's standing barns (#1869). AnimalShelterChoice moves pen animals
// into it while their race is in danger outdoors (AnimalExposures).
const BarnAreaKey = "Barn"

// BarnAreaLabel is the native label of the Barn area.
const BarnAreaLabel = BarnAreaKey

// BarnCells are the interior cells of the plan's barns that stand shelled
// (CensusRoomIn), sorted: an animal is never sent to a barn that is
// not yet a room.
func (p LayoutPlan) BarnCells(rooms RoomObservation) []domain.Cell {
	set := map[domain.Cell]bool{}
	for _, room := range p.HerdRooms(PlannedBarn) {
		if _, standing := CensusRoomIn(room, rooms); !standing {
			continue
		}
		for _, c := range rectCells(room.Interior) {
			set[c] = true
		}
	}
	return sortedCells(set)
}

// AnimalShelterChoice is the husbandry write that shelters the pen animals
// from exposure, one per cycle, derived from the animals' current allowed
// area alone (no stored prior state): while the animal's race is in danger
// outdoors (a hostile threat, RoundsFacts.Hostiles > 0 -- the colony's
// non-distant Hostile and HuntingPredator holds -- endangers every race and
// skips the weather reads; otherwise AnimalExposures over the pen animals'
// races) an animal with no area restriction is let into the Barn area (allowed_area), and once its
// race is out of danger an animal in the Barn area is released
// (allowed_area cleared), which is how a pen animal is unrestricted.
// An animal in any other area (the vet room's) is left to the flow that put
// it there, as is one marked for removal or with an unread area, support or
// removal fact. No standing Barn area or no pen animal chooses nothing; an
// unread hostile count, or with no threat a race with no comfort range or an
// unread census or temperature, is an error (ErrAnimalExposure).
func (f RoundsFacts) AnimalShelterChoice() (HusbandryChoice, error) {
	none := HusbandryChoice{Reason: HusbandryNoDeficit}
	rows, known := f.AnimalUpkeep.Animals.Value()
	barn, barnKnown := f.BarnArea.Value()
	if !known || !barnKnown || barn == "" {
		return none, nil
	}
	var pen []UpkeepAnimal
	var races []Resource
	for _, a := range rows {
		requires, pk := a.RequiresPen.Value()
		release, rk := a.Release.Value()
		slaughter, sk := a.Slaughter.Value()
		supports, uk := a.SupportsAreas.Value()
		if _, ak := a.AllowedArea.Value(); pk && requires && rk && !release && sk && !slaughter && uk && supports && ak {
			pen = append(pen, a)
			races = append(races, a.Definition)
		}
	}
	if len(pen) == 0 {
		return none, nil
	}
	hostiles, hostilesKnown := f.Hostiles.Value()
	if !hostilesKnown {
		return none, fmt.Errorf("%w: the hostile count is unread", ErrAnimalExposure)
	}
	danger := map[Resource]bool{}
	if hostiles > 0 {
		for _, r := range races {
			danger[r] = true
		}
	} else {
		exposures, err := AnimalExposures(races, f.DisasterConditions, f.OutdoorTemperature, f.AnimalUpkeep.AnimalRaces.comfort)
		if err != nil {
			return none, err
		}
		for _, e := range exposures {
			danger[e.Race] = e.Danger()
		}
	}
	sort.Slice(pen, func(i, j int) bool { return pen[i].ID < pen[j].ID })
	for _, a := range pen {
		area, _ := a.AllowedArea.Value()
		switch {
		case danger[a.Definition] && area == "":
			return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryAllowedArea, Argument: barn}, nil
		case !danger[a.Definition] && area == barn:
			return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryAllowedArea}, nil
		}
	}
	return none, nil
}

// comfort is a race's comfortable range from the catalog row, an error for a
// race the catalog lacks or whose range the game does not show.
func (c AnimalRaceCatalog) comfort(def Resource) (AnimalComfort, error) {
	race, ok := c.Race(def)
	if !ok {
		return AnimalComfort{}, fmt.Errorf("not in the race catalog")
	}
	comfort, known := race.Comfort.Value()
	if !known {
		return AnimalComfort{}, fmt.Errorf("the game shows no comfort range")
	}
	return comfort, nil
}
