package policy

import (
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
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
type GearPawn struct {
	Pawn         PawnID
	Loadout      string
	Blocked      bool
	Deficit      domain.Fact[bool]
	Candidates   domain.Fact[[]GearCandidate]
	Replacements domain.Fact[[]GearReplacement]
}
type GearObservation struct{ Pawns []GearPawn }
type GearReview struct {
	Recovered domain.Fact[bool]
	Deficit   domain.Fact[float64]
}

func (v GearObservation) Validate() error {
	if len(v.Pawns) > 256 {
		return errors.New("gear census exceeds bound")
	}
	seen := map[PawnID]bool{}
	for _, p := range v.Pawns {
		if !foodID(string(p.Pawn)) || !foodID(p.Loadout) || seen[p.Pawn] {
			return errors.New("invalid gear pawn or loadout")
		}
		seen[p.Pawn] = true
		if candidates, known := p.Candidates.Value(); known {
			if len(candidates) > 256 {
				return errors.New("gear candidates exceed bound")
			}
			targets := map[string]bool{}
			for _, c := range candidates {
				if !foodID(c.Target) || !validResource(c.Definition) || !foodNumber(c.Gain) || c.Gain <= 0 || targets[c.Target] {
					return errors.New("invalid gear candidate")
				}
				targets[c.Target] = true
			}
		}
		if needs, known := p.Replacements.Value(); known {
			if len(needs) > 256 {
				return errors.New("gear replacement needs exceed bound")
			}
			identities := map[GearReplacement]bool{}
			for _, n := range needs {
				if !validResource(n.Definition) || n.Stuff != "" && !validResource(n.Stuff) || !foodID(n.Reason) || identities[n] {
					return errors.New("invalid gear replacement need")
				}
				identities[n] = true
			}
		}
	}
	return nil
}

// ReviewGear matches the complete native census used by equipment upkeep.
// Unknown fields and an empty census cannot establish recovery.
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
	missing := 0
	for _, p := range v.Pawns {
		deficit, dk := p.Deficit.Value()
		candidates, ck := p.Candidates.Value()
		if !dk || !ck {
			return GearReview{}, nil
		}
		if deficit || len(candidates) > 0 {
			missing++
		}
	}
	return GearReview{Recovered: domain.Known(missing == 0), Deficit: domain.Known(float64(missing) / float64(len(v.Pawns)))}, nil
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
	Kind            GearMethodKind
	ID              domain.MethodID
	Pawn            PawnID
	Loadout, Target string
	Need            GearReplacement
	Bench, Recipe   string
	Costs           []Amount
	Filter          []Resource
}
type GearBill struct {
	Active   domain.Fact[bool]
	Products []Resource
}
type GearRecipe struct {
	Definition             string
	Products               []Resource
	Available, AvailableOn domain.Fact[bool]
	Ingredients            domain.Fact[[][]Amount]
}
type GearBench struct {
	ID      string
	Bills   domain.Fact[[]GearBill]
	Recipes domain.Fact[[]GearRecipe]
}
type GearPlanningRequest struct {
	Observation domain.Fact[GearObservation]
	Seen        []domain.MethodID
	Benches     domain.Fact[[]GearBench]
	Stock       []Stock
	Rules       []ResourceRule
	Holds       []Amount
}

func gearMethodID(kind string, p GearPawn, target string, need GearReplacement) domain.MethodID {
	// Stable typed identity; retired methods stay seen for this goal epoch.
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

// SelectGearMethod proposes one exact replacement or one repeat-count-one bill.
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
	if len(r.Seen) > 4096 {
		return GearMethod{}, errors.New("gear method history exceeds bound")
	}
	seen := map[domain.MethodID]bool{}
	for _, id := range r.Seen {
		if !foodID(string(id)) || seen[id] {
			return GearMethod{}, errors.New("invalid gear method history")
		}
		seen[id] = true
	}
	v, _ := r.Observation.Value()
	if err := validateGearProduction(nil, r); err != nil {
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
			costs, _, funded, unknown := gearIngredients([][]Amount{{{Resource: c.candidate.Definition, Count: 1}}}, "", r)
			if unknown {
				return GearMethod{Kind: GearUnknown}, nil
			}
			if !funded {
				continue
			}
			return GearMethod{Kind: GearReplace, ID: id, Pawn: c.pawn.Pawn, Loadout: c.pawn.Loadout, Target: c.candidate.Target, Costs: costs}, nil
		}
	}
	if existing {
		return GearMethod{Kind: GearBlocked}, nil
	}
	type replacement struct {
		pawn GearPawn
		need GearReplacement
	}
	needs := []replacement{}
	for _, p := range v.Pawns {
		if p.Blocked {
			continue
		}
		ns, known := p.Replacements.Value()
		if !known {
			return GearMethod{Kind: GearUnknown}, nil
		}
		for _, n := range ns {
			needs = append(needs, replacement{p, n})
		}
	}
	if len(needs) == 0 {
		return GearMethod{Kind: GearBlocked}, nil
	}
	sort.SliceStable(needs, func(i, j int) bool {
		a, b := needs[i], needs[j]
		if a.pawn.Pawn != b.pawn.Pawn {
			return a.pawn.Pawn < b.pawn.Pawn
		}
		if a.need.Definition != b.need.Definition {
			return a.need.Definition < b.need.Definition
		}
		if a.need.Stuff != b.need.Stuff {
			return a.need.Stuff < b.need.Stuff
		}
		return a.need.Reason < b.need.Reason
	})
	benches, known := r.Benches.Value()
	if !known {
		return GearMethod{Kind: GearUnknown}, nil
	}
	if err := validateGearProduction(benches, r); err != nil {
		return GearMethod{}, err
	}
	benches = append([]GearBench(nil), benches...)
	sort.Slice(benches, func(i, j int) bool { return benches[i].ID < benches[j].ID })
	for _, n := range needs {
		id := gearMethodID("produce", n.pawn, "", n.need)
		if seen[id] {
			return GearMethod{Kind: GearWait, ID: id, Pawn: n.pawn.Pawn, Loadout: n.pawn.Loadout, Need: n.need}, nil
		}
		// Check every bench before adding a bill, including later-sorted player benches.
		for _, b := range benches {
			bills, known := b.Bills.Value()
			if !known {
				return GearMethod{Kind: GearUnknown}, nil
			}
			for _, bill := range bills {
				if !containsResource(bill.Products, n.need.Definition) {
					continue
				}
				active, known := bill.Active.Value()
				if !known {
					return GearMethod{Kind: GearUnknown}, nil
				}
				if active {
					return GearMethod{Kind: GearWait, Need: n.need}, nil
				}
			}
		}
		for _, b := range benches {
			recipes, known := b.Recipes.Value()
			if !known {
				return GearMethod{Kind: GearUnknown}, nil
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
					return GearMethod{Kind: GearUnknown}, nil
				}
				slots, known := recipe.Ingredients.Value()
				if !known {
					return GearMethod{Kind: GearUnknown}, nil
				}
				costs, filter, ok, unknown := gearIngredients(slots, n.need.Stuff, r)
				if unknown {
					return GearMethod{Kind: GearUnknown}, nil
				}
				if ok {
					return GearMethod{Kind: GearProduce, ID: id, Pawn: n.pawn.Pawn, Loadout: n.pawn.Loadout, Need: n.need, Bench: b.ID, Recipe: recipe.Definition, Costs: costs, Filter: filter}, nil
				}
			}
		}
	}
	return GearMethod{Kind: GearBlocked}, nil
}

func validateGearProduction(benches []GearBench, r GearPlanningRequest) error {
	if len(benches) > 256 || len(r.Stock) > 4096 || len(r.Holds) > 4096 {
		return errors.New("gear production inputs exceed bound")
	}
	if err := ValidateResourceRules(r.Rules); err != nil {
		return err
	}
	stock := map[Resource]bool{}
	for _, s := range r.Stock {
		n, k := s.Available.Value()
		if !validResource(s.Resource) || stock[s.Resource] || k && n < 0 {
			return errors.New("invalid gear stock")
		}
		stock[s.Resource] = true
	}
	for _, h := range r.Holds {
		if !validResource(h.Resource) || h.Count < 0 {
			return errors.New("invalid gear resource hold")
		}
	}
	ids := map[string]bool{}
	products := func(v []Resource) bool {
		if len(v) > 256 {
			return false
		}
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
		if len(bills) > 256 || len(recipes) > 256 {
			return errors.New("gear workshop census exceeds bound")
		}
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
			slots, _ := recipe.Ingredients.Value()
			if len(slots) > 256 {
				return errors.New("recipe ingredients exceed bound")
			}
			for _, slot := range slots {
				if len(slot) == 0 || len(slot) > 256 {
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

func gearIngredients(slots [][]Amount, stuff Resource, r GearPlanningRequest) ([]Amount, []Resource, bool, bool) {
	available := map[Resource]int64{}
	knownStock := map[Resource]bool{}
	for _, s := range r.Stock {
		if n, known := s.Available.Value(); known {
			available[s.Resource] = n
			knownStock[s.Resource] = true
		}
	}
	for _, rule := range r.Rules {
		if rule.Spending != Allow {
			available[rule.Resource] = 0
			knownStock[rule.Resource] = true
		} else {
			available[rule.Resource] = max(0, available[rule.Resource]-rule.Reserve)
		}
	}
	for _, hold := range r.Holds {
		available[hold.Resource] = max(0, available[hold.Resource]-hold.Count)
	}
	used := map[Resource]int64{}
	for _, slot := range slots {
		hasStuff := false
		for _, a := range slot {
			hasStuff = hasStuff || a.Resource == stuff
		}
		options := append([]Amount(nil), slot...)
		sort.Slice(options, func(i, j int) bool {
			if options[i].Count != options[j].Count {
				return options[i].Count < options[j].Count
			}
			return options[i].Resource < options[j].Resource
		})
		chosen := false
		unknown := false
		for _, a := range options {
			if hasStuff && a.Resource != stuff {
				continue
			}
			if !knownStock[a.Resource] {
				unknown = true
				continue
			}
			if available[a.Resource] < a.Count {
				continue
			}
			available[a.Resource] -= a.Count
			used[a.Resource] += a.Count
			chosen = true
			break
		}
		if !chosen {
			return nil, nil, false, unknown
		}
	}
	if stuff != "" {
		if used[stuff] == 0 {
			return nil, nil, false, false
		}
		// The native bill filter is flat: another chosen ingredient must not also
		// allow substituting a different material in a stuff-bearing slot.
		for _, slot := range slots {
			hasStuff := false
			other := false
			for _, a := range slot {
				hasStuff = hasStuff || a.Resource == stuff
				other = other || a.Resource != stuff && used[a.Resource] > 0
			}
			if hasStuff && other {
				return nil, nil, false, false
			}
		}
	}
	filter := []Resource{}
	for resource := range used {
		filter = append(filter, resource)
	}
	sort.Slice(filter, func(i, j int) bool { return filter[i] < filter[j] })
	costs := []Amount{}
	for _, resource := range filter {
		costs = append(costs, Amount{resource, used[resource]})
	}
	return costs, filter, true, false
}
