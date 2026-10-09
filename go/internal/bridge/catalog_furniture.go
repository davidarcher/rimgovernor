package bridge

import (
	"cmp"
	"math"
	"slices"

	"google.golang.org/protobuf/proto"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The classes and stats the furniture rules match, never names.
const (
	ClassSarcophagus = "RimWorld.Building_Sarcophagus"
	// ClassDoor is the door class; the animal flap is the buildable one
	// BuildingProperties.roamerCanOpen marks.
	ClassDoor = "RimWorld.Building_Door"
	// StatWorkTableWorkSpeedFactor and StatMedicalTendQualityOffset are the
	// stats a bench's and a medical bed's facility offsets.
	StatWorkTableWorkSpeedFactor = "WorkTableWorkSpeedFactor"
	StatMedicalTendQualityOffset = "MedicalTendQualityOffset"
)

// RoomFurniture is the furniture the room planners place, chosen by rules over
// the catalog rows (policy.RoomFurniture states them); shapes are the rows'
// plannable shapes (PieceShapes), which name the room role family of each
// bench. A catalog with no bed, sarcophagus, animal bed, bench or facility
// the rules need is an error.
func (catalog *DefinitionCatalog) RoomFurniture(shapes map[string]policy.InteriorPieceDef) (policy.RoomFurniture, error) {
	if catalog == nil {
		return policy.RoomFurniture{}, contract("no definition catalog")
	}
	var out policy.RoomFurniture
	beds, err := catalog.bedRanks()
	if err != nil {
		return out, err
	}
	for _, b := range beds.sleeping {
		out.Beds = append(out.Beds, policy.FurnitureBed{Def: b.def, Slots: b.slots, Free: b.free})
	}
	if out.AnimalSpot, out.AnimalBed, err = beds.animalBeds(); err != nil {
		return out, err
	}
	if out.Heater, err = catalog.cheapestHeater(); err != nil {
		return out, err
	}
	if out.AnimalFlap, err = catalog.animalFlap(); err != nil {
		return out, err
	}
	if out.Sarcophagus, err = catalog.cheapestOfClass(ClassSarcophagus); err != nil {
		return out, err
	}
	out.Bench = map[policy.RoomRole]string{}
	for _, role := range []policy.RoomRole{policy.RoomRoleKitchen, policy.RoomRoleWorkshop, policy.RoomRoleLaboratory} {
		if out.Bench[role], err = catalog.cheapestBench(shapes, role); err != nil {
			return out, err
		}
	}
	facilities, err := catalog.facilityRows()
	if err != nil {
		return out, err
	}
	bedLinks, err := catalog.linkedFacilities(out.PrimaryBed())
	if err != nil {
		return out, err
	}
	benchLinks, err := catalog.linkedFacilities(out.Bench[policy.RoomRoleWorkshop])
	if err != nil {
		return out, err
	}
	var medical []string
	for _, b := range beds.all {
		if b.medical {
			medical = append(medical, b.def)
		}
	}
	medicalLinks, err := catalog.linkedFacilities(medical...)
	if err != nil {
		return out, err
	}
	if out.EndTable, err = catalog.bestFacility("an end table", facilities, bedLinks, StatComfort, func(f facilityRow) bool { return f.cardinalToHead }); err != nil {
		return out, err
	}
	if out.Dresser, err = catalog.bestFacility("a dresser", facilities, bedLinks, StatComfort, func(f facilityRow) bool { return !f.adjacent }); err != nil {
		return out, err
	}
	if out.Cabinet, err = catalog.bestFacility("a tool cabinet", facilities, benchLinks, StatWorkTableWorkSpeedFactor, func(facilityRow) bool { return true }); err != nil {
		return out, err
	}
	if out.Monitor, err = catalog.bestFacility("a vitals monitor", facilities, medicalLinks, StatMedicalTendQualityOffset, func(f facilityRow) bool { return f.adjacent && !f.cardinalToHead }); err != nil {
		return out, err
	}
	return out, nil
}

// bedRank is one bed row with the numbers the rules rank it by.
type bedRank struct {
	def                                            string
	slots                                          int32
	free                                           bool
	ratio                                          float64
	comfort                                        float64
	humanlike, medical, counts, crib, unrestricted bool
}

type bedRanks struct {
	all      []bedRank
	sleeping []bedRank
}

// bedRanks reads every buildable Building_Bed. A bed is unrestricted when its
// body size limit is the largest any bed states, the game's "any pawn".
func (catalog *DefinitionCatalog) bedRanks() (bedRanks, error) {
	var out bedRanks
	limit := float32(math.Inf(-1))
	for _, row := range catalog.ThingDefs {
		if bed, err := catalog.ClassIsA(row.GetThingClass(), ClassBed); err != nil {
			return out, err
		} else if bed {
			limit = max(limit, row.GetBuilding().GetBedMaxBodySize())
		}
	}
	for name, row := range catalog.ThingDefs {
		if !Buildable(row) {
			continue
		}
		bed, err := catalog.ClassIsA(row.GetThingClass(), ClassBed)
		if err != nil {
			return out, err
		}
		if !bed {
			continue
		}
		b := row.GetBuilding()
		rank := bedRank{def: name, slots: row.GetSize().GetX(), humanlike: b.GetBedHumanlike(), medical: b.GetBedDefaultMedical(), counts: b.GetBedCountsForBedroomOrBarracks(), crib: b.GetBedCrib(), unrestricted: b.GetBedMaxBodySize() >= limit}
		rank.ratio, rank.comfort, rank.free, err = catalog.comfortPerCost(name)
		if err != nil {
			return out, err
		}
		out.all = append(out.all, rank)
		if rank.humanlike && !rank.medical && rank.counts && !rank.crib && rank.unrestricted && !math.IsInf(rank.ratio, -1) {
			out.sleeping = append(out.sleeping, rank)
		}
	}
	slices.SortFunc(out.sleeping, compareBeds)
	return out, nil
}

// compareBeds orders beds by sleeping slots, then the costed ones by comfort
// per cost (most first) ahead of the free ones by comfort, then by name.
func compareBeds(a, b bedRank) int {
	if c := cmp.Compare(a.slots, b.slots); c != 0 {
		return c
	}
	if a.free != b.free {
		if a.free {
			return 1
		}
		return -1
	}
	if a.free {
		return cmp.Or(cmp.Compare(b.comfort, a.comfort), cmp.Compare(a.def, b.def))
	}
	return cmp.Or(cmp.Compare(b.ratio, a.ratio), cmp.Compare(a.def, b.def))
}

// comfortPerCost is a bed's Comfort per unit of cost at its best stuff (the
// market value of its adjusted costs), free when some stuff builds it for
// nothing, and negative infinity when the game shows no Comfort for a bed
// that costs something.
func (catalog *DefinitionCatalog) comfortPerCost(name string) (ratio, comfort float64, free bool, err error) {
	stuffs, err := catalog.AllowedStuffs(name)
	if err != nil {
		return 0, 0, false, err
	}
	if len(stuffs) == 0 {
		stuffs = []string{""}
	}
	ratio = math.Inf(-1)
	for _, stuff := range stuffs {
		value, shown, err := catalog.ShownStatValue(name, stuff, StatComfort)
		if err != nil {
			return 0, 0, false, err
		}
		cost, err := catalog.costValue(name, stuff)
		if err != nil {
			return 0, 0, false, err
		}
		if cost <= 0 {
			free, comfort = true, max(comfort, float64(value))
			continue
		}
		if shown {
			ratio = math.Max(ratio, float64(value)/cost)
		}
	}
	if free {
		ratio = math.Inf(1)
	}
	return ratio, comfort, free, nil
}

// animalBeds are the free bed and the best costed bed an animal of any size
// can use (not humanlike, no body size limit): the sleeping spot and the bed.
func (r bedRanks) animalBeds() (spot, bed string, err error) {
	var spots, beds []bedRank
	for _, b := range r.all {
		switch {
		case b.humanlike || !b.unrestricted:
		case b.free:
			spots = append(spots, b)
		case !math.IsInf(b.ratio, -1):
			beds = append(beds, b)
		}
	}
	if len(spots) == 0 || len(beds) == 0 {
		return "", "", contract("catalog has %d free and %d costed animal beds, the barn needs one of each", len(spots), len(beds))
	}
	slices.SortFunc(spots, func(a, b bedRank) int { return cmp.Compare(a.def, b.def) })
	slices.SortFunc(beds, compareBeds)
	return spots[0].def, beds[0].def, nil
}

// animalFlap is the buildable door roaming animals can open (a Building_Door
// whose building properties say roamerCanOpen), by name when several. A catalog
// with none is an error: nothing stands in for the flap between a pen and its
// barn.
func (catalog *DefinitionCatalog) animalFlap() (string, error) {
	var best string
	for name, row := range catalog.ThingDefs {
		if !Buildable(row) || !row.GetBuilding().GetRoamerCanOpen() {
			continue
		}
		door, err := catalog.ClassIsA(row.GetThingClass(), ClassDoor)
		if err != nil {
			return "", err
		}
		if door && (best == "" || name < best) {
			best = name
		}
	}
	if best == "" {
		return "", contract("catalog has no buildable animal flap (a %s that roamers can open)", ClassDoor)
	}
	return best, nil
}

// cheapestOfClass is the cheapest buildable def of the class (or a subclass),
// by name when equal.
func (catalog *DefinitionCatalog) cheapestOfClass(class string) (string, error) {
	var best string
	bestCost := math.Inf(1)
	for name, row := range catalog.ThingDefs {
		if !Buildable(row) {
			continue
		}
		match, err := catalog.ClassIsA(row.GetThingClass(), class)
		if err != nil {
			return "", err
		}
		if !match {
			continue
		}
		cost, err := catalog.CheapestCostValue(name)
		if err != nil {
			return "", err
		}
		if cost < bestCost || cost == bestCost && name < best {
			best, bestCost = name, cost
		}
	}
	if best == "" {
		return "", contract("catalog has no buildable %s", class)
	}
	return best, nil
}

// cheapestHeater is the cheapest buildable def that heats its room from a
// power draw (a CompProperties_TempControl with a positive energyPerSecond and
// a consuming power comp), by name when equal. A cooler's energy is negative
// and a campfire has no power comp, so neither matches.
func (catalog *DefinitionCatalog) cheapestHeater() (string, error) {
	var best string
	bestCost := math.Inf(1)
	for name, row := range catalog.ThingDefs {
		if !Buildable(row) {
			continue
		}
		control := compOf(row, (*d.CompPropertiesAny).GetCompProperties_TempControl)
		power := compOf(row, (*d.CompPropertiesAny).GetCompProperties_Power)
		if control == nil || power == nil || control.GetEnergyPerSecond() <= 0 || power.GetBasePowerConsumption() <= 0 {
			continue
		}
		cost, err := catalog.CheapestCostValue(name)
		if err != nil {
			return "", err
		}
		if cost < bestCost || cost == bestCost && name < best {
			best, bestCost = name, cost
		}
	}
	if best == "" {
		return "", contract("catalog has no buildable heater (a temperature control that heats from a power draw)")
	}
	return best, nil
}

// cheapestBench is the cheapest buildable work table of a room role that a
// worker uses from the floor in front of it, by name when equal.
func (catalog *DefinitionCatalog) cheapestBench(shapes map[string]policy.InteriorPieceDef, role policy.RoomRole) (string, error) {
	var best string
	bestCost := math.Inf(1)
	for name, shape := range shapes {
		if shape.Family != role || !shape.WorkedFromFront() {
			continue
		}
		if work := catalog.ThingDefs[name].GetBuilding().GetWorkTableRoomRole(); work != string(role) {
			continue
		}
		cost, err := catalog.CheapestCostValue(name)
		if err != nil {
			return "", err
		}
		if cost < bestCost || cost == bestCost && name < best {
			best, bestCost = name, cost
		}
	}
	if best == "" {
		return "", contract("catalog has no buildable %s work table worked from the floor in front of it", role)
	}
	return best, nil
}

// facilityRow is a buildable def's facility comp with the link rules the
// templates read.
type facilityRow struct {
	def             string
	offsets         map[string]float32
	maxDistance     float64
	maxSimultaneous int32
	adjacent        bool
	cardinalToHead  bool
}

// facilityRows are the buildable defs carrying a CompProperties_Facility.
func (catalog *DefinitionCatalog) facilityRows() (map[string]facilityRow, error) {
	out := map[string]facilityRow{}
	for name, row := range catalog.ThingDefs {
		if !Buildable(row) {
			continue
		}
		facility, ok := exactComp[*d.CompProperties_Facility](row)
		if !ok {
			continue
		}
		f := facilityRow{def: name, offsets: map[string]float32{}, maxDistance: float64(facility.GetMaxDistance()), maxSimultaneous: facility.GetMaxSimultaneous(),
			cardinalToHead: facility.GetMustBePlacedAdjacentCardinalToBedHead() || facility.GetMustBePlacedAdjacentCardinalToAndFacingBedHead()}
		f.adjacent = f.cardinalToHead || facility.GetMustBePlacedAdjacent()
		for _, offset := range facility.GetStatOffsets() {
			f.offsets[offset.GetValue().GetStat()] += offset.GetValue().GetValue()
		}
		out[name] = f
	}
	return out, nil
}

// linkedFacilities are the facility defs any of the defs' CompAffectedByFacilities
// lists as linkable.
func (catalog *DefinitionCatalog) linkedFacilities(defs ...string) (map[string]bool, error) {
	out := map[string]bool{}
	for _, name := range defs {
		row := catalog.ThingDefs[name]
		if row == nil {
			return nil, contract("no row for %s", name)
		}
		affected, ok := exactComp[*d.CompProperties_AffectedByFacilities](row)
		if !ok {
			continue
		}
		for _, facility := range affected.GetLinkableFacilities() {
			out[facility] = true
		}
	}
	return out, nil
}

// bestFacility is the linkable facility that offsets stat and fits the slot
// with the most offset per unit of cost (a free one is the best), by name
// when equal.
func (catalog *DefinitionCatalog) bestFacility(what string, facilities map[string]facilityRow, linked map[string]bool, stat string, fits func(facilityRow) bool) (policy.FacilityLink, error) {
	var best facilityRow
	bestScore := math.Inf(-1)
	for name, f := range facilities {
		offset, offsets := f.offsets[stat]
		if !linked[name] || !offsets || offset <= 0 || !fits(f) {
			continue
		}
		cost, err := catalog.CheapestCostValue(name)
		if err != nil {
			return policy.FacilityLink{}, err
		}
		score := math.Inf(1)
		if cost > 0 {
			score = float64(offset) / cost
		}
		if score > bestScore || score == bestScore && name < best.def {
			best, bestScore = f, score
		}
	}
	if best.def == "" {
		return policy.FacilityLink{}, contract("catalog has no facility that is %s: nothing the interior templates' bed or bench links offsets %s in that way", what, stat)
	}
	return policy.FacilityLink{Def: best.def, MaxDistance: best.maxDistance, MaxSimultaneous: best.maxSimultaneous, Adjacent: best.adjacent, CardinalToHead: best.cardinalToHead}, nil
}

// exactComp is the first comp of row that is exactly the message type T, not
// a subclass with a message of its own (a gravship facility is no bed or bench
// facility); false when the def has none.
func exactComp[T proto.Message](row *d.ThingDef) (T, bool) {
	for _, comp := range row.GetComps() {
		msg := comp.GetValue().ProtoReflect()
		which := msg.WhichOneof(msg.Descriptor().Oneofs().ByName("value"))
		if which == nil {
			continue
		}
		if sub, ok := msg.Get(which).Message().Interface().(T); ok {
			return sub, true
		}
	}
	var none T
	return none, false
}
