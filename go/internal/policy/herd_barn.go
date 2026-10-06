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
		// Census: an animal is sent only into a roofed barn.
		if _, standing := CensusRoomIn(room, rooms); !standing {
			continue
		}
		for _, c := range rectCells(room.Interior) {
			set[c] = true
		}
	}
	return sortedCells(set)
}

// AnimalShelterChoice is the husbandry write that places animals in their
// kind's allowed area, one per cycle, derived from the animals' current
// allowed area alone (no stored prior state). An animal is moved only from no
// restriction or the Barn area; one in any other area (the vet room's) is left
// to the flow that put it there, as is one marked for removal or with an unread
// area, support or removal fact. With the ring's closure unread nothing is
// chosen (#2234, epic #2229):
//   - a predator is kept in the Wild area (the map minus the paddock), a tamed
//     warg (a roamer) included, except a bonded non-roamer predator (a cat),
//     which is a companion;
//   - a roamer (RequiresPen) is kept in the Barn while the ring is open; once
//     the ring is closed the paddock holds it unrestricted, and it is let into
//     the Barn only while its race is in danger outdoors (a hostile threat,
//     RoundsFacts.Hostiles > 0, endangers every race and skips the weather
//     reads; otherwise AnimalExposures) and released once that passes (#1869);
//   - a bonded non-roamer is kept in the Companion area, the paddock yard.
//
// A kind whose area is not standing chooses nothing; an unread hostile count,
// or with no threat a race with no comfort range or an unread census or
// temperature, is an error (ErrAnimalExposure) once a roamer needs it.
func (f RoundsFacts) AnimalShelterChoice() (HusbandryChoice, error) {
	none := HusbandryChoice{Reason: HusbandryNoDeficit}
	rows, known := f.AnimalUpkeep.Animals.Value()
	closed, closedKnown := f.PaddockClosed.Value()
	if !known || !closedKnown {
		return none, nil
	}
	barn, _ := f.BarnArea.Value()
	companion, _ := f.CompanionArea.Value()
	wild, _ := f.WildArea.Value()
	var placed []UpkeepAnimal
	var races []Resource
	for _, a := range rows {
		release, rk := a.Release.Value()
		slaughter, sk := a.Slaughter.Value()
		supports, uk := a.SupportsAreas.Value()
		if _, ak := a.AllowedArea.Value(); !(rk && !release && sk && !slaughter && uk && supports && ak) {
			continue
		}
		placed = append(placed, a)
		if requires, pk := a.RequiresPen.Value(); pk && requires && !a.Herd.Predator && closed {
			races = append(races, a.Definition)
		}
	}
	danger := map[Resource]bool{}
	if len(races) > 0 {
		hostiles, hostilesKnown := f.Hostiles.Value()
		if !hostilesKnown {
			return none, fmt.Errorf("%w: the hostile count is unread", ErrAnimalExposure)
		}
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
	}
	sort.Slice(placed, func(i, j int) bool { return placed[i].ID < placed[j].ID })
	for _, a := range placed {
		area, _ := a.AllowedArea.Value()
		requires, pk := a.RequiresPen.Value()
		bonded, bk := a.Bonded.Value()
		var want string
		switch {
		case a.Herd.Predator && !(pk && !requires && bk && bonded):
			want = wild
		case pk && requires:
			if !closed || danger[a.Definition] {
				want = barn
			}
		case pk && bk && bonded:
			want = companion
		default:
			continue
		}
		roamer := pk && requires && !a.Herd.Predator
		if want == "" && !roamer || area == want || area != "" && area != barn {
			continue
		}
		return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryAllowedArea, Argument: want}, nil
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
