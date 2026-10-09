package policy

import (
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Candidates have already passed native outfit, forced/locked gear, player-job,
// ownership, reachability and resource-policy checks. Proposals still require a
// fresh native admission against the exact inspected loadout before dispatch.
type GearCandidate struct {
	Target     string
	Gain       float64
	Definition Resource
}
type GearReplacement struct {
	Definition Resource
	Stuff      Resource
	Reason     string
}

// GearApparel is one worn garment as the native loadout reports it:
// Condition is its hit-point fraction (1 for a definition without hit
// points) and Groups the body-part groups it covers.
type GearApparel struct {
	Definition Resource
	Condition  float64
	Groups     []string
}
type GearPawn struct {
	Climate *GearClimate
	Policy  domain.Fact[ApparelPolicyState]
	// LoadoutModel is unknown only for a pawn that cannot wear apparel
	// (Blocked, no apparel policy) or one the model refused, whose cause
	// ModelRefusal names; PlanColonyGear fails on a refused pawn rather than
	// judging it by a weaker rule.
	LoadoutModel domain.Fact[GearLoadoutInput]
	ModelRefusal string
	Pawn         PawnID
	Loadout      string
	Blocked      bool
	// Deficit and Replacements are projected from the loadout model's gaps
	// (modeledGearObservation); a census decoder never sets them.
	Deficit      domain.Fact[bool]
	Candidates   domain.Fact[[]GearCandidate]
	Replacements domain.Fact[[]GearReplacement]
	Apparel      domain.Fact[[]GearApparel]
}
type GearObservation struct {
	Pawns  []GearPawn
	Stored domain.Fact[[]GearStock]
	// Outfits is every outfit policy's load id; one no pawn keeps (OutfitKeep)
	// is owed a prune.
	Outfits domain.Fact[[]string]
}

// OutfitsToPrune is the outfits owed a prune: none until every pawn is on
// its own outfit (OutfitKeep).
func (v GearObservation) OutfitsToPrune() []string {
	keep, settled := OutfitKeep(v.Pawns)
	all, known := v.Outfits.Value()
	if !settled || !known {
		return nil
	}
	var r []string
	for _, id := range all {
		if !keep[id] {
			r = append(r, id)
		}
	}
	return r
}

// GearReview is the census MaintainEquipment is judged on. Recovered and
// Deficit follow the loadout model's gaps and the outfit policy; WornOut and
// Uncovered are the apparel-condition census (the fraction of pawns wearing
// any garment at or under the tattered threshold, and the fraction with a
// core body-part group uncovered), known only when every pawn's worn
// apparel was observed.
type GearReview struct {
	Loadouts  []GearLoadout
	Demand    domain.Fact[[]GearDemand]
	Recovered domain.Fact[bool]
	Deficit   domain.Fact[float64]
	WornOut   domain.Fact[float64]
	Uncovered domain.Fact[float64]
}

// GearTatteredCondition is the hit-point fraction at or under which native
// upkeep counts a garment as worn out and raises a replacement need.
const GearTatteredCondition = .5

// GearCoreGroups are the body-part groups a dressed colonist covers; a pawn
// wearing nothing over one of them is uncovered.
var GearCoreGroups = []string{"Torso", "Legs"}

func (a GearApparel) valid() bool {
	if !validResource(a.Definition) || !foodNumber(a.Condition) || a.Condition > 1 {
		return false
	}
	seen := map[string]bool{}
	for _, g := range a.Groups {
		if !foodID(g) || seen[g] {
			return false
		}
		seen[g] = true
	}
	return true
}

// tattered reports a pawn wearing any garment at or under the threshold.
func (p GearPawn) tattered() bool {
	apparel, _ := p.Apparel.Value()
	for _, a := range apparel {
		if a.Condition <= GearTatteredCondition {
			return true
		}
	}
	return false
}

// uncovered reports a pawn with a core body-part group no garment covers.
func (p GearPawn) uncovered() bool {
	apparel, _ := p.Apparel.Value()
	for _, group := range GearCoreGroups {
		covered := false
		for _, a := range apparel {
			for _, g := range a.Groups {
				covered = covered || g == group
			}
		}
		if !covered {
			return true
		}
	}
	return false
}

func (v GearObservation) Validate() error {
	if stored, known := v.Stored.Value(); known {
		seen := map[GearStock]bool{}
		for _, row := range stored {
			key := row
			key.Count = 0
			if !validResource(row.Definition) || row.Stuff != "" && !validResource(row.Stuff) || row.Quality < 0 || row.Quality > 6 || row.HPBand < 5 || row.HPBand > 9 || row.Count <= 0 || seen[key] {
				return errors.New("invalid gear storage")
			}
			seen[key] = true
		}
	}
	seen := map[PawnID]bool{}
	for _, p := range v.Pawns {
		if err := p.Climate.Validate(); err != nil {
			return err
		}
		if !foodID(string(p.Pawn)) || !foodID(p.Loadout) || seen[p.Pawn] {
			return errors.New("invalid gear pawn or loadout")
		}
		seen[p.Pawn] = true
		if model, known := p.LoadoutModel.Value(); known {
			if err := model.Validate(); err != nil {
				return err
			}
		}
		if candidates, known := p.Candidates.Value(); known {
			targets := map[string]bool{}
			for _, c := range candidates {
				if !foodID(c.Target) || !validResource(c.Definition) || !foodNumber(c.Gain) || c.Gain <= 0 || targets[c.Target] {
					return errors.New("invalid gear candidate")
				}
				targets[c.Target] = true
			}
		}
		if needs, known := p.Replacements.Value(); known {
			identities := map[GearReplacement]bool{}
			for _, n := range needs {
				if !validResource(n.Definition) || n.Stuff != "" && !validResource(n.Stuff) || !foodID(n.Reason) || identities[n] {
					return errors.New("invalid gear replacement need")
				}
				identities[n] = true
			}
		}
		if apparel, known := p.Apparel.Value(); known {
			for _, a := range apparel {
				if !a.valid() {
					return errors.New("invalid gear apparel")
				}
			}
		}
	}
	return nil
}

// ReviewGear matches the complete census used by equipment upkeep. An unknown
// or empty census cannot establish recovery; a pawn the loadout model refuses
// is an error naming the cause.
func ReviewGear(f domain.Fact[GearObservation]) (GearReview, error) {
	v, known := f.Value()
	if !known {
		return GearReview{}, nil
	}
	if err := v.Validate(); err != nil {
		return GearReview{}, err
	}
	if len(v.Pawns) == 0 {
		return GearReview{}, nil
	}
	missing, tattered, uncovered, dressed := 0, 0, 0, true
	loadouts, demand, err := PlanColonyGear(v.Pawns)
	if err != nil {
		return GearReview{}, err
	}
	modeled := map[PawnID]GearLoadout{}
	for _, l := range loadouts {
		modeled[l.Pawn] = l
	}
	for _, p := range v.Pawns {
		deficit := false
		if l, ok := modeled[p.Pawn]; ok {
			for _, gap := range l.Gaps {
				deficit = deficit || gap.Gain > GearGapThreshold(l.Role)
			}
		}
		_, policyNeeded := DesiredApparelPolicy(p)
		if deficit || policyNeeded {
			missing++
		}
		if _, known := p.Apparel.Value(); !known {
			dressed = false
			continue
		}
		if p.tattered() {
			tattered++
		}
		if p.uncovered() {
			uncovered++
		}
	}
	// Outfits owed a prune keep the goal open for the prune.
	if missing == 0 && len(v.OutfitsToPrune()) > 0 {
		missing++
	}
	n := float64(len(v.Pawns))
	review := GearReview{Loadouts: loadouts, Demand: demand, Recovered: domain.Known(missing == 0), Deficit: domain.Known(float64(missing) / n)}
	if dressed {
		review.WornOut = domain.Known(float64(tattered) / n)
		review.Uncovered = domain.Known(float64(uncovered) / n)
	}
	return review, nil
}

// GearReplacementNeeds is the definitions the census asks a bench to produce
// for pawns in deficit (replacement needs with no loose candidate to wear),
// sorted and deduplicated. Empty unless the census is known, valid and not
// recovered, so the work planner enables a bench's work type exactly while
// MaintainEquipment is in deficit and could raise a bill. A pawn the loadout
// model refuses is an error, not an empty answer.
func GearReplacementNeeds(f domain.Fact[GearObservation]) ([]Resource, error) {
	review, err := ReviewGear(f)
	if err != nil {
		return nil, err
	}
	if recovered, known := review.Recovered.Value(); !known || recovered {
		return nil, nil
	}
	v, _ := f.Value()
	v = modeledGearObservation(v, review.Loadouts)
	seen := map[Resource]bool{}
	var out []Resource
	for _, p := range v.Pawns {
		if deficit, _ := p.Deficit.Value(); !deficit {
			continue
		}
		needs, _ := p.Replacements.Value()
		for _, n := range needs {
			if !seen[n.Definition] {
				seen[n.Definition] = true
				out = append(out, n.Definition)
			}
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out, nil
}

type GearMethodKind string

const (
	GearUnknown   GearMethodKind = "unknown"
	GearRecovered GearMethodKind = "recovered"
	GearReplace   GearMethodKind = "replace"
	GearProduce   GearMethodKind = "produce"
	GearWait      GearMethodKind = "wait_for_existing_work"
	GearBlocked   GearMethodKind = "no_eligible_method"
)

type GearMethod struct {
	Count           int32
	Kind            GearMethodKind
	ID              domain.MethodID
	Pawn            PawnID
	Loadout, Target string
	Need            GearReplacement
	Bench, Recipe   string
	Filter          []Resource
	RequiredWork    []WorkRequirement
	// Bill is the standing bill a GearWait found already making Need.
	Bill GearBill
}
type GearBill struct {
	ID, Recipe string
	// Role is the recipe's role by its catalog row.
	Role   domain.RecipeRole
	Active domain.Fact[bool]
	// Worker is the pinned pawn, known "" when unrestricted.
	Worker   domain.Fact[string]
	Products []Resource
	// Finite is whether the bill repeats a count (neither Forever nor a
	// stock target), the only bills a stale-bill removal may name.
	Finite domain.Fact[bool]
	// Spec is the bill as the work ledger identifies it (recipe, ingredient
	// filter, worker pin, wire mode and count, bench definition); unknown
	// when the readback lacked the repeat mode or count.
	Spec domain.Fact[OrderSpec]
	// Spent is a finished bill; Kind is LedgerMechGestation for a gestation
	// recipe, LedgerProduction otherwise.
	Spent bool
	Kind  LedgerBillKind
}
type GearRecipe struct {
	Definition string
	// Role is what the recipe does by its catalog row.
	Role                   domain.RecipeRole
	Products               []Resource
	Available, AvailableOn domain.Fact[bool]
	Ingredients            domain.Fact[[][]Amount]
	RequiredWork           domain.Fact[[]WorkRequirement]
	// WorkAmount is the work one unit takes (the catalog's RecipeWorkAmount);
	// RequiredWork is the skill gate, not this.
	WorkAmount domain.Fact[float64]
	// MechKind is the PawnKindDef of the mech a gestation recipe makes,
	// "" for every other recipe.
	MechKind string
}
type GearBench struct {
	ID string
	// Def is the workbench definition, empty when unread; Usable whether the
	// bench takes a new bill.
	Def    string
	Usable domain.Fact[bool]
	// WorkSpeed is the bench's WorkTableWorkSpeedFactor now; unknown when
	// native sent none.
	WorkSpeed domain.Fact[float64]
	Bills     domain.Fact[[]GearBill]
	Recipes   domain.Fact[[]GearRecipe]
}

// RecipeHost is one native recipe definition with the player-buildable
// bench definitions that host it, read before any such bench exists so a
// workshop project can choose which bench to stage. Available is the
// recipe's own research gate; the bench definitions' own availability is
// judged against the planning census separately.
type RecipeHost struct {
	Definition   string
	Products     []Resource
	Available    bool
	Benches      []string
	Ingredients  domain.Fact[[][]Amount]
	RequiredWork domain.Fact[[]WorkRequirement]
	// Research names the ResearchProjectDefs the recipe itself requires
	// (sorted); empty when only its bench gates it.
	Research []string
}
type GearPlanningRequest struct {
	Observation domain.Fact[GearObservation]
	Seen        []domain.MethodID
	Benches     domain.Fact[[]GearBench]
	// StuffCategories are each stuff's catalog categories (ItemFacts): the
	// stuffs a bill's filter admits beside the loadout's own (gearFilter).
	StuffCategories map[Resource][]string
}

func gearMethodID(kind string, p GearPawn, target string, need GearReplacement) domain.MethodID {
	// Stable typed identity; retired methods stay seen for this Episode.
	value := struct {
		Pawn            PawnID
		Loadout, Target string
		Need            GearReplacement
	}{p.Pawn, p.Loadout, target, need}
	data, _ := json.Marshal(value)
	sum := sha256.Sum256(data)
	return domain.MethodID(fmt.Sprintf("gear-%s-%x", kind, sum[:16]))
}

func containsResource(values []Resource, want Resource) bool {
	for _, v := range values {
		if v == want {
			return true
		}
	}
	return false
}

// SelectGearMethod proposes one exact replacement or one demand-sized bill.
// It issues no game orders and does not reserve resources. The shared method
// admission must recheck these costs against concurrent plans before committing.
func SelectGearMethod(r GearPlanningRequest) (GearMethod, error) {
	review, err := ReviewGear(r.Observation)
	if err != nil {
		return GearMethod{}, err
	}
	if _, known := review.Recovered.Value(); !known {
		return GearMethod{Kind: GearUnknown}, nil
	}
	if positive(review.Recovered) {
		return GearMethod{Kind: GearRecovered}, nil
	}
	v, _ := r.Observation.Value()
	return selectGear(r, review, modeledGearObservation(v, review.Loadouts))
}

// selectGear chooses the method for a census already projected from the
// loadout model (modeledGearObservation).
func selectGear(r GearPlanningRequest, review GearReview, v GearObservation) (GearMethod, error) {
	seen, err := gearSeen(r.Seen)
	if err != nil {
		return GearMethod{}, err
	}
	if err := validateGearProduction(nil); err != nil {
		return GearMethod{}, err
	}
	type choice struct {
		pawn      GearPawn
		candidate GearCandidate
	}
	choices := []choice{}
	existing := false
	for _, p := range v.Pawns {
		candidates, _ := p.Candidates.Value()
		existing = existing || len(candidates) > 0
		if !p.Blocked {
			for _, c := range candidates {
				choices = append(choices, choice{p, c})
			}
		}
	}
	sort.Slice(choices, func(i, j int) bool {
		a, b := choices[i], choices[j]
		if a.candidate.Gain != b.candidate.Gain {
			return a.candidate.Gain > b.candidate.Gain
		}
		if a.pawn.Pawn != b.pawn.Pawn {
			return a.pawn.Pawn < b.pawn.Pawn
		}
		return a.candidate.Target < b.candidate.Target
	})
	for _, c := range choices {
		id := gearMethodID("replace", c.pawn, c.candidate.Target, GearReplacement{})
		if !seen[id] {
			return GearMethod{Kind: GearReplace, ID: id, Pawn: c.pawn.Pawn, Loadout: c.pawn.Loadout, Target: c.candidate.Target}, nil
		}
	}
	if existing {
		return GearMethod{Kind: GearBlocked}, nil
	}
	needs := []gearNeed{}
	for _, p := range v.Pawns {
		if p.Blocked {
			continue
		}
		ns, known := p.Replacements.Value()
		if !known {
			return GearMethod{Kind: GearUnknown}, nil
		}
		for _, n := range ns {
			// The armory crafts body armor and helmets.
			if !ArmoryArmor(n.Definition) {
				needs = append(needs, gearNeed{p, n})
			}
		}
	}
	return produceGear(needs, v, review, seen, r)
}

// gearSeen indexes a goal's method history, refusing a malformed one.
func gearSeen(ids []domain.MethodID) (map[domain.MethodID]bool, error) {
	seen := map[domain.MethodID]bool{}
	for _, id := range ids {
		if !foodID(string(id)) || seen[id] {
			return nil, errors.New("invalid gear method history")
		}
		seen[id] = true
	}
	return seen, nil
}

// gearNeed is one definition to produce, attributed to a pawn or to the
// armory's colony weapon batch.
type gearNeed struct {
	pawn GearPawn
	need GearReplacement
}

// produceGear proposes one demand-sized bill for the first need a funded
// recipe covers after netting stored stock, or waits on an existing bill.
func produceGear(needs []gearNeed, v GearObservation, review GearReview, seen map[domain.MethodID]bool, r GearPlanningRequest) (GearMethod, error) {
	if len(needs) == 0 {
		return GearMethod{Kind: GearBlocked}, nil
	}
	sortGearNeeds(needs)
	benches, demand, known, err := gearProductionDemand(needs, v, review, r)
	if err != nil || !known {
		return GearMethod{Kind: GearUnknown}, err
	}
	for _, n := range needs {
		count := demand[gearStockKey{n.need.Definition, n.need.Stuff}]
		if count == 0 {
			continue
		}
		id := gearMethodID("produce", n.pawn, "", n.need)
		if seen[id] {
			return GearMethod{Kind: GearWait, ID: id, Pawn: n.pawn.Pawn, Loadout: n.pawn.Loadout, Need: n.need}, nil
		}
		method, resolved, err := produceNeed(n, count, benches, r)
		if err != nil || resolved {
			return method, err
		}
	}
	return GearMethod{Kind: GearBlocked}, nil
}

// gearProductionDemand is the bench census sorted by id and, per definition
// and stuff, how many items the needs still lack after netting the stored
// stock. known is false while the bench census is unread.
func gearProductionDemand(needs []gearNeed, v GearObservation, review GearReview, r GearPlanningRequest) (benches []GearBench, demand map[gearStockKey]int, known bool, err error) {
	benches, known = r.Benches.Value()
	if !known {
		return nil, nil, false, nil
	}
	if err = validateGearProduction(benches); err != nil {
		return nil, nil, false, err
	}
	benches = append([]GearBench(nil), benches...)
	sort.Slice(benches, func(i, j int) bool { return benches[i].ID < benches[j].ID })
	demand = map[gearStockKey]int{}
	for _, n := range needs {
		demand[gearStockKey{n.need.Definition, n.need.Stuff}]++
	}
	stored, _ := v.Stored.Value()
	stored = unassignedGearStock(stored, review.Loadouts)
	for _, row := range stored {
		if row.HPBand >= 5 && row.Quality >= 2 {
			key := gearStockKey{row.Definition, row.Stuff}
			demand[key] = max(0, demand[key]-row.Count)
		}
	}
	for _, count := range demand {
		if count > math.MaxInt32 {
			return nil, nil, false, errors.New("gear batch exceeds bill bound")
		}
	}
	return benches, demand, true, nil
}

// produceNeed resolves one need of count items against the benches: Wait with
// the standing bill (Bill) that already makes it, Unknown while a bench's
// bills or a recipe's facts are unread, Produce with the first funded recipe.
// resolved is false when no recipe is funded, so the next need is judged.
func produceNeed(n gearNeed, count int, benches []GearBench, r GearPlanningRequest) (method GearMethod, resolved bool, err error) {
	// Check every bench before adding a bill, including later-sorted player benches.
	for _, b := range benches {
		bills, known := b.Bills.Value()
		if !known {
			return GearMethod{Kind: GearUnknown}, true, nil
		}
		for _, bill := range bills {
			if bill.Spent || !containsResource(bill.Products, n.need.Definition) {
				continue
			}
			active, known := bill.Active.Value()
			if !known {
				return GearMethod{Kind: GearUnknown}, true, nil
			}
			if active {
				return GearMethod{Kind: GearWait, Need: n.need, Bill: bill}, true, nil
			}
		}
	}
	for _, b := range benches {
		recipes, known := b.Recipes.Value()
		if !known {
			return GearMethod{Kind: GearUnknown}, true, nil
		}
		recipes = append([]GearRecipe(nil), recipes...)
		sort.Slice(recipes, func(i, j int) bool { return recipes[i].Definition < recipes[j].Definition })
		for _, recipe := range recipes {
			if !containsResource(recipe.Products, n.need.Definition) {
				continue
			}
			available, ak := recipe.Available.Value()
			on, ok := recipe.AvailableOn.Value()
			if ak && !available || ok && !on {
				continue
			}
			if !ak || !ok {
				return GearMethod{Kind: GearUnknown}, true, nil
			}
			work, known := recipe.RequiredWork.Value()
			if !known {
				return GearMethod{Kind: GearUnknown}, true, nil
			}
			slots, known := recipe.Ingredients.Value()
			if !known {
				return GearMethod{Kind: GearUnknown}, true, nil
			}
			slots = gearBatchIngredients(slots, count)
			if filter, ok := gearFilter(slots, n.need.Stuff, r.StuffCategories); ok {
				id := gearMethodID("produce", n.pawn, "", n.need)
				return GearMethod{Kind: GearProduce, Count: int32(count), ID: id, Pawn: n.pawn.Pawn, Loadout: n.pawn.Loadout, Need: n.need, Bench: b.ID, Recipe: recipe.Definition, Filter: filter, RequiredWork: append([]WorkRequirement(nil), work...)}, true, nil
			}
		}
	}
	return GearMethod{}, false, nil
}

func validateGearProduction(benches []GearBench) error {
	ids := map[string]bool{}
	products := func(v []Resource) bool {
		seen := map[Resource]bool{}
		for _, p := range v {
			if !validResource(p) || seen[p] {
				return false
			}
			seen[p] = true
		}
		return true
	}
	for _, b := range benches {
		if !foodID(b.ID) || ids[b.ID] {
			return errors.New("invalid gear bench")
		}
		ids[b.ID] = true
		bills, _ := b.Bills.Value()
		recipes, _ := b.Recipes.Value()
		for _, bill := range bills {
			if !products(bill.Products) {
				return errors.New("invalid bill products")
			}
		}
		names := map[string]bool{}
		for _, recipe := range recipes {
			if !foodID(recipe.Definition) || names[recipe.Definition] || !products(recipe.Products) {
				return errors.New("invalid gear recipe")
			}
			names[recipe.Definition] = true
			if work, known := recipe.RequiredWork.Value(); known {
				if _, err := AssignWork(nil, work); err != nil {
					return err
				}
			}
			slots, _ := recipe.Ingredients.Value()
			for _, slot := range slots {
				if len(slot) == 0 {
					return errors.New("invalid ingredient alternatives")
				}
				resources := map[Resource]bool{}
				for _, a := range slot {
					if !validResource(a.Resource) || a.Count <= 0 || resources[a.Resource] {
						return errors.New("invalid ingredient amount")
					}
					resources[a.Resource] = true
				}
			}
		}
	}
	return nil
}

// gearValuables are the stuffs a bill never draws on unless the loadout
// names them: the currency and the favor-sale gold.
var gearValuables = map[Resource]bool{"Silver": true, FavorSaleResource: true}

// gearFilter is the ingredient filter of a bill placed with nothing in stock.
// A slot holding the loadout's stuff admits it and the stuffs
// sharing a catalog category with it (never a valuable); any other slot
// admits only its cheapest non-valuable alternative (fewest units, then
// name), the member OpenBillDemand asks for. ok is false when a slot has no
// admissible member, the loadout's stuff is in no slot, or the flat native
// filter would let another slot's member stand in for a stuff-bearing slot.
func gearFilter(slots [][]Amount, stuff Resource, categories map[Resource][]string) ([]Resource, bool) {
	admitted := map[Resource]bool{}
	members := make([]map[Resource]bool, len(slots))
	stuffSlot := make([]bool, len(slots))
	stuffed := false
	for i, slot := range slots {
		members[i] = map[Resource]bool{}
		stuffSlot[i] = stuff != "" && slices.ContainsFunc(slot, func(a Amount) bool { return a.Resource == stuff })
		stuffed = stuffed || stuffSlot[i]
		var pick Amount
		for _, a := range slot {
			if stuffSlot[i] {
				if a.Resource == stuff || !gearValuables[a.Resource] && slices.ContainsFunc(categories[stuff], func(c string) bool { return slices.Contains(categories[a.Resource], c) }) {
					members[i][a.Resource] = true
				}
			} else if !gearValuables[a.Resource] && (pick.Resource == "" || a.Count < pick.Count || a.Count == pick.Count && a.Resource < pick.Resource) {
				pick = a
			}
		}
		if pick.Resource != "" {
			members[i][pick.Resource] = true
		}
		if len(members[i]) == 0 {
			return nil, false
		}
		for r := range members[i] {
			admitted[r] = true
		}
	}
	if stuff != "" && !stuffed {
		return nil, false
	}
	for i, slot := range slots {
		if !stuffSlot[i] {
			continue
		}
		for _, a := range slot {
			if admitted[a.Resource] && !members[i][a.Resource] {
				return nil, false
			}
		}
	}
	filter := make([]Resource, 0, len(admitted))
	for r := range admitted {
		filter = append(filter, r)
	}
	slices.Sort(filter)
	return filter, true
}
