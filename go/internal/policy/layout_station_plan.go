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
	// Herds are the pen herd by race, Races the animal race rows and Pasture the
	// worst-quadrum growth per terrain: the pen nodes' products and hunger
	// (#2996). Herds empty asks for no pen.
	Herds     []StationHerd
	Races     AnimalRaceCatalog
	Pasture   domain.Fact[PastureTable]
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
	Plan  LayoutPlan
	Built map[int32]bool
	// Pinned are the rooms the replan holds where they stand (TilePinned):
	// a pinned room stands where it is in the embedding, and one
	// not yet built is a rebuild in flight, which holds every built room.
	Pinned map[int32]bool
	Survey MapSurvey
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
	// Suites are the target-sized bedrooms the colony owes (SuiteWishes, #2939):
	// each is a bedroom node of its own in the graph.
	Suites []SuiteWish
	// ExpectedMood is the colonists' mean mood level, 0 to 1, the mood price's
	// baseline (#2975, #2878); unknown leaves the comfort tiers and the lost role
	// effects unpriced.
	ExpectedMood domain.Fact[float64]
	// Mismatches are the standing rooms whose native role is not the one
	// PredictRole gives their contents (GuardNativeRoles, #2898): a cost input
	// and a split move of the regrouping.
	Mismatches []RoleMismatch
	// Derive marks the facts of a first plan, read from a survey, the colonists
	// and the tier before the colony is: a station fact the colony has not read
	// (the census, the orders, the stock) then leaves the graph at the seed rooms
	// instead of failing the advice, and the rest grows on replan (#2958).
	Derive bool
	// ThroneArea is the interior cells of the throne room a title asks (the
	// growth's ThroneArea); 0 is none owed, unknown asks for none and says so.
	ThroneArea domain.Fact[int]
	// RetireShelter is the review's ShelterRetirable: the plan's shelter is no
	// longer owed a node and leaves with the next plan.
	RetireShelter bool
	// Demand is the departments' room demand (the training range's stands and
	// the ring's markers, #3003); its zero value asks for none.
	Demand RoomDemand
	// Child are the shapes of the child rooms the colony owes (ChildRoomShapes);
	// the playroom, classroom and deathrest chamber among them are room nodes.
	Child []ChildRoomShape
}

// seedRooms are the special rooms the plan is owed or holds: the day-1 shelter
// while nothing stands and the plan holds none, or while the plan's shelter is
// not retirable (RetireShelter), and the throne room the plan lacks, or the
// largest it holds, so a standing one is a node to pin (#3003). The shelter
// needs the colonists; unknown, the advice cannot size it.
func (f StationFacts) seedRooms() (SeedRooms, error) {
	var s SeedRooms
	held := 0
	for _, r := range f.Plan.AllRooms() {
		if r.Role == PlannedThrone {
			held = max(held, int(r.Region.Bounds().Width)*int(r.Region.Bounds().Height))
		}
	}
	s.ThroneArea = held
	if area, ok := f.ThroneArea.Value(); ok && area > held {
		s.ThroneArea = area
	}
	if slices.ContainsFunc(f.Plan.AllRooms(), func(r PlannedRoom) bool { return r.Role == PlannedShelter }) {
		if f.RetireShelter {
			return s, nil
		}
	} else {
		for _, standing := range f.Built {
			if standing {
				return s, nil
			}
		}
	}
	colonists, ok := f.Colonists.Value()
	if !ok {
		return SeedRooms{}, unknownStationFact("colonists")
	}
	s.Shelter, s.Colonists, s.Cold, s.Hot = true, int(colonists), f.Survey.Cold, f.Survey.Hot
	return s, nil
}

// trainingAsk is the range stands and ring markers the graph holds nodes for
// (#3003): what the demand asks (RoomDemand.Ranges, Rings) or the plan's
// standing range and ring already hold, whichever is more, so a standing room
// is a node to pin and the demand only widens it. Zero asks for no such room.
func (f StationFacts) trainingAsk() (stands, markers int) {
	stands, markers = min(f.Demand.Ranges, RangeMaxStands), min(f.Demand.Rings, RingMaxMarkers)
	if rooms := f.Plan.RangeRooms(); len(rooms) > 0 {
		stands = max(stands, int(RangeStandCount(rooms[0])))
	}
	if rooms := f.Plan.RingRooms(); len(rooms) > 0 {
		markers = max(markers, rooms[0].Markers)
	}
	return stands, markers
}

// regroupPrice is what the regrouping prices conflicts with: a colonist's work
// from the food census and crops, the mood model and the expected mood.
func (f StationFacts) regroupPrice() RegroupPrice {
	return RegroupPrice{Work: StationWorkPrice(f), Mood: f.Catalog.Mood, Expected: f.ExpectedMood}
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
	return StationPopulation{Colonists: max(int(colonists)-babies, 0), Injured: len(ill), Prisoners: prisoners, Babies: babies, Animals: f.Animals, Herds: f.Herds}, nil
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
	seed, err := f.seedRooms()
	if err != nil {
		return StationGraphInput{}, err
	}
	stands, markers := f.trainingAsk()
	return StationGraphInput{
		Catalog: f.Catalog, Furniture: f.Furniture, Dining: f.Dining, Items: f.Items, Recipes: f.Recipes,
		Population: population, NutritionPerDay: nutrition, Orders: orders,
		Stock: stock, StackLimit: f.StackLimit, Perishable: f.Perishable,
		Crops: f.Crops, FoodReserveDays: f.FoodReserveDays, Suites: f.Suites, Seed: seed,
		TrainingStands: stands, TrainingMarkers: markers, Graveyards: max(f.Demand.Graveyards, len(f.Plan.roomsOf(PlannedGraveyard))),
		Rooms: append(GearRoomNodes(f.Plan, f.Demand), ChildRoomNodes(f.Plan, f.Child)...),
		Races: f.Races, Pasture: f.Pasture,
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
	key := fmt.Sprint(f.Dining, f.RoofDistance, f.Built, f.Pinned, f.OutdoorCurve, f.ExpectedMood, f.Mismatches, f.Derive, f.ThroneArea, f.RetireShelter, f.Demand, f.Child) + string(plan)
	input, err := f.GraphInput()
	if err != nil {
		return key + err.Error()
	}
	return key + fmt.Sprint(input.Population, input.NutritionPerDay, input.Orders, input.Stock, input.FoodReserveDays, input.Crops, input.Suites, input.Pasture)
}

// rebuildPending reports a rebuild in flight: a successor pending in the plan
// or a pinned room not yet standing (as rebuildInFlight reads the census).
func rebuildPending(f StationFacts) bool {
	if SuccessorPending(f.Plan) {
		return true
	}
	for id := range f.Pinned {
		if !f.Built[id] {
			return true
		}
	}
	return false
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
	// Sites places the power sites (PowerSiteNodes); a tiled plan stores them.
	Sites  SiteEmbedding
	Groups []GroupAdvice
	// Regroup are the regrouping search's moves (layout_regroup.go): each one
	// applied, or the single reason none was.
	Regroup []RegroupDecision
	// Tiled is the embedding tiled into a plan; nil unless TileStations ran.
	Tiled *TileResult
	// Comfort is the priced comfort tier of the bed and each worked bench
	// (#2975), advisory; nil while the model or a fact is unknown.
	Comfort []ComfortChoice
	// SeedOnly names the unknown fact that left a derive-time graph at the seed
	// rooms (StationFacts.Derive); empty when the full graph was built.
	SeedOnly string
}

// TileStations is AdviseStations with the embedding tiled into a plan
// (TileEmbedding): the fresh derive's planner (DeriveLayoutPlan). The advice at
// a review does not tile on its own: the derive (DeriveLayoutPlan) and the
// replan (ReplanLayout) tile with it.
func TileStations(f StationFacts) (StationAdvice, error) {
	out, err := AdviseStations(f)
	if err != nil {
		return StationAdvice{}, err
	}
	tiled, err := TileEmbedding(f.tileInput(out))
	if err != nil {
		return out, err
	}
	out.Tiled = &tiled
	return out, nil
}

// tileInput is what the tiler reads of the advice and the facts.
func (f StationFacts) tileInput(out StationAdvice) TileInput {
	steel, _ := f.Items.MarketValue(siteSteelUnit) // unknown stays zero: the airlocks and vents go unpriced
	groups, emb := out.Clustering.Groups, out.Embedding
	held, blocks := heldBlocks(len(groups), emb.Held)
	groups, emb.Blocks = append(slices.Clone(groups), held...), append(slices.Clone(emb.Blocks), blocks...)
	return TileInput{Groups: groups, Embedding: emb, Catalog: f.Catalog, Zones: f.Plan.Zones, Reservations: f.Plan.Reserved(), Survey: f.Survey, OutdoorCurve: f.OutdoorCurve, RoofDistance: f.RoofDistance, FullRotRateC: f.FullRotRateC, SteelValue: steel, VentAvailable: f.VentAvailable, Light: stationLightPrice(f, steel), Sites: PowerSitesOf(out.Graph.Sites, out.Sites), Graph: out.Graph, Furniture: f.Furniture, Dining: f.Dining, NextRoomID: f.Plan.NextRoomID}
}

// AdviseStations runs the station chain over the live colony. It reads the
// plan and survey and changes neither.
func AdviseStations(f StationFacts) (StationAdvice, error) {
	if f.Built == nil {
		return StationAdvice{}, unknownStationFact("construction census")
	}
	var graph StationGraph
	var seedOnly string
	input, err := f.GraphInput()
	if f.Dining.Table.Def == "" {
		err = unknownStationFact("dining furniture")
	}
	switch {
	case err == nil:
		if graph, err = BuildStationGraph(input); err != nil {
			return StationAdvice{}, err
		}
	case f.Derive && errors.Is(err, ErrStationFacts):
		// A first plan has no census, orders or stock: the graph keeps the
		// rooms that need none and the rest grows on replan. Nothing is
		// guessed for the facts left unread.
		seedOnly = err.Error()
		seed, serr := f.seedRooms()
		if serr != nil {
			return StationAdvice{}, serr
		}
		if f.RoofDistance <= 0 {
			return StationAdvice{}, unknownStationFact("roof support distance")
		}
		graph = SeedGraph(StationGraphInput{Catalog: f.Catalog, Seed: seed})
		graph.Fields = f.seedFields()
	default:
		return StationAdvice{}, err
	}
	built := BuiltRegions(f.Plan, f.Built)
	for i := range built {
		built[i].Pinned = f.Pinned[built[i].Room]
	}
	regrouped, err := Regroup(RegroupInput{
		Cluster:    ClusterInput{Graph: graph, Catalog: f.Catalog, RoofDistance: f.RoofDistance},
		Embed:      EmbedInput{Zones: f.Plan.Zones, Reservations: f.Plan.Reserved(), Survey: f.Survey, RoofDistance: f.RoofDistance, Built: built, Standing: f.standingRooms(), InFlight: rebuildPending(f), RetiredGround: f.Plan.RetiredGround},
		Price:      f.regroupPrice(),
		Mismatches: f.Mismatches,
	})
	if err != nil {
		return StationAdvice{}, err
	}
	clustering, embedIn, embedding := regrouped.Clustering, regrouped.Embed, regrouped.Embedding
	out := StationAdvice{Graph: graph, Clustering: clustering, Embedding: embedding, Regroup: regrouped.Decisions, SeedOnly: seedOnly}
	if out.Graph.Sites, out.Sites, err = f.powerSites(clustering.Groups, embedIn, embedding); err != nil {
		return StationAdvice{}, err
	}
	comfortSteel, _ := f.Items.MarketValue(siteSteelUnit)
	out.Comfort = adviseComfort(f, graph, comfortSteel)
	for _, m := range MatchGroupRooms(clustering.Groups, built) {
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
	// Pinned: the colony holds the room (TilePinned); the
	// embedding keeps it where it stands. Set by the caller, never by BuiltRegions.
	Pinned bool
}

// matchedPlanned are the planned rooms matched to groups by their planned
// role, with the native role the game gives them: a freezer is a storeroom
// and a butchery reads as a kitchen, but each is matched only to the group
// that became it (PlannedRoleOfGroup), not to the plain storage or kitchen
// groups.
var matchedPlanned = map[PlannedRole]RoomRole{
	PlannedFreezer:  RoomRoleStoreroom,
	PlannedButchery: RoomRoleKitchen,
	// A target-sized bedroom (#2939) is matched only to the suites, so a raised
	// target outgrows its own suite and not an ordinary bedroom.
	PlannedSuite: RoomRoleSuite,
}

// roomNodeRoles are the planned roles of the station-less room nodes of the
// graph (circulation, the outskirts rooms, the training rooms, the shelter and
// the throne room): a standing room of one is matched to the group of its node
// by planned role, outdoor or without a native role as it may be (#3003). The
// set is read off the node constructors, so a new node kind joins it by being
// built there.
var roomNodeRoles = func() map[PlannedRole]bool {
	nodes := append(OutskirtsRoomNodes(), TrainingRoomNodes(1, 1)...)
	nodes = append(nodes, SeedRoomNodes(SeedRooms{Shelter: true, Colonists: 1, ThroneArea: 1})...)
	nodes = append(nodes, RoomNode{Role: PlannedCirculation})
	out := map[PlannedRole]bool{}
	for _, n := range nodes {
		out[n.Role] = true
	}
	for _, role := range append(slices.Clone(gearRooms), childNodeRoles...) {
		out[role] = true
	}
	return out
}()

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
		if _, special := matchedPlanned[p]; special || roomNodeRoles[p] {
			return string(p)
		}
	}
	return string(g.Role)
}

// BuiltRegions are the plan's standing rooms that carry a native role
// (moduleRoomRoles, and the freezer and butchery of matchedPlanned) or a room
// node of the graph (roomNodeRoles), by room id. Outdoor rooms and rooms with
// no native role are left out unless they are a room node's, and so is a room
// whose successor stands: the pair is one room.
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
		if roomNodeRoles[r.Role] {
			ok, planned = true, r.Role // a room node's room: matched by planned role, outdoor or with no native role
		} else if r.Outdoor {
			continue
		}
		if !ok || !built[r.ID] || replaced[r.ID] {
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

// standingRooms are the pinned standing rooms (StationFacts.Pinned) as the
// embedding holds them (EmbedInput.Standing): every built room that is pinned,
// whatever its role or whether a group carries it, bar the attached rooms and a
// room whose successor stands (the pair is one room, as in BuiltRegions).
func (f StationFacts) standingRooms() []BuiltRegion {
	replaced := map[int32]bool{}
	for _, r := range f.Plan.AllRooms() {
		if f.Built[r.ID] && r.Replaces != 0 {
			replaced[r.Replaces] = true
		}
	}
	var out []BuiltRegion
	for _, r := range f.Plan.AllRooms() {
		if f.Built[r.ID] && f.Pinned[r.ID] && !r.attached() && !replaced[r.ID] {
			out = append(out, BuiltRegion{Room: r.ID, Planned: r.Role, Region: r.Region, Pinned: true})
		}
	}
	return out
}

// heldBlocks are the groups and blocks of the embedding's held rooms, held room
// k being group len(groups)+k: a group of its planned role with no stations, so
// the tiler tiles the standing room where it stands.
func heldBlocks(groups int, held []HeldRoom) ([]RoomGroup, []EmbeddedBlock) {
	var gs []RoomGroup
	var blocks []EmbeddedBlock
	for k, h := range held {
		id := groups + k
		gs = append(gs, RoomGroup{ID: id, Planned: h.Role, Area: int(h.Interior.Width * h.Interior.Height)})
		blocks = append(blocks, EmbeddedBlock{Group: id, Interior: h.Interior, Built: h.Room})
	}
	return gs, blocks
}
