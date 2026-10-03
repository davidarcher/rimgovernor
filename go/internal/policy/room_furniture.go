package policy

import (
	"fmt"
	"slices"
)

// FurnitureBed is one bed an adult colonist may sleep in, as the catalog rows
// rank it (DefinitionCatalog.RoomFurniture). Slots is the game's sleeping
// slot count (the bed's width); Free is a bed that costs nothing to build, a
// sleeping spot.
type FurnitureBed struct {
	Def   string
	Slots int32
	Free  bool
}

// FacilityLink is a building the game links to a bed or bench as a facility
// (CompProperties_Facility), with the link rules the interior templates lay
// it out by.
type FacilityLink struct {
	Def string
	// MaxDistance is the furthest centre-to-centre distance at which the
	// facility links; MaxSimultaneous how many things one facility links.
	MaxDistance     float64
	MaxSimultaneous int32
	// Adjacent is set when the facility must stand against the thing it
	// serves (mustBePlacedAdjacent and its bed-head variants), CardinalToHead
	// when it must stand cardinally beside a bed's head.
	Adjacent, CardinalToHead bool
}

// RoomFurniture is the furniture the room planners place, chosen by rules over
// the catalog rows and never by name (DefinitionCatalog.RoomFurniture). It is
// carried in PieceShapes, a threaded value, never a global:
//   - Beds are the beds a colonist of any size may sleep in (humanlike, not a
//     crib, no body size limit) that count for a bedroom or barracks and are
//     not medical by default, by sleeping slots, then the ones that cost
//     something by Comfort per unit of cost at the best stuff, then the free
//     ones by Comfort (the same rule as the dining chair);
//   - Sarcophagus is the Building_Sarcophagus, the cheapest when several;
//   - AnimalSpot is the free bed of the kind animals use (not humanlike, no
//     body size limit) and AnimalBed the costed one with the most Comfort per
//     cost;
//   - Bench is, per room role, the cheapest buildable work table of that role
//     worked from the floor in front of it (the template's default bench);
//   - EndTable and Dresser are the Comfort facilities a bed links, the one
//     that stands beside the bed's head and the one that does not; Cabinet
//     is the work speed facility a workshop bench links and Monitor the tend
//     quality facility a medical bed links, each the best offset per cost.
type RoomFurniture struct {
	Beds        []FurnitureBed
	Sarcophagus string
	AnimalSpot  string
	AnimalBed   string
	Bench       map[RoomRole]string

	EndTable, Dresser, Cabinet, Monitor FacilityLink
}

// slotBeds are the beds of one slot count in preference order.
func (f RoomFurniture) slotBeds(slots int32) []FurnitureBed {
	var out []FurnitureBed
	for _, b := range f.Beds {
		if b.Slots == slots {
			out = append(out, b)
		}
	}
	return out
}

func bedDefs(beds []FurnitureBed) []string {
	out := make([]string, len(beds))
	for i, b := range beds {
		out[i] = b.Def
	}
	return out
}

// SleepingBeds are the single-slot beds in preference order: what the
// sleeping and hospital planners stage, a sleeping spot last.
func (f RoomFurniture) SleepingBeds() []string { return bedDefs(f.slotBeds(1)) }

// HospitalBeds are the beds the hospital planner stages, the sleeping beds
// except a bedroll, which only the sleeping planner stages (it checks the
// stuff on hand, the hospital planner does not).
func (f RoomFurniture) HospitalBeds() []string {
	return slices.DeleteFunc(f.SleepingBeds(), func(def string) bool { return sleepingBedrolls[def] })
}

// PrimaryBed is the single bed the planners stage first (the jail's bed, the
// bedroom and hospital templates' default bed); empty when the catalog has
// none.
func (f RoomFurniture) PrimaryBed() string {
	if beds := f.slotBeds(1); len(beds) > 0 {
		return beds[0].Def
	}
	return ""
}

// CoupleBed is the double bed staged first for a couple; empty when the
// catalog has none.
func (f RoomFurniture) CoupleBed() string {
	for _, b := range f.slotBeds(2) {
		if !b.Free {
			return b.Def
		}
	}
	return ""
}

// SleepingLadder is the bed definitions a bed step may stage in preference
// order; for a couple each double comes first, ahead of the single of its
// rank.
func (f RoomFurniture) SleepingLadder(couple bool) []string {
	singles := f.slotBeds(1)
	if !couple {
		return bedDefs(singles)
	}
	var doubles []FurnitureBed
	for _, b := range f.slotBeds(2) {
		if !b.Free {
			doubles = append(doubles, b)
		}
	}
	var out []string
	for i := 0; i < max(len(singles), len(doubles)); i++ {
		if i < len(doubles) {
			out = append(out, doubles[i].Def)
		}
		if i < len(singles) {
			out = append(out, singles[i].Def)
		}
	}
	return out
}

// Facilities are the facility links, in a fixed order.
func (f RoomFurniture) Facilities() []FacilityLink {
	return []FacilityLink{f.EndTable, f.Dresser, f.Cabinet, f.Monitor}
}

// BenchFor is the default bench of a room role's template.
func (f RoomFurniture) BenchFor(role RoomRole) string { return f.Bench[role] }

// Definitions are every definition the planners read availability and stuff
// for, sorted and without repeats: the beds, the sarcophagus, the animal
// beds, the default benches and the facilities.
func (f RoomFurniture) Definitions() []string {
	out := bedDefs(f.Beds)
	out = append(out, f.Sarcophagus, f.AnimalSpot, f.AnimalBed)
	for _, bench := range f.Bench {
		out = append(out, bench)
	}
	for _, link := range f.Facilities() {
		out = append(out, link.Def)
	}
	out = slices.DeleteFunc(out, func(s string) bool { return s == "" })
	slices.Sort(out)
	return slices.Compact(out)
}

// Validate checks the furniture is a set the interior templates can lay out:
// a bed, a double bed, a sarcophagus, both animal beds, a default bench of
// each room role with a template, and facilities whose link rules match the
// way the templates place them (the end table beside the bed's head, the
// dresser and the monitor, the cabinet shared by two benches).
func (f RoomFurniture) Validate() error {
	if f.PrimaryBed() == "" {
		return fmt.Errorf("room furniture has no single bed")
	}
	if f.CoupleBed() == "" {
		return fmt.Errorf("room furniture has no costed double bed")
	}
	for role, def := range map[string]string{"sarcophagus": f.Sarcophagus, "animal sleeping spot": f.AnimalSpot, "animal bed": f.AnimalBed} {
		if def == "" {
			return fmt.Errorf("room furniture has no %s", role)
		}
	}
	for _, role := range []RoomRole{RoomRoleKitchen, RoomRoleWorkshop, RoomRoleLaboratory} {
		if f.Bench[role] == "" {
			return fmt.Errorf("room furniture has no default %s bench", role)
		}
	}
	for _, check := range []struct {
		what string
		link FacilityLink
		ok   func(FacilityLink) bool
		need string
	}{
		{"end table", f.EndTable, func(l FacilityLink) bool { return l.CardinalToHead && l.MaxSimultaneous >= 1 }, "stand cardinally beside a bed's head"},
		{"dresser", f.Dresser, func(l FacilityLink) bool { return !l.Adjacent && l.MaxDistance > 0 && l.MaxSimultaneous >= 1 }, "link at a distance"},
		{"tool cabinet", f.Cabinet, func(l FacilityLink) bool { return l.MaxSimultaneous >= 2 && l.MaxDistance > 0 }, "link two benches"},
		{"vitals monitor", f.Monitor, func(l FacilityLink) bool { return l.Adjacent && !l.CardinalToHead && l.MaxSimultaneous >= 1 }, "stand against the bed it serves"},
	} {
		if check.link.Def == "" {
			return fmt.Errorf("room furniture has no %s facility", check.what)
		}
		if !check.ok(check.link) {
			return fmt.Errorf("the interior templates place %s %s as a facility that must %s, its row says otherwise (%+v)", check.what, check.link.Def, check.need, check.link)
		}
	}
	return nil
}
