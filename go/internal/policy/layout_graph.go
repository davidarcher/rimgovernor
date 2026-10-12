package policy

import (
	"errors"
	"fmt"
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The station graph is the site planner's input: which stations the colony
// needs (nodes, with a count each) and how strongly each pair wants to stand
// together (edges). Nothing here is a per-role constant. Counts come from the
// models that already size the colony: the population with its spare place
// (HousingSparePlaces), the nutrition the colonists eat, the production
// orders of the work ledger and the stock the resource demand holds. Which
// definition serves a need comes from the station catalog and the furniture
// rules over it. Edges are the daily item and pawn flows those same models
// imply: ingredients from storage to the station that works them, food from
// the stove to the table, the meals a sleeper walks to. Required links come
// from the environment needs the catalog states: a stove working perishable
// ingredients must be beside the cold storage that holds them. The graph is
// pure data, deterministic and never persisted; clustering it into rooms is
// the next stage.

// StationPurpose is what a node's stations are for.
type StationPurpose string

const (
	PurposeSleep       StationPurpose = "sleep"
	PurposeSleepMed    StationPurpose = "sleep_medical"
	PurposeSleepPrison StationPurpose = "sleep_prison"
	PurposeSleepBaby   StationPurpose = "sleep_baby"
	PurposeSleepAnimal StationPurpose = "sleep_animal"
	PurposeWork        StationPurpose = "work"
	PurposeStore       StationPurpose = "store"
	PurposeStoreCold   StationPurpose = "store_cold"
	PurposeDine        StationPurpose = "dine"
	// PurposeCool is the cooling station clustering adds to a group that needs
	// cold (a catalog cooler; no graph node has it).
	PurposeCool StationPurpose = "cool"
)

// Reasons a node or edge exists; the caller journals them as telemetry
// decision reasons.
const (
	ReasonBedsPopulationReserve = "beds_population_plus_reserve"
	ReasonBedsPatients          = "beds_injured"
	ReasonBedsPrisoners         = "beds_prisoners"
	ReasonBedsBabies            = "beds_babies"
	ReasonBedsAnimals           = "beds_animals"
	ReasonWorkNutrition         = "work_nutrition_and_orders"
	ReasonStoreStock            = "store_stock_demand"
	ReasonDineSeats             = "dine_seats_of_table"
	ReasonFlowIngredient        = "flow_ingredient"
	ReasonFlowProduct           = "flow_product"
	ReasonFlowMeal              = "flow_meal"
	ReasonNeedCold              = "need_cold"
)

// HousingSparePlaces is the spare place MaintainHousing's expansion phase
// keeps beyond the population: indoor capacity must exceed the colonist
// count. The station graph provides the same spare bed.
const HousingSparePlaces = 1

// StationPopulation is who needs a place. Colonists are the free people who
// sleep in a regular bed and eat at a table; Injured are the colonists who
// need a medical bed now. Prisoners, Babies and Animals sleep in their own
// kind of bed.
type StationPopulation struct {
	Colonists, Injured, Prisoners, Babies, Animals int
}

// RecipeFlow is what one run of a recipe takes in and puts out, by the
// catalog's rows. Ingredients are one alternative list per slot, the shape
// OpenBill.Slots has; Food is set when a product is a nutrition-giving food
// a human eats (the kitchen role worker's own test).
type RecipeFlow struct {
	Ingredients [][]Amount
	Products    []Amount
	Food        bool
	// Butchery is set when the recipe's special product is Butchery: the
	// ingredient's meat and leather come out (the def lists no product, the
	// race decides): meat to the cold store, leather to the plain one.
	Butchery bool
}

// StationGraphInput is every model the graph reads. Callers build the graph
// from known facts only: an unknown population or nutrition is not zero.
type StationGraphInput struct {
	Catalog   StationCatalog
	Furniture RoomFurniture
	Dining    DiningFurniture
	Items     ItemFacts
	Recipes   map[string]RecipeFlow
	// Population is the colony's people.
	Population StationPopulation
	// NutritionPerDay is what the colonists eat a day (FoodForecast's
	// consumers, summed).
	NutritionPerDay float64
	// Orders are the work ledger's wanted production orders.
	Orders []OrderSpec
	// Stock is the stock the colony holds (DerivedDemand.Needs, with the food
	// reserve); StackLimit is each item's largest stack and Perishable the
	// items that rot.
	Stock      map[Resource]int64
	StackLimit map[Resource]int64
	Perishable map[Resource]bool
}

// StationNode is a class of station and how many the colony needs.
type StationNode struct {
	ID      string
	Def     string
	Purpose StationPurpose
	Count   int
	// Needs are the catalog's environment needs of the station that apply to
	// this use of it.
	Needs  []EnvNeed
	Reason string
	// DailyWork is the work ticks a day the colony asks of the node's stations
	// together (recipe runs times the station's work per run); 0 for a node
	// that is not worked. Conflict costs are priced against it.
	DailyWork float64
	// Filth is set when the colony's orders have the node's stations butcher:
	// a recipe with the Butchery special product spawns the corpse's blood (or
	// machine bits) at the butcher's cell (Corpse.ButcherProducts calls
	// FilthMaker.TryMakeFilth; read by decompiling 1.6, the defs only name the
	// special product). Filth lowers the room's cleanliness, which the cook's
	// room reads as RoomStatDefOf.FoodPoisonChance when a meal is made
	// (CompFoodPoisonable.Notify_RecipeProduced). It is use, not station: the
	// same bench that dismantles mechanoids also works other recipes.
	Filth bool
}

// StationEdge is the wish that two nodes stand together. Weight is the
// movements a day (items or pawn trips) between them. A Required edge is a
// link the environment needs demand (cold storage beside the stove).
type StationEdge struct {
	A, B     string
	Weight   float64
	Required bool
	Reason   string
}

// RoomNode is a room the colony needs that holds no station: a node of the
// graph with a role and an area demand, nothing to furnish. Circulation is the
// first (#2911); the morgue and the waste yard (#2882) are the same kind.
type RoomNode struct {
	ID   string
	Role PlannedRole
	// Area is the interior cells the room is asked for. 0 for a room that joins
	// others: clustering sizes it from the rooms it joins (Joins).
	Area int
	// Width is the fixed short side of the room in cells, 0 for the squarest
	// block: a corridor is a strip.
	Width int32
	// Joins marks a room that exists to connect the rooms that cannot be walked
	// through (addCirculation).
	Joins  bool
	Reason string
}

// StationGraph is the planner's input.
type StationGraph struct {
	Nodes []StationNode
	// Rooms are the station-less rooms.
	Rooms []RoomNode
	Edges []StationEdge
}

// PlannedCirculation is the corridor that joins the rooms of a large colony:
// a room with no station, walked by everyone.
const PlannedCirculation PlannedRole = "circulation"

// CirculationWidth is a circulation corridor's width in cells. One: in peace
// pawns neither block nor slow each other (PawnUtility.PawnBlockedBy is false
// unless a hostile is near or the job collides), the pather has no wall-hugging
// cost and door costs do not depend on width, so width buys nothing before an
// attack; the wider lanes that give bypass under hostile pressure are the
// defense planner's (#2886). Read from game code, recorded on #2911.
const CirculationWidth int32 = 1

// ReasonCirculation is why the graph holds a corridor.
const ReasonCirculation = "circulation_joins_private_rooms"

// Node is the node with the id.
func (g StationGraph) Node(id string) (StationNode, bool) {
	for _, n := range g.Nodes {
		if n.ID == id {
			return n, true
		}
	}
	return StationNode{}, false
}

// Weight is the summed edge weight between two nodes, 0 when none.
func (g StationGraph) Weight(a, b string) float64 {
	a, b = orderedPair(a, b)
	total := 0.0
	for _, e := range g.Edges {
		if e.A == a && e.B == b {
			total += e.Weight
		}
	}
	return total
}

// Requires reports a required link between two nodes.
func (g StationGraph) Requires(a, b string) bool {
	a, b = orderedPair(a, b)
	for _, e := range g.Edges {
		if e.A == a && e.B == b && e.Required {
			return true
		}
	}
	return false
}

// ErrStationGraph marks a need the catalog has no station for.
var ErrStationGraph = errors.New("station graph")

func orderedPair(a, b string) (string, string) {
	if b < a {
		return b, a
	}
	return a, b
}

func nodeID(def string, purpose StationPurpose) string { return def + "/" + string(purpose) }

// ceilDiv is the stations that serve n at per each.
func ceilDiv(n, per int) int {
	if n <= 0 || per <= 0 {
		return 0
	}
	return (n + per - 1) / per
}

// BuildStationGraph derives the graph from the models in in.
func BuildStationGraph(in StationGraphInput) (StationGraph, error) {
	b := &graphBuilder{in: in, nodes: map[string]*StationNode{}, edges: map[[2]string]*StationEdge{}}
	if err := b.sleepers(); err != nil {
		return StationGraph{}, err
	}
	runs, err := b.workLoads()
	if err != nil {
		return StationGraph{}, err
	}
	meals := b.mealsPerDay()
	if err := b.dining(meals); err != nil {
		return StationGraph{}, err
	}
	if err := b.storage(); err != nil {
		return StationGraph{}, err
	}
	b.flows(runs)
	b.sleepEat(meals)
	g := b.graph()
	for _, n := range g.Nodes {
		switch n.Purpose {
		case PurposeSleep, PurposeSleepMed, PurposeSleepBaby:
			// Somebody sleeps in a room nobody may walk through (RoomCrossing.Beds):
			// the colony may need a way to reach it that crosses no other room.
			g.Rooms = append(g.Rooms, RoomNode{ID: string(PlannedCirculation), Role: PlannedCirculation, Width: CirculationWidth, Joins: true, Reason: ReasonCirculation})
			return g, nil
		}
	}
	return g, nil
}

type graphBuilder struct {
	in    StationGraphInput
	nodes map[string]*StationNode
	edges map[[2]string]*StationEdge
}

func (b *graphBuilder) station(def string) (Station, error) {
	s, ok := b.in.Catalog.Station(def)
	if !ok {
		return Station{}, fmt.Errorf("%w: %q is no station in the catalog", ErrStationGraph, def)
	}
	return s, nil
}

func (b *graphBuilder) addNode(def string, purpose StationPurpose, count int, needs []EnvNeed, reason string) string {
	id := nodeID(def, purpose)
	b.nodes[id] = &StationNode{ID: id, Def: def, Purpose: purpose, Count: count, Needs: needs, Reason: reason}
	return id
}

func (b *graphBuilder) addEdge(a, c string, weight float64, required bool, reason string) {
	if a == c || weight <= 0 && !required {
		return
	}
	a, c = orderedPair(a, c)
	key := [2]string{a, c}
	e := b.edges[key]
	if e == nil {
		e = &StationEdge{A: a, B: c, Reason: reason}
		b.edges[key] = e
	}
	e.Weight += weight
	e.Required = e.Required || required
	if required {
		e.Reason = reason
	}
}

func (b *graphBuilder) graph() StationGraph {
	var g StationGraph
	for _, n := range b.nodes {
		g.Nodes = append(g.Nodes, *n)
	}
	sort.Slice(g.Nodes, func(i, j int) bool { return g.Nodes[i].ID < g.Nodes[j].ID })
	for _, e := range b.edges {
		g.Edges = append(g.Edges, *e)
	}
	sort.Slice(g.Edges, func(i, j int) bool {
		if g.Edges[i].A != g.Edges[j].A {
			return g.Edges[i].A < g.Edges[j].A
		}
		return g.Edges[i].B < g.Edges[j].B
	})
	return g
}

// sleepers adds one bed node per kind of sleeper. A bed serves as many
// sleepers as the catalog gives its sleeping slots.
func (b *graphBuilder) sleepers() error {
	p := b.in.Population
	f := b.in.Furniture
	nurseries := b.in.Catalog.Contributors(RoomRoleNursery)
	hospital := f.HospitalBeds()
	for _, k := range []struct {
		purpose StationPurpose
		people  int
		def     string
		reason  string
	}{
		{PurposeSleep, p.Colonists + HousingSparePlaces, f.PrimaryBed(), ReasonBedsPopulationReserve},
		{PurposeSleepMed, p.Injured, first(hospital), ReasonBedsPatients},
		{PurposeSleepPrison, p.Prisoners, f.PrimaryBed(), ReasonBedsPrisoners},
		{PurposeSleepBaby, p.Babies, first(nurseries), ReasonBedsBabies},
		{PurposeSleepAnimal, p.Animals, firstNonEmpty(f.AnimalBed, f.AnimalSpot), ReasonBedsAnimals},
	} {
		if k.people <= 0 {
			continue
		}
		if k.def == "" {
			return fmt.Errorf("%w: no bed def for %s sleepers", ErrStationGraph, k.purpose)
		}
		s, err := b.station(k.def)
		if err != nil {
			return err
		}
		b.addNode(k.def, k.purpose, ceilDiv(k.people, max(s.Capacity.Slots, 1)), nil, k.reason)
	}
	return nil
}

func first(list []string) string {
	if len(list) == 0 {
		return ""
	}
	return list[0]
}

func firstNonEmpty(list ...string) string {
	for _, s := range list {
		if s != "" {
			return s
		}
	}
	return ""
}

// recipeLoad is the runs of one recipe a day at one station.
type recipeLoad struct {
	station string
	recipe  string
	runs    float64
	work    float64
}

// hosts reports the station's work for a recipe (0 when it does not host it).
func hostWork(s Station, recipe string) float64 {
	for _, r := range s.Capacity.Recipes {
		if r.Recipe == recipe {
			return r.Work
		}
	}
	return 0
}

// hostOf is the station that works a recipe: the preferred def when it hosts
// the recipe, else the furniture rule's bench for the role, else the first
// hosting station by name.
func (b *graphBuilder) hostOf(recipe string, preferred ...string) (string, float64) {
	for _, def := range preferred {
		if s, ok := b.in.Catalog.Station(def); ok && s.Capacity.Kind == CapacityWorkers {
			if w := hostWork(s, recipe); w > 0 {
				return def, w
			}
		}
	}
	for _, def := range b.in.Catalog.Defs() {
		s := b.in.Catalog.Stations[def]
		if s.Capacity.Kind != CapacityWorkers {
			continue
		}
		if w := hostWork(s, recipe); w > 0 {
			return def, w
		}
	}
	return "", 0
}

func (r RecipeFlow) productCount() float64 {
	n := 0.0
	for _, p := range r.Products {
		n += float64(p.Count)
	}
	return n
}

func (r RecipeFlow) nutrition(items ItemFacts) float64 {
	n := 0.0
	for _, p := range r.Products {
		n += float64(p.Count) * items.Nutrition[p.Resource]
	}
	return n
}

// workLoads adds a node per work station the colony needs and returns the
// daily runs of every recipe. The nutrition the colonists eat is met by the
// food recipe that takes the least work per nutrition at a kitchen station;
// the ledger's orders add their runs (the work owed, cleared within the
// day). Where the two ask for the same recipe the larger stands, as
// BuildResourceDemand merges overlapping targets.
func (b *graphBuilder) workLoads() ([]recipeLoad, error) {
	loads := map[[2]string]*recipeLoad{}
	add := func(station, recipe string, runs, work float64, merge func(a, b float64) float64) {
		key := [2]string{station, recipe}
		l := loads[key]
		if l == nil {
			l = &recipeLoad{station: station, recipe: recipe, work: work}
			loads[key] = l
		}
		l.runs = merge(l.runs, runs)
	}
	nutrition := map[[2]string]float64{}
	if b.in.NutritionPerDay > 0 {
		recipe, station, work, perRun := b.bestFoodRecipe()
		if recipe == "" {
			return nil, fmt.Errorf("%w: no kitchen station works a food recipe", ErrStationGraph)
		}
		runs := b.in.NutritionPerDay / perRun
		nutrition[[2]string{station, recipe}] = runs
		add(station, recipe, runs, work, math.Max)
	}
	orders := map[[2]string]float64{}
	orderWork := map[[2]string]float64{}
	for _, o := range b.in.Orders {
		if o.Target <= 0 {
			continue
		}
		station, work := b.hostOf(o.Recipe, o.BenchKind)
		if station == "" {
			continue
		}
		runs := float64(o.Target)
		if per := b.in.Recipes[o.Recipe].productCount(); per > 0 {
			runs /= per
		}
		key := [2]string{station, o.Recipe}
		orders[key] += runs
		orderWork[key] = work
	}
	for key, runs := range orders {
		add(key[0], key[1], runs, orderWork[key], math.Max)
	}
	perStation := map[string]float64{}
	var out []recipeLoad
	for _, l := range loads {
		perStation[l.station] += l.runs * l.work
		out = append(out, *l)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].station != out[j].station {
			return out[i].station < out[j].station
		}
		return out[i].recipe < out[j].recipe
	})
	stations := make([]string, 0, len(perStation))
	for def := range perStation {
		stations = append(stations, def)
	}
	sort.Strings(stations)
	for _, def := range stations {
		s, err := b.station(def)
		if err != nil {
			return nil, err
		}
		count := max(1, int(math.Ceil(perStation[def]/float64(domain.TicksPerDay*max(s.Capacity.Slots, 1)))))
		node := b.nodes[b.addNode(def, PurposeWork, count, s.Needs, ReasonWorkNutrition)]
		node.DailyWork = perStation[def]
		node.Filth = slices.ContainsFunc(out, func(l recipeLoad) bool { return l.station == def && b.in.Recipes[l.recipe].Butchery })
	}
	return out, nil
}

// bestFoodRecipe is the food recipe at a kitchen station that takes the least
// work for a unit of nutrition, with the station that works it.
func (b *graphBuilder) bestFoodRecipe() (recipe, station string, work, perRun float64) {
	best := math.Inf(1)
	kitchens := b.in.Catalog.Contributors(RoomRoleKitchen)
	if bench := b.in.Furniture.Bench[RoomRoleKitchen]; bench != "" {
		kitchens = append([]string{bench}, kitchens...)
	}
	for _, def := range kitchens {
		s, ok := b.in.Catalog.Station(def)
		if !ok || s.Capacity.Kind != CapacityWorkers {
			continue
		}
		for _, r := range s.Capacity.Recipes {
			flow, ok := b.in.Recipes[r.Recipe]
			nut := flow.nutrition(b.in.Items)
			if !ok || !flow.Food || nut <= 0 || r.Work <= 0 {
				continue
			}
			cost := r.Work / nut
			if cost < best || cost == best && r.Recipe < recipe {
				best, recipe, station, work, perRun = cost, r.Recipe, def, r.Work, nut
			}
		}
		if recipe != "" {
			break
		}
	}
	return recipe, station, work, perRun
}

// mealsPerDay is the food items the colony eats a day: the runs of the
// nutrition recipe times what a run makes.
func (b *graphBuilder) mealsPerDay() float64 {
	if b.in.NutritionPerDay <= 0 {
		return 0
	}
	recipe, _, _, perRun := b.bestFoodRecipe()
	if recipe == "" || perRun <= 0 {
		return 0
	}
	return b.in.NutritionPerDay / perRun * b.in.Recipes[recipe].productCount()
}

// dining adds the tables the colonists eat at. A table seats at its edge
// cells, as the dining template puts a chair at every side.
func (b *graphBuilder) dining(meals float64) error {
	eaters := b.in.Population.Colonists
	if eaters <= 0 || meals <= 0 {
		return nil
	}
	table := b.in.Dining.Table
	if table.Def == "" {
		return fmt.Errorf("%w: no dining table def", ErrStationGraph)
	}
	s, err := b.station(table.Def)
	if err != nil {
		return err
	}
	seats := int(2 * (table.Size.X + table.Size.Z))
	b.addNode(table.Def, PurposeDine, ceilDiv(eaters, seats), s.Needs, ReasonDineSeats)
	return nil
}

// storageStation is the shelf a stock is stored on: the catalog's item store
// of the storeroom role holding the most stacks per cell, wanting cold when
// cold is asked for.
func (b *graphBuilder) storageStation(cold bool) (string, Station, bool) {
	best, bestDensity := "", 0.0
	var bestStation Station
	for _, def := range b.in.Catalog.Contributors(RoomRoleStoreroom) {
		s := b.in.Catalog.Stations[def]
		if s.Capacity.Kind != CapacityItems || s.Capacity.Slots <= 0 {
			continue
		}
		wantsCold := false
		for _, n := range s.Needs {
			wantsCold = wantsCold || n.Kind == NeedCold
		}
		if cold && !wantsCold {
			continue
		}
		density := float64(s.Capacity.Slots) / float64(max(s.Shape.Size.X*s.Shape.Size.Z, 1))
		if density > bestDensity {
			best, bestDensity, bestStation = def, density, s
		}
	}
	return best, bestStation, best != ""
}

// storage adds the shelves the stock needs: perishable stock to a store that
// wants cold, the rest to a plain one.
func (b *graphBuilder) storage() error {
	stacks := map[bool]int{}
	resources := make([]Resource, 0, len(b.in.Stock))
	for r := range b.in.Stock {
		resources = append(resources, r)
	}
	slices.Sort(resources)
	for _, r := range resources {
		n := b.in.Stock[r]
		if n <= 0 {
			continue
		}
		limit := b.in.StackLimit[r]
		if limit <= 0 {
			return fmt.Errorf("%w: no stack limit for %s", ErrStationGraph, r)
		}
		stacks[b.in.Perishable[r]] += int((n + limit - 1) / limit)
	}
	if stacks[true] > 0 {
		if def, s, ok := b.storageStation(true); ok {
			b.addNode(def, PurposeStoreCold, ceilDiv(stacks[true], s.Capacity.Slots), coldNeeds(s), ReasonStoreStock)
		} else {
			stacks[false] += stacks[true]
		}
	}
	if stacks[false] > 0 {
		def, s, ok := b.storageStation(false)
		if !ok {
			return fmt.Errorf("%w: no item store in the catalog", ErrStationGraph)
		}
		b.addNode(def, PurposeStore, ceilDiv(stacks[false], s.Capacity.Slots), withoutCold(s.Needs), ReasonStoreStock)
	}
	return nil
}

func coldNeeds(s Station) []EnvNeed {
	var out []EnvNeed
	for _, n := range s.Needs {
		if n.Kind == NeedCold {
			out = append(out, n)
		}
	}
	return out
}

func withoutCold(needs []EnvNeed) []EnvNeed {
	var out []EnvNeed
	for _, n := range needs {
		if n.Kind != NeedCold {
			out = append(out, n)
		}
	}
	return out
}

// storeFor is the node an item is held in.
func (b *graphBuilder) storeFor(r Resource) (string, bool) { return b.storeOf(b.in.Perishable[r]) }

// storeOf is the store that holds things that rot (the cold one, else the
// plain one) or things that keep.
func (b *graphBuilder) storeOf(perishable bool) (string, bool) {
	purpose := PurposeStore
	if perishable {
		purpose = PurposeStoreCold
	}
	for _, n := range b.nodes {
		if n.Purpose == purpose {
			return n.ID, true
		}
	}
	if purpose == PurposeStoreCold {
		for _, n := range b.nodes {
			if n.Purpose == PurposeStore {
				return n.ID, true
			}
		}
	}
	return "", false
}

// flows adds the item edges of every recipe load: ingredients from the store
// to the station, the product from the station to the table (food) or the
// store. A station working a perishable ingredient requires the cold store.
func (b *graphBuilder) flows(loads []recipeLoad) {
	dine := ""
	for _, n := range b.nodes {
		if n.Purpose == PurposeDine {
			dine = n.ID
		}
	}
	for _, l := range loads {
		work := nodeID(l.station, PurposeWork)
		flow := b.in.Recipes[l.recipe]
		for _, pick := range recipePicks(flow) {
			store, ok := b.storeFor(pick.Resource)
			if !ok {
				continue
			}
			b.addEdge(store, work, l.runs*float64(pick.Count), false, ReasonFlowIngredient)
			if b.in.Perishable[pick.Resource] {
				if n := b.nodes[store]; n.Purpose == PurposeStoreCold {
					b.addEdge(store, work, 0, true, ReasonNeedCold)
				}
			}
			if flow.Butchery {
				// The yield of a butchered ingredient is meat, which rots, and
				// leather, which keeps: one haul of each per ingredient.
				for _, rots := range []bool{true, false} {
					if out, ok := b.storeOf(rots); ok {
						b.addEdge(work, out, l.runs*float64(pick.Count), false, ReasonFlowProduct)
					}
				}
			}
		}
		for _, p := range flow.Products {
			amount := l.runs * float64(p.Count)
			if flow.Food && dine != "" {
				b.addEdge(work, dine, amount, false, ReasonFlowMeal)
			} else if store, ok := b.storeFor(p.Resource); ok {
				b.addEdge(work, store, amount, false, ReasonFlowProduct)
			}
		}
	}
}

// recipePicks are the ingredients a run takes: each slot's cheapest member
// (lowest count, then name), as OpenBill.picks chooses.
func recipePicks(f RecipeFlow) []Amount {
	var out []Amount
	for _, slot := range f.Ingredients {
		var pick Amount
		for _, a := range slot {
			if pick.Resource == "" || a.Count < pick.Count || a.Count == pick.Count && a.Resource < pick.Resource {
				pick = a
			}
		}
		if pick.Resource != "" && pick.Count > 0 {
			out = append(out, pick)
		}
	}
	return out
}

// sleepEat adds the sleep and eat cycle: each meal a sleeper eats is a trip
// between bed and table, shared by regular and medical beds by the people in
// them.
func (b *graphBuilder) sleepEat(meals float64) {
	p := b.in.Population
	if meals <= 0 || p.Colonists <= 0 {
		return
	}
	var dine string
	for _, n := range b.nodes {
		if n.Purpose == PurposeDine {
			dine = n.ID
		}
	}
	if dine == "" {
		return
	}
	injured := float64(min(p.Injured, p.Colonists))
	colonists := float64(p.Colonists)
	for _, n := range b.nodes {
		switch n.Purpose {
		case PurposeSleep:
			b.addEdge(n.ID, dine, meals*(colonists-injured)/colonists, false, ReasonFlowMeal)
		case PurposeSleepMed:
			b.addEdge(n.ID, dine, meals*injured/colonists, false, ReasonFlowMeal)
		}
	}
}
