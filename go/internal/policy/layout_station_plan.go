package policy

import (
	"encoding/json"
	"errors"
	"fmt"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The station chain in advisory mode. At replan time the live colony is read
// into StationFacts, the chain (BuildStationGraph, ClusterStations, Embed) runs
// over it, and each group is matched to the rooms already built for its
// predicted role. The result is a StationAdvice the caller records; nothing
// here changes what the old planner builds. Unknown facts are never zero: a
// fact the colony has not read fails the advice with ErrStationFacts.

// ErrStationFacts marks advice that could not be assembled because a fact it
// reads is unknown; the error text names the fact.
var ErrStationFacts = errors.New("station facts unknown")

func unknownStationFact(name string) error { return fmt.Errorf("%w: %s", ErrStationFacts, name) }

// StationFacts is what the advice reads, each part from the model that owns
// it: the catalog rows (Catalog, Furniture, Dining, Items, Recipes, StackLimit,
// Perishable), the population censuses, the colonists' food census, the work
// ledger's orders, the resource demand and the saved plan with the survey it
// was grown over.
type StationFacts struct {
	Catalog   StationCatalog
	Furniture RoomFurniture
	Dining    DiningFurniture
	Items     ItemFacts
	Recipes   map[string]RecipeFlow
	// StackLimit and Perishable cover every resource of Stock and of the
	// recipes' items.
	StackLimit map[Resource]int64
	Perishable map[Resource]bool

	// Colonists is the colony's people; Patients and Surgical size the
	// hospital ward as SelectHospitalBed does; Prisoners is the held prisoners
	// and Animals the pen herd; WorkPawns say who is a baby.
	Colonists domain.Fact[int64]
	Patients  domain.Fact[[]CarePawn]
	Surgical  []PawnID
	Prisoners domain.Fact[int]
	Animals   int
	WorkPawns domain.Fact[[]WorkPawn]
	// Food is the colonists' food census; its consumers' rates sum to the
	// nutrition a day.
	Food domain.Fact[FoodSupply]
	// Orders are the work ledger's wanted orders; Stock the resource demand's
	// stock levels (DerivedDemand.Needs).
	Orders domain.Fact[[]OrderSpec]
	Stock  domain.Fact[map[Resource]int64]

	// RoofDistance is the game's roof support distance.
	RoofDistance float64
	// Plan is the saved plan the advice reads zones, reservations and built
	// rooms from; Built the ids of its rooms that stand as planned
	// (BuiltRooms); Survey the map it was grown over.
	Plan   LayoutPlan
	Built  map[int32]bool
	Survey MapSurvey
	// Tile also tiles the embedding into a plan (TileEmbedding, #2876). Off,
	// the old planner stays the only one; the tiled plan is advisory until the
	// epic (#2855) switches over.
	Tile bool
	// OutdoorCurve is the seasonal outdoor temperature curve (twelve means, C)
	// and FullRotRateC the catalog's full-rot temperature; the climate advice
	// (groupClimate) needs both and is left off while either is unknown.
	OutdoorCurve domain.Fact[[]float64]
	FullRotRateC float64
	// VentAvailable is whether the vent is researched (#2915); unknown or
	// false leaves vents out of the tiled plan.
	VentAvailable domain.Fact[bool]
	// Crops are the crops the colony grows (the caller's choice, not the
	// chain's) and FoodReserveDays the stored-food buffer their fields budget:
	// each edible crop with a known capacity target is a field node (#2920).
	Crops           []CropChoice
	FoodReserveDays float64
}

// StationPopulationOf is the people the graph sizes beds for. Babies sleep in
// cribs, so they leave the colonists; the injured are colonists who need a
// medical bed now (illPatients).
func StationPopulationOf(f StationFacts) (StationPopulation, error) {
	colonists, ok := f.Colonists.Value()
	if !ok {
		return StationPopulation{}, unknownStationFact("colonists")
	}
	ill, ok := illPatients(f.Patients, f.Surgical)
	if !ok {
		return StationPopulation{}, unknownStationFact("patients")
	}
	prisoners, ok := f.Prisoners.Value()
	if !ok {
		return StationPopulation{}, unknownStationFact("prisoners")
	}
	pawns, ok := f.WorkPawns.Value()
	if !ok {
		return StationPopulation{}, unknownStationFact("pawn stages")
	}
	babies := BabyCount(pawns)
	return StationPopulation{Colonists: max(int(colonists)-babies, 0), Injured: len(ill), Prisoners: prisoners, Babies: babies, Animals: f.Animals}, nil
}

// BabyCount is the pawns still in a crib: the newborn and baby stages
// ChildRoomNeeds gives a nursery bed. A pawn whose stage is unknown owes
// nothing, as there.
func BabyCount(pawns []WorkPawn) int {
	n := 0
	for _, p := range pawns {
		bt, ok := p.Biotech.Value()
		if !ok {
			continue
		}
		if stage, ok := bt.DevelopmentalStage.Value(); ok && (stage == "Newborn" || stage == "Baby") {
			n++
		}
	}
	return n
}

// HumanNutritionPerDay is the nutrition the colonists eat a day: the sum of
// the consumers' rates. False when the census is incomplete or any rate is
// unknown.
func HumanNutritionPerDay(supply FoodSupply) (float64, bool) {
	if complete, ok := supply.Complete.Value(); ok && !complete {
		return 0, false
	}
	total := 0.0
	for _, c := range supply.Consumers {
		rate, ok := c.NutritionPerDay.Value()
		if !ok {
			return 0, false
		}
		total += rate
	}
	return total, true
}

// GraphInput assembles the graph's input, or names the unknown fact.
func (f StationFacts) GraphInput() (StationGraphInput, error) {
	population, err := StationPopulationOf(f)
	if err != nil {
		return StationGraphInput{}, err
	}
	supply, ok := f.Food.Value()
	if !ok {
		return StationGraphInput{}, unknownStationFact("food census")
	}
	nutrition, ok := HumanNutritionPerDay(supply)
	if !ok {
		return StationGraphInput{}, unknownStationFact("nutrition per day")
	}
	orders, ok := f.Orders.Value()
	if !ok {
		return StationGraphInput{}, unknownStationFact("work ledger orders")
	}
	stock, ok := f.Stock.Value()
	if !ok {
		return StationGraphInput{}, unknownStationFact("resource demand")
	}
	return StationGraphInput{
		Catalog: f.Catalog, Furniture: f.Furniture, Dining: f.Dining, Items: f.Items, Recipes: f.Recipes,
		Population: population, NutritionPerDay: nutrition, Orders: orders,
		Stock: stock, StackLimit: f.StackLimit, Perishable: f.Perishable,
		Crops: f.Crops, FoodReserveDays: f.FoodReserveDays,
	}, nil
}

// InputsKey names what the chain varies with: the graph's population,
// nutrition, orders and stock (or the unknown fact), the dining furniture and
// roof distance, and the plan with the rooms standing. Two facts with one key
// give one advice over one survey. The definitions (Catalog, Furniture,
// Items, Recipes and the stack and rot rows of the stock) are the load's, so
// the caller keys the load beside it.
func (f StationFacts) InputsKey() string {
	plan, _ := json.Marshal(f.Plan) // the plan holds pointers; the saved form is its value
	key := fmt.Sprint(f.Dining, f.RoofDistance, f.Built, f.Tile, f.OutdoorCurve) + string(plan)
	input, err := f.GraphInput()
	if err != nil {
		return key + err.Error()
	}
	return key + fmt.Sprint(input.Population, input.NutritionPerDay, input.Orders, input.Stock, input.FoodReserveDays, input.Crops)
}

// GroupAdvice is one room group with the built rooms matched to it.
type GroupAdvice struct {
	Group RoomGroup
	// Rooms are the matched built rooms' ids and Capacity their summed area.
	Rooms    []int32
	Capacity int
	// Outgrown: the group needs more floor than its built rooms give.
	Outgrown bool
	// Climate is the cold room's climate answer, beside its cooling station:
	// the watts of each opening to the outdoors (#2940); nil for a group that
	// does not require cold or when the curve or the game's numbers are unknown.
	Climate *GroupClimate
}

// StationAdvice is the chain's result over the live colony.
type StationAdvice struct {
	Graph      StationGraph
	Clustering Clustering
	Embedding  Embedding
	// Sites places the plan's power sites (PowerSiteNodes), advisory beside the
	// authoritative PlanUtilities.
	Sites  SiteEmbedding
	Groups []GroupAdvice
	// Tiled is the embedding tiled into a plan; nil unless StationFacts.Tile.
	Tiled *TileResult
}

// AdviseStations runs the station chain over the live colony. It reads the
// plan and survey and changes neither.
func AdviseStations(f StationFacts) (StationAdvice, error) {
	if f.Built == nil {
		return StationAdvice{}, unknownStationFact("construction census")
	}
	if f.Dining.Table.Def == "" {
		return StationAdvice{}, unknownStationFact("dining furniture")
	}
	input, err := f.GraphInput()
	if err != nil {
		return StationAdvice{}, err
	}
	graph, err := BuildStationGraph(input)
	if err != nil {
		return StationAdvice{}, err
	}
	clustering, err := ClusterStations(ClusterInput{Graph: graph, Catalog: f.Catalog, RoofDistance: f.RoofDistance})
	if err != nil {
		return StationAdvice{}, err
	}
	embedIn := EmbedInput{Groups: clustering.Groups, Zones: f.Plan.Zones, Reservations: f.Plan.Reserved(), Survey: f.Survey, RoofDistance: f.RoofDistance, Fields: FieldEmbedLinks(graph, clustering.Groups)}
	embedding, err := Embed(embedIn)
	if err != nil {
		return StationAdvice{}, err
	}
	graph.Sites = PowerSiteNodes(f.Plan)
	out := StationAdvice{Graph: graph, Clustering: clustering, Embedding: embedding}
	if len(graph.Sites) > 0 {
		if out.Sites, err = EmbedSites(SiteEmbedInput{Embed: embedIn, Embedding: embedding, Nodes: graph.Sites}); err != nil {
			return StationAdvice{}, err
		}
	}
	if f.Tile {
		steel, _ := f.Items.MarketValue(siteSteelUnit) // unknown stays zero: the airlocks and vents go unpriced
		tiled, err := TileEmbedding(TileInput{Groups: clustering.Groups, Embedding: embedding, Catalog: f.Catalog, Zones: f.Plan.Zones, Reservations: f.Plan.Reserved(), Survey: f.Survey, OutdoorCurve: f.OutdoorCurve, RoofDistance: f.RoofDistance, FullRotRateC: f.FullRotRateC, SteelValue: steel, VentAvailable: f.VentAvailable, Light: stationLightPrice(f, steel)})
		if err != nil {
			return StationAdvice{}, err
		}
		out.Tiled = &tiled
	}
	for _, m := range MatchGroupRooms(clustering.Groups, BuiltRegions(f.Plan, f.Built)) {
		g := clustering.Groups[m.Group]
		out.Groups = append(out.Groups, GroupAdvice{Group: g, Rooms: m.Rooms, Capacity: m.Capacity, Outgrown: GroupOutgrown(g, m), Climate: groupClimate(f, g)})
	}
	return out, nil
}

// BuiltRegion is a planned room that stands as planned, with the role the
// game gives its furniture.
type BuiltRegion struct {
	Room int32
	Role RoomRole
	// Planned is the planned role of a room the native role does not tell
	// apart (matchedPlanned): the freezer and the butchery; empty otherwise.
	Planned PlannedRole
	Region  Region
}

// matchedPlanned are the planned rooms matched to groups by their planned
// role, with the native role the game gives them: a freezer is a storeroom
// and a butchery reads as a kitchen, but each is matched only to the group
// that became it (PlannedRoleOfGroup), not to the plain storage or kitchen
// groups.
var matchedPlanned = map[PlannedRole]RoomRole{
	PlannedFreezer:  RoomRoleStoreroom,
	PlannedButchery: RoomRoleKitchen,
}

// matchKey is the key a room is matched to groups under.
func (b BuiltRegion) matchKey() string {
	if b.Planned != "" {
		return string(b.Planned)
	}
	return string(b.Role)
}

// matchKeyOfGroup is the key a group is matched to rooms under.
func matchKeyOfGroup(g RoomGroup) string {
	if p, ok := PlannedRoleOfGroup(g); ok {
		if _, special := matchedPlanned[p]; special {
			return string(p)
		}
	}
	return string(g.Role)
}

// BuiltRegions are the plan's standing rooms that carry a native role
// (moduleRoomRoles, and the freezer and butchery of matchedPlanned), by room
// id. Outdoor rooms and rooms with no native role are left out, and so is a
// room whose successor stands: the pair is one room.
func BuiltRegions(plan LayoutPlan, built map[int32]bool) []BuiltRegion {
	var out []BuiltRegion
	replaced := map[int32]bool{}
	for _, r := range plan.AllRooms() {
		if built[r.ID] && r.Replaces != 0 {
			replaced[r.Replaces] = true
		}
	}
	for _, r := range plan.AllRooms() {
		role, ok := moduleRoomRoles[r.Role]
		var planned PlannedRole
		if native, special := matchedPlanned[r.Role]; special {
			role, ok, planned = native, true, r.Role
		}
		if !ok || r.Outdoor || !built[r.ID] || replaced[r.ID] {
			continue
		}
		out = append(out, BuiltRegion{Room: r.ID, Role: role, Planned: planned, Region: r.Region})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Room < out[j].Room })
	return out
}

// GroupRooms is the built rooms matched to one group.
type GroupRooms struct {
	Group int
	// Rooms are the matched room ids, ascending; Capacity their summed floor.
	Rooms    []int32
	Capacity int
}

// MatchGroupRooms matches built rooms to the groups of their role. Groups and
// rooms of a role are each taken largest first (a group by Area, a room by
// floor, ties by lower id); the n-th largest group gets the n-th largest room.
// A room left over when the role has more rooms than groups goes to the group
// of that role with the most floor still short (Area less the capacity it has,
// ties to the lower group id), so a role's rooms are always all assigned. A
// group of a role with no built room has no rooms. The result has one entry per
// group, in group order.
func MatchGroupRooms(groups []RoomGroup, built []BuiltRegion) []GroupRooms {
	out := make([]GroupRooms, len(groups))
	for i, g := range groups {
		out[i].Group = g.ID
	}
	index := map[int]int{}
	for i, g := range groups {
		index[g.ID] = i
	}
	byRole := map[string][]RoomGroup{}
	for _, g := range groups {
		k := matchKeyOfGroup(g)
		byRole[k] = append(byRole[k], g)
	}
	roomsByRole := map[string][]BuiltRegion{}
	for _, b := range built {
		k := b.matchKey()
		roomsByRole[k] = append(roomsByRole[k], b)
	}
	roles := make([]string, 0, len(byRole))
	for role := range byRole {
		roles = append(roles, role)
	}
	slices.Sort(roles)
	for _, role := range roles {
		gs, rs := byRole[role], roomsByRole[role]
		sort.Slice(gs, func(i, j int) bool {
			if gs[i].Area != gs[j].Area {
				return gs[i].Area > gs[j].Area
			}
			return gs[i].ID < gs[j].ID
		})
		sort.Slice(rs, func(i, j int) bool {
			if a, b := rs[i].Region.Area(), rs[j].Region.Area(); a != b {
				return a > b
			}
			return rs[i].Room < rs[j].Room
		})
		give := func(g RoomGroup, r BuiltRegion) {
			m := &out[index[g.ID]]
			m.Rooms = append(m.Rooms, r.Room)
			m.Capacity += r.Region.Area()
		}
		for i, r := range rs {
			if i < len(gs) {
				give(gs[i], r)
				continue
			}
			best := gs[0]
			for _, g := range gs[1:] {
				if short, bestShort := g.Area-out[index[g.ID]].Capacity, best.Area-out[index[best.ID]].Capacity; short > bestShort || short == bestShort && g.ID < best.ID {
					best = g
				}
			}
			give(best, r)
		}
	}
	for i := range out {
		slices.Sort(out[i].Rooms)
	}
	return out
}

// GroupOutgrown reports a group that needs more floor than its matched built
// rooms give. A group with no built room has nothing to outgrow.
func GroupOutgrown(g RoomGroup, m GroupRooms) bool {
	return len(m.Rooms) > 0 && g.Area > m.Capacity
}
