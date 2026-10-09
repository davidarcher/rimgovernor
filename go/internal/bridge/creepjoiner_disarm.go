package bridge

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The creepjoiner disarming's def lookups, see policy.CreepJoinerDisarm
// for the game rules. They read the def mirror and name no def.

// CreepJoinerDisarm is the catalog's disarming input: each pawn kind's race
// body sites and each install recipe's item price. A nil catalog has none.
func (catalog *DefinitionCatalog) CreepJoinerDisarm() (policy.CreepJoinerDisarm, error) {
	if catalog == nil {
		return policy.CreepJoinerDisarm{}, nil
	}
	catalog.disarmOnce.Do(func() { catalog.disarm, catalog.disarmErr = catalog.buildDisarm() })
	return catalog.disarm, catalog.disarmErr
}

func (catalog *DefinitionCatalog) buildDisarm() (policy.CreepJoinerDisarm, error) {
	out := policy.CreepJoinerDisarm{Sites: map[string][]policy.DisarmSite{}, InstallValue: map[string]float64{}}
	byRace := map[string][]policy.DisarmSite{}
	for _, kind := range catalog.pawnKinds() {
		race := kind.GetRace()
		sites, done := byRace[race]
		if !done {
			var err error
			if sites, err = catalog.raceDisarmSites(race); err != nil {
				return policy.CreepJoinerDisarm{}, err
			}
			byRace[race] = sites
		}
		if len(sites) > 0 {
			out.Sites[kind.GetDefName()] = sites
		}
	}
	for name, row := range catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()] {
		recipe, ok := row.(*d.RecipeDef)
		if !ok || recipe.GetAddsHediff() == "" || len(recipe.GetAppliedOnFixedBodyParts()) == 0 {
			continue
		}
		// A def with no market value row leaves the recipe unpriced: the
		// planner then says so instead of guessing.
		if value, err := catalog.installItemValue(recipe); err == nil {
			out.InstallValue[name] = value
		}
	}
	return out, nil
}

// installItemValue is the market value of the items a recipe consumes by
// name: each ingredient slot that is exactly one def, at its count. The
// medicine slot names categories and is priced by the operation.
func (catalog *DefinitionCatalog) installItemValue(recipe *d.RecipeDef) (float64, error) {
	total := 0.0
	for _, slot := range recipe.GetIngredients() {
		filter := slot.GetValue().GetFilter()
		if len(filter.GetThingDefs()) != 1 || len(filter.GetCategories()) != 0 {
			continue
		}
		price, err := catalog.StatValue(filter.GetThingDefs()[0], "", statMarketValue)
		if err != nil {
			return 0, err
		}
		total += float64(price) * float64(slot.GetValue().GetCount())
	}
	return total, nil
}

// bodyPart is one part of a body in BodyDef.AllParts order (the game's
// pre-order walk, BodyDef.CacheDataRecursive).
type bodyPart struct {
	def    string
	groups []string
	parent int
}

// raceDisarmSites are the sites of a race: the race's melee tools other than
// the one that is always usable each link to a body part group; the site is
// the lowest part containing every part of that group. A race with no
// always-usable tool (nothing would be left to fight with) has none.
func (catalog *DefinitionCatalog) raceDisarmSites(race string) ([]policy.DisarmSite, error) {
	thing := catalog.ThingDefs[race]
	if thing == nil {
		return nil, contract("pawn kind race %s has no ThingDef row", race)
	}
	var groups []string
	always := false
	for _, tool := range thing.GetTools() {
		t := tool.GetValue()
		switch {
		case t.GetEnsureLinkedBodyPartsGroupAlwaysUsable():
			always = true
		case t.GetLinkedBodyPartsGroup() != "" && !slices.Contains(groups, t.GetLinkedBodyPartsGroup()):
			groups = append(groups, t.GetLinkedBodyPartsGroup())
		}
	}
	if !always || len(groups) == 0 {
		return nil, nil
	}
	bodyName := thing.GetRace().GetBody()
	body := DefRow[*d.BodyDef](catalog, bodyName)
	if body == nil {
		return nil, contract("race %s has no BodyDef row %q", race, bodyName)
	}
	var parts []bodyPart
	var walk func(record *d.BodyPartRecord, parent int)
	walk = func(record *d.BodyPartRecord, parent int) {
		index := len(parts)
		parts = append(parts, bodyPart{def: record.GetDef(), groups: record.GetGroups(), parent: parent})
		for _, child := range record.GetParts() {
			walk(child.GetValue(), index)
		}
	}
	if body.GetCorePart() == nil {
		return nil, contract("body %s has no core part", bodyName)
	}
	walk(body.GetCorePart(), -1)
	var sites []policy.DisarmSite
	seen := map[int]bool{}
	for _, group := range groups {
		site := -1
		for i, part := range parts {
			if slices.Contains(part.groups, group) {
				site = lowestCommon(parts, site, i)
			}
		}
		if site < 0 || seen[site] {
			continue
		}
		seen[site] = true
		sites = append(sites, policy.DisarmSite{Index: site, Part: parts[site].def, Ancestors: ancestors(parts, site)})
	}
	slices.SortFunc(sites, func(a, b policy.DisarmSite) int { return a.Index - b.Index })
	return sites, nil
}

// ancestors are the indexes of a part's ancestors, nearest first.
func ancestors(parts []bodyPart, index int) []int {
	var out []int
	for p := parts[index].parent; p >= 0; p = parts[p].parent {
		out = append(out, p)
	}
	return out
}

// lowestCommon is the lowest part that is a or b or an ancestor of both; a
// negative a is no part yet.
func lowestCommon(parts []bodyPart, a, b int) int {
	if a < 0 {
		return b
	}
	chainA := append([]int{a}, ancestors(parts, a)...)
	for _, x := range append([]int{b}, ancestors(parts, b)...) {
		if slices.Contains(chainA, x) {
			return x
		}
	}
	return -1
}
