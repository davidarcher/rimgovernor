package policy

import (
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainTrade closes the colony's silver gap with goods the bench crafts:
// while SilverGap is positive it ranks the craftable sale goods and declares
// the winners to the work ledger (OrderDeclarer) as finite batches pinned to
// the best capable worker; the trade planner then sells what they make as
// surplus (ExportSaleSurplus, SaleGearSurplus). Ownership is split by floor:
// MaintainResource owns a bill whose product has a floor (steel, stone
// blocks, components, meals); MaintainTrade owns the sale goods with none, and
// never acquires anything: it spends only UsableIngredients. MaintainArt keeps
// room decoration only.
//
// The gap may stay open if the benches are slow; the other silver responses
// (organ harvests, surplus sales) run beside this one, and a good no
// reachable trader buys is never ordered.
const MaintainTrade ConcernID = "MaintainTrade"

// tradePriority ranks MaintainTrade with the other upkeep projects.
const tradePriority = 3

// ExportRunwayHorizonDays is how far ahead ExportGuard keeps the colony's own
// use of an ingredient whole: an export may not draw a resource below its
// reserve plus this many days of its observed use.
const ExportRunwayHorizonDays = 15.0

// ExportSellRatio is the planning prior for what a trader pays against the
// item's market value (the game's base sell factor, before negotiation and
// the trader's own margin). The sale itself is priced from the live sheet.
const ExportSellRatio = 0.5

// ExportRequest is what one Round's export declaration reads.
type ExportRequest struct {
	Benches  []GearBench
	Profiles domain.Fact[[]PawnProfile]
	Items    ItemFacts
	// Gap is the silver gap (SilverGap); Demand gives the floors and needs
	// that put a product out of reach (DerivedDemand.Needs) and Floors the
	// economic trade floors.
	Gap    domain.Fact[float64]
	Demand DerivedDemand
	Floors map[string]int64
	// Runways and Supply give the ingredient stock a bill may spend
	// (UsableIngredients) and the 15-day line the guard holds.
	Runways []ResourceRunway
	Supply  []Stock
	// Stock is the colony stock by definition, Surplus and SurplusKnown the
	// gear above demand (ApparelSurplus, SpareWeaponCounts) and Packed the
	// packed sculptures held: the goods already in flight.
	Stock        map[Resource]int64
	Surplus      GearSurplus
	SurplusKnown bool
	Packed       domain.Fact[int64]
	// Sources are the priced ways to acquire an ingredient by hand (mine,
	// chop, harvest, hunt), per ingredient; none leaves the produce prior.
	Sources map[Resource][]SupplyCandidate
	// Buys says per product whether a reachable trader buys it; CashCap is the
	// low end of the silver the best reachable trader carries.
	Buys    map[Resource]domain.Fact[bool]
	CashCap domain.Fact[int64]
	// WorkFor is the work of one piece of a product made from a stuff, for the
	// recipes that name none (the product's WorkToMake); nil reads none.
	WorkFor func(product, stuff Resource) (float64, bool)
}

// ExportPlan is a Round's export declaration with the ranking behind it.
type ExportPlan struct {
	Declared Declared
	// Products are the products MaintainTrade can rank (every craftable sale
	// good its benches make, floors and the non-sale kinds left out), the
	// goods a held stack joins the sale surplus of while the gap is open.
	Products []Resource
	// Ranked is the candidates by descending score, for the reader.
	Ranked []ExportCandidate
	// InFlight is the sale value of the goods already held, packed or on
	// standing bills that was netted off the gap. Candidates is the best
	// ExportViewCandidates scored pairs, ordered or not, for the reader.
	InFlight   float64
	Candidates []ExportCandidate
}

// ExportCandidate is one ranked sale good: a recipe at a bench kind made from
// one stuff or filter by a worker, with the silver it nets per work tick.
type ExportCandidate struct {
	Recipe, BenchKind string
	Product, Stuff    Resource
	Worker            PawnID
	// Value is the expected market value of one piece, Net its sale value
	// beyond what its ingredients fetch raw, Ticks the crafting ticks plus the
	// ingredients' acquisition ticks and Score = Net / Ticks.
	Value, Net, Ticks, Score float64
	Filter                   []string
	// Draw is one piece's ingredient units.
	Draw []Amount
}

// ExportRunwayGuard decides how much of a draw on the colony's stock an
// export may take. Admit grants up to want pieces of unit, counting every
// draw it has granted before (the orders declared this Round, placed or not)
// and the standing bills'. It is replaceable by a finite-versus-renewable
// classification of the resource without changing a caller.
type ExportRunwayGuard interface {
	Admit(unit []Amount, want int64) (granted int64, known bool)
}

// horizonGuard is the level-1 guard: every resource is treated as finite, so
// an export keeps the reserve and ExportRunwayHorizonDays of the colony's own
// observed use.
type horizonGuard struct {
	runways   []ResourceRunway
	have      map[Resource]int64
	committed map[Resource]int64
}

// NewExportGuard is the level-1 ExportRunwayGuard over the usable census,
// holding the ExportRunwayHorizonDays line of every ingredient the runways
// forecast; draws holds the units already committed to standing and declared
// bills.
func NewExportGuard(runways []ResourceRunway, supply []Stock, draws map[Resource]int64) ExportRunwayGuard {
	g := &horizonGuard{runways: runways, have: map[Resource]int64{}, committed: map[Resource]int64{}}
	for _, s := range supply {
		if have, ok := s.Available.Value(); ok && have >= 0 {
			g.have[s.Resource] = have
		}
	}
	for r, n := range draws {
		g.committed[r] += n
	}
	return g
}

// Admit is ExportRunwayGuard.Admit. known is false while an ingredient's
// census or the use rate of one its runway forecasts is unread.
func (g *horizonGuard) Admit(unit []Amount, want int64) (int64, bool) {
	granted := want
	for _, a := range unit {
		if a.Count <= 0 {
			continue
		}
		have, ok := g.have[a.Resource]
		if !ok {
			return 0, false
		}
		line := int64(0)
		for _, row := range g.runways {
			if row.Resource != a.Resource {
				continue
			}
			if stock, known := row.Stock.Value(); known && stock >= 0 {
				have = min(have, stock)
			}
			rate, known := row.ConsumptionPerDay.Value()
			if !known || !finite(rate) || row.Reserve < 0 {
				return 0, false
			}
			line = row.Reserve + int64(math.Ceil(max(0, rate)*ExportRunwayHorizonDays))
		}
		free := have - line - g.committed[a.Resource]
		granted = min(granted, max(0, free)/a.Count)
	}
	if granted <= 0 {
		return 0, true
	}
	for _, a := range unit {
		g.committed[a.Resource] += a.Count * granted
	}
	return granted, true
}

// exportQualityCentre is the planning prior for the quality a worker of a
// skill level turns out, as an index into the quality scale (Poor at a
// beginner, Normal at skill 8, Good at 16): the game's quality roll centres
// near it. Judgment, not a game number.
func exportQualityCentre(skill int) float64 {
	return min(4, 1+float64(max(0, skill))/8)
}

// exportQualityFactor is the MarketValue factor of the expected quality: the
// quality price factors interpolated at the skill's centre.
func exportQualityFactor(skill int) float64 {
	c := exportQualityCentre(skill)
	lo := int(math.Floor(c))
	hi := min(lo+1, len(marketQualityFactor)-1)
	frac := c - float64(lo)
	return marketQualityFactor[lo]*(1-frac) + marketQualityFactor[hi]*frac
}

// exportProduct reports whether a recipe's product is one MaintainTrade may
// rank. A product with a floor or a need, food, stone blocks, components and
// the currency are others' (MaintainResource's), never MaintainTrade's.
func exportProduct(r ExportRequest, product Resource) bool {
	i := r.Items
	switch {
	case product == "" || product == i.Currency || product == ComponentResource || i.IsStoneBlocks(product):
		return false
	case r.Demand.Needs[product] > 0 || r.Floors[string(product)] > 0 || i.Nutrition[product] > 0:
		return false
	}
	return true
}

// exportGear reports a product the surplus gear sale sells (apparel and
// weapons); gear and packed art do not sell from the generic stock surplus.
func exportGear(i ItemFacts, product Resource) bool {
	return slices.Contains(i.Categories[product], "Apparel") || slices.Contains(i.Categories[product], "Weapons")
}

func exportQualityBearing(i ItemFacts, recipe GearRecipe, product Resource) bool {
	return recipe.Role == domain.RoleSculpture || exportGear(i, product)
}

// ExportSaleSurplus adds the held stock of the export products to the trade
// need's surplus, retaining none of it, while the colony is short of silver
// (the OrganSaleSurplus rule). Gear and art sell by their own paths, so
// products holds only the generic goods.
func ExportSaleSurplus(items ItemFacts, need domain.Fact[TradeNeed], resources domain.Fact[[]Amount], colonists domain.Fact[int64], products []Resource) domain.Fact[TradeNeed] {
	n, nk := need.Value()
	rows, rk := resources.Value()
	if !nk || !rk || len(products) == 0 || !positive(SilverShort(items, need, SilverStock(items, resources), colonists)) {
		return need
	}
	stock := map[Resource]int64{}
	for _, row := range rows {
		stock[row.Resource] += row.Count
	}
	ordered := append([]Resource(nil), products...)
	sort.Slice(ordered, func(i, j int) bool { return ordered[i] < ordered[j] })
	for _, product := range slices.Compact(ordered) {
		if count := stock[product]; count > 0 && !slices.ContainsFunc(n.Surplus, func(a Amount) bool { return a.Resource == product }) {
			n.Surplus = append(n.Surplus, Amount{Resource: product, Count: count})
			n.retain(product, 0)
		}
	}
	return domain.Known(n)
}

// ExportSaleProducts is the products of an ExportPlan the generic stock
// surplus sells: its products less gear (SaleGearSurplus) and sculptures
// (the packed art sale).
func ExportSaleProducts(items ItemFacts, products []Resource) []Resource {
	var out []Resource
	for _, p := range products {
		if !exportGear(items, p) && !isSculptureProduct(items, p) {
			out = append(out, p)
		}
	}
	return out
}

func isSculptureProduct(i ItemFacts, product Resource) bool {
	return slices.ContainsFunc(i.Sculptures, func(s Sculpture) bool { return Resource(s.Def) == product })
}

// exportRecipe is a recipe of a usable bench that MaintainTrade may rank.
type exportRecipe struct {
	Bench     GearBench
	Recipe    GearRecipe
	Product   Resource
	Slots     [][]Amount
	Work      float64
	Required  []WorkRequirement
	Quality   bool
	Sculpture bool
}

// exportRecipes are the rankable recipes on the usable benches. unread is set
// when a recipe that would otherwise be ranked has an unread fact.
func exportRecipes(r ExportRequest) (out []exportRecipe, unread bool) {
	for _, bench := range r.Benches {
		usable, uk := bench.Usable.Value()
		recipes, rk := bench.Recipes.Value()
		if !uk || !rk {
			unread = true
			continue
		}
		if !usable {
			continue
		}
		for _, recipe := range recipes {
			if recipe.Kind != LedgerProduction || len(recipe.Products) != 1 || !exportProduct(r, recipe.Products[0]) {
				continue
			}
			available, ak := recipe.Available.Value()
			on, ok := recipe.AvailableOn.Value()
			if !ak || !ok {
				unread = true
				continue
			}
			if !available || !on {
				continue
			}
			slots, sk := recipe.Ingredients.Value()
			// A recipe whose work is the product's WorkToMake carries none here
			// (workOf prices it per stuff); an unpriced one is simply not ranked.
			work, _ := recipe.WorkAmount.Value()
			required, qk := recipe.RequiredWork.Value()
			if !sk || !qk || !finite(work) {
				unread = true
				continue
			}
			product := recipe.Products[0]
			out = append(out, exportRecipe{Bench: bench, Recipe: recipe, Product: product, Slots: slots, Work: work, Required: required,
				Quality: exportQualityBearing(r.Items, recipe, product), Sculpture: recipe.Role == domain.RoleSculpture})
		}
	}
	return out, unread
}

// exportPicks is the ingredient units of one piece under a filter: per slot
// the cheapest member the filter admits (the loadout stuff first).
func exportPicks(slots [][]Amount, filter []Resource, stuff Resource) ([]Amount, bool) {
	var picks []Amount
	for _, slot := range slots {
		var best Amount
		for _, a := range slot {
			if !slices.Contains(filter, a.Resource) || a.Count <= 0 {
				continue
			}
			switch {
			case best.Resource == "":
				best = a
			case stuff != "" && a.Resource == stuff:
				best = a
			case stuff != "" && best.Resource == stuff:
			case a.Count < best.Count || a.Count == best.Count && a.Resource < best.Resource:
				best = a
			}
		}
		if best.Resource == "" {
			return nil, false
		}
		picks = append(picks, best)
	}
	return picks, true
}

// exportVariants are the ingredient choices of a recipe: one per stuff a
// stuff-made product accepts, else the single cheapest filter.
type exportVariant struct {
	Stuff  Resource
	Filter []Resource
	Picks  []Amount
}

func exportVariants(i ItemFacts, e exportRecipe) []exportVariant {
	stuffs := []Resource{""}
	if len(i.AcceptedStuff[e.Product]) > 0 {
		stuffs = i.StuffsFor(e.Product)
	}
	var out []exportVariant
	for _, stuff := range stuffs {
		filter, ok := gearFilter(e.Slots, stuff, i.StuffCategories)
		if !ok {
			continue
		}
		picks, ok := exportPicks(e.Slots, filter, stuff)
		if !ok {
			continue
		}
		out = append(out, exportVariant{Stuff: stuff, Filter: filter, Picks: picks})
	}
	return out
}

// AcquisitionTicksPerUnit is the pawn ticks one unit of a resource costs by
// the cheapest of its hand sources: the source's labor plus the haul
// (distance by the trip and its return, plus a tick a trip) over what it
// yields. false when no source prices it.
func AcquisitionTicksPerUnit(sources []SupplyCandidate) (float64, bool) {
	best, found := 0.0, false
	for _, s := range sources {
		labor, lk := s.UpfrontCost.LaborTicks.Value()
		path, pk := s.PathDistance.Value()
		if len(s.Yields) == 0 || !lk || !pk {
			continue
		}
		units, uk := s.Yields[0].StockCap.Value()
		if !uk || units <= 0 {
			continue
		}
		trips := 1.0
		if s.NeedsHaul && s.UnitsPerTrip > 0 {
			trips = math.Ceil(float64(units) / float64(s.UnitsPerTrip))
		}
		cost := (labor + path*(1+2*trips) + trips) / float64(units)
		if !found || cost < best {
			best, found = cost, true
		}
	}
	return best, found
}

func (r ExportRequest) acquisition(resource Resource) float64 {
	if ticks, ok := AcquisitionTicksPerUnit(r.Sources[resource]); ok {
		return ticks
	}
	return AcquisitionLaborPerUnit[CandidateProduce]
}

// exportWorker is a pawn able to do a recipe's work, with the skill that sets
// the quality it makes.
type exportWorker struct {
	ID    PawnID
	Skill int
}

func exportWorkers(profiles []PawnProfile, required []WorkRequirement) []exportWorker {
	var out []exportWorker
	for _, p := range profiles {
		if p.Child || !foodID(string(p.ID)) {
			continue
		}
		capable, skill, skilled := true, 0, false
		for _, q := range required {
			if !p.Capable(q.Work, q.Minimum) {
				capable = false
				break
			}
			if q.Skill != "" && !skilled {
				skill, skilled = p.Skill(q.Skill).Level, true
			}
		}
		if capable {
			out = append(out, exportWorker{ID: p.ID, Skill: skill})
		}
	}
	return out
}

// score prices one piece of a variant made by a worker of the given skill.
// ok is false when the good does not beat selling its ingredients raw or a
// price is unread.
func (r ExportRequest) score(e exportRecipe, v exportVariant, skill int) (c ExportCandidate, ok bool) {
	items := r.Items
	work, ok := r.workOf(e, v.Stuff)
	if !ok {
		return c, false
	}
	raw, ticks := 0.0, work/benchSpeed(e.Bench)
	for _, a := range v.Picks {
		price, priced := items.Market[a.Resource]
		if !priced {
			return c, false
		}
		raw += float64(a.Count) * price
		ticks += float64(a.Count) * r.acquisition(a.Resource)
	}
	value := 0.0
	if v.Stuff != "" {
		value = raw + work*valuePerWorkTick
	} else if base, priced := items.Market[e.Product]; priced {
		value = base
	} else {
		return c, false
	}
	if e.Quality {
		value *= exportQualityFactor(skill)
	}
	net := ExportSellRatio * (value - raw)
	if !(net > 0) || !(ticks > 0) {
		return c, false
	}
	filter := make([]string, len(v.Filter))
	for i, f := range v.Filter {
		filter[i] = string(f)
	}
	return ExportCandidate{Recipe: e.Recipe.Definition, BenchKind: e.Bench.Def, Product: e.Product, Stuff: v.Stuff, Value: value, Net: net, Ticks: ticks, Score: net / ticks, Filter: filter, Draw: v.Picks}, true
}

// workOf is the work one piece takes: the recipe's own amount, else the
// sculpture's catalog work, else the product's WorkToMake under the stuff
// (ExportRequest.WorkFor). false when none is read.
func (r ExportRequest) workOf(e exportRecipe, stuff Resource) (float64, bool) {
	if e.Work > 0 {
		return e.Work, true
	}
	for _, s := range r.Items.Sculptures {
		if Resource(s.Def) == e.Product && s.Work > 0 && finite(s.Work) {
			return s.Work, true
		}
	}
	if r.WorkFor != nil {
		if work, ok := r.WorkFor(e.Product, stuff); ok && work > 0 && finite(work) {
			return work, true
		}
	}
	return 0, false
}

func benchSpeed(b GearBench) float64 {
	if speed, ok := b.WorkSpeed.Value(); ok && speed > 0 && finite(speed) {
		return speed
	}
	return 1
}

// standingExports are the active pinned finite batches on the benches that
// make an export product: placed and unfinished goods that stay declared as
// they stand and count against the gap. unread is set when one's spec is.
func standingExports(benches []GearBench, products map[Resource]bool) (orders []OrderSpec, bills []GearBill, unread bool) {
	for _, b := range benches {
		list, _ := b.Bills.Value()
		for _, bill := range list {
			if bill.Spent || len(bill.Products) != 1 || !products[bill.Products[0]] {
				continue
			}
			worker, wk := bill.Worker.Value()
			if !wk || worker == "" {
				continue
			}
			if active, ak := bill.Active.Value(); ak && !active {
				continue
			}
			spec, sk := bill.Spec.Value()
			if !sk {
				unread = true
				continue
			}
			if spec.Mode != domain.GearBatch {
				continue
			}
			orders = append(orders, spec)
			bills = append(bills, bill)
		}
	}
	return orders, bills, unread
}

// DeclareExportOrders is MaintainTrade's wanted orders. While the silver gap
// is open every standing export batch is declared as it stands and, for what
// the goods held and in progress leave of the gap, one finite batch per free
// worker is added in descending score order, sized by the gap, the trader
// cash cap and the runway guard. A gap known closed declares nothing, so the
// ledger removes the batches. Abstain while the gap, the workers, the buyers
// or any recipe or standing bill that matters is unread.
func DeclareExportOrders(r ExportRequest) ExportPlan {
	plan := ExportPlan{}
	recipes, unread := exportRecipes(r)
	productSet := map[Resource]bool{}
	for _, e := range recipes {
		if !productSet[e.Product] {
			productSet[e.Product] = true
			plan.Products = append(plan.Products, e.Product)
		}
	}
	sort.Slice(plan.Products, func(i, j int) bool { return plan.Products[i] < plan.Products[j] })
	gap, gk := r.Gap.Value()
	if !gk {
		plan.Declared.Abstain = true
		return plan
	}
	if !(gap > 0) {
		plan.Declared.Abstain = unread
		return plan
	}
	profiles, pk := r.Profiles.Value()
	cash, ck := r.CashCap.Value()
	if !pk || !ck {
		plan.Declared.Abstain = true
		return plan
	}
	standing, bills, standingUnread := standingExports(r.Benches, productSet)
	plan.Declared.Orders = append(plan.Declared.Orders, standing...)
	plan.Declared.Abstain = unread || standingUnread
	busy := map[string]bool{}
	for _, o := range standing {
		busy[o.Worker] = true
	}
	// The goods already in flight: held, packed, and on standing bills.
	plan.InFlight = r.inFlight(recipes, bills)
	gap -= plan.InFlight
	if !(gap > 0) || cash <= 0 {
		return plan
	}
	draws := map[Resource]int64{}
	for _, bill := range bills {
		spec, _ := bill.Spec.Value()
		for _, a := range r.billDraw(recipes, spec) {
			draws[a.Resource] += a.Count
		}
	}
	guard := NewExportGuard(r.Runways, r.Supply, draws)
	type pair struct {
		cand   ExportCandidate
		worker PawnID
	}
	var pairs []pair
	usable := map[Resource]int64{}
	for _, q := range UsableIngredients(r.Runways, r.Supply) {
		usable[q.Key.Def] = q.Count
	}
	for _, e := range recipes {
		if buys, known := r.Buys[e.Product].Value(); !known {
			plan.Declared.Abstain = true
			continue
		} else if !buys {
			continue
		}
		// Gear held above demand is unread: the gap cannot be netted, so the
		// good is not ordered.
		if exportGear(r.Items, e.Product) && !r.SurplusKnown {
			plan.Declared.Abstain = true
			continue
		}
		for _, v := range exportVariants(r.Items, e) {
			// An ingredient the colony may not spend drops the candidate.
			if slices.ContainsFunc(v.Picks, func(a Amount) bool { have, ok := usable[a.Resource]; return !ok || have < a.Count }) {
				continue
			}
			for _, w := range exportWorkers(profiles, e.Required) {
				if c, ok := r.score(e, v, w.Skill); ok {
					c.Worker = w.ID
					pairs = append(pairs, pair{c, w.ID})
				}
			}
		}
	}
	sort.SliceStable(pairs, func(i, j int) bool {
		a, b := pairs[i].cand, pairs[j].cand
		if a.Score != b.Score {
			return a.Score > b.Score
		}
		if a.Recipe != b.Recipe {
			return a.Recipe < b.Recipe
		}
		if a.Stuff != b.Stuff {
			return a.Stuff < b.Stuff
		}
		return a.Worker < b.Worker
	})
	plan.Candidates = make([]ExportCandidate, 0, min(len(pairs), ExportViewCandidates))
	for _, p := range pairs[:min(len(pairs), ExportViewCandidates)] {
		plan.Candidates = append(plan.Candidates, p.cand)
	}
	budget := float64(cash)
	for _, p := range pairs {
		if !(gap > 0) || !(budget > 0) {
			break
		}
		c := p.cand
		if busy[string(p.worker)] {
			continue
		}
		price := ExportSellRatio * c.Value
		if !(price > 0) {
			continue
		}
		want := int64(min(math.Ceil(gap/price), math.Floor(budget/price), math.MaxInt32))
		if want < 1 {
			continue
		}
		granted, known := guard.Admit(c.Draw, want)
		if !known {
			plan.Declared.Abstain = true
			continue
		}
		if granted < 1 {
			continue
		}
		busy[string(p.worker)] = true
		gap -= float64(granted) * price
		budget -= float64(granted) * price
		plan.Ranked = append(plan.Ranked, c)
		plan.Declared.Orders = append(plan.Declared.Orders, OrderSpec{Recipe: c.Recipe, Ingredients: c.Filter, Worker: string(c.Worker), Mode: domain.GearBatch, Target: int32(granted), BenchKind: c.BenchKind, Product: c.Product, Class: ResourceMaterial})
	}
	return plan
}

// ExportIngredients are the ingredients the ranked goods' cheapest picks draw
// on (every variant of every rankable recipe): the stock and the hand sources
// the declaring planner reads to price them. Sorted.
func ExportIngredients(r ExportRequest) []Resource {
	recipes, _ := exportRecipes(r)
	seen := map[Resource]bool{}
	for _, e := range recipes {
		for _, v := range exportVariants(r.Items, e) {
			for _, a := range v.Picks {
				seen[a.Resource] = true
			}
		}
	}
	out := make([]Resource, 0, len(seen))
	for resource := range seen {
		out = append(out, resource)
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}

// RankExports is the candidates MaintainTrade would order against an open
// gap, best first, for the reader: DeclareExportOrders' ranking without the
// gap's sizing.
func RankExports(r ExportRequest) []ExportCandidate {
	recipes, _ := exportRecipes(r)
	profiles, _ := r.Profiles.Value()
	var out []ExportCandidate
	for _, e := range recipes {
		if buys, known := r.Buys[e.Product].Value(); !known || !buys {
			continue
		}
		for _, v := range exportVariants(r.Items, e) {
			best := ExportCandidate{}
			for _, w := range exportWorkers(profiles, e.Required) {
				if c, ok := r.score(e, v, w.Skill); ok && c.Score > best.Score {
					c.Worker, best = w.ID, c
				}
			}
			if best.Recipe != "" {
				out = append(out, best)
			}
		}
	}
	sort.SliceStable(out, func(i, j int) bool {
		if out[i].Score != out[j].Score {
			return out[i].Score > out[j].Score
		}
		if out[i].Recipe != out[j].Recipe {
			return out[i].Recipe < out[j].Recipe
		}
		return out[i].Stuff < out[j].Stuff
	})
	return out
}

// inFlight is the silver the goods already on the way are worth against the
// gap: the held stock of each export product (gear counts only its surplus),
// the packed sculptures and the pieces of the standing bills, each at the
// expected sale price.
func (r ExportRequest) inFlight(recipes []exportRecipe, bills []GearBill) float64 {
	price := map[Resource]float64{}
	cheapestSculpture := 0.0
	for _, e := range recipes {
		base, ok := r.Items.Market[e.Product]
		if !ok {
			continue
		}
		price[e.Product] = ExportSellRatio * base
		if e.Sculpture && (cheapestSculpture == 0 || price[e.Product] < cheapestSculpture) {
			cheapestSculpture = price[e.Product]
		}
	}
	total := 0.0
	held := map[Resource]int64{}
	for product := range price {
		held[product] = r.Stock[product]
	}
	if r.SurplusKnown {
		for definition := range price {
			if exportGear(r.Items, definition) {
				held[definition] = int64(r.Surplus.Weapons[definition])
			}
		}
		for _, row := range r.Surplus.Apparel {
			if _, ok := price[row.Definition]; ok {
				held[row.Definition] += int64(row.Count)
			}
		}
	} else {
		for definition := range price {
			if exportGear(r.Items, definition) {
				held[definition] = 0
			}
		}
	}
	for product, n := range held {
		total += float64(n) * price[product]
	}
	if packed, ok := r.Packed.Value(); ok && packed > 0 {
		total += float64(packed) * cheapestSculpture
	}
	for _, bill := range bills {
		if spec, ok := bill.Spec.Value(); ok && len(bill.Products) == 1 {
			total += float64(spec.Target) * price[bill.Products[0]]
		}
	}
	return total
}

// billDraw is the ingredient units a standing batch still draws: its pieces
// times one piece's picks under its ingredient filter.
func (r ExportRequest) billDraw(recipes []exportRecipe, spec OrderSpec) []Amount {
	for _, e := range recipes {
		if e.Recipe.Definition != spec.Recipe || e.Bench.Def != spec.BenchKind {
			continue
		}
		filter := make([]Resource, len(spec.Ingredients))
		for i, s := range spec.Ingredients {
			filter[i] = Resource(s)
		}
		picks, ok := exportPicks(e.Slots, filter, "")
		if !ok {
			return nil
		}
		out := make([]Amount, len(picks))
		for i, a := range picks {
			out[i] = Amount{a.Resource, a.Count * int64(spec.Target)}
		}
		return out
	}
	return nil
}

// ExportViewCandidates bounds the scored candidates an ExportPlan keeps for
// the ledger view.
const ExportViewCandidates = 10
