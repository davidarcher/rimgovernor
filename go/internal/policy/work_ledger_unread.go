package policy

import "slices"

// UnreadFact names the one input a planner did not read when it abstained from
// declaring. The set is closed: every abstain site picks a constant, so the
// ledger view can say which input was missing instead of only who abstained.
type UnreadFact string

const (
	UnreadReview      UnreadFact = "review"
	UnreadStock       UnreadFact = "stock"
	UnreadBenches     UnreadFact = "benches"
	UnreadBenchDef    UnreadFact = "bench_definition"
	UnreadBills       UnreadFact = "bills"
	UnreadRecipes     UnreadFact = "recipes"
	UnreadDefinitions UnreadFact = "definitions"
	UnreadNativeRead  UnreadFact = "native_source"
	UnreadMedicine    UnreadFact = "medicine"
	UnreadArtists     UnreadFact = "artists"
	UnreadArtNeed     UnreadFact = "art_need"
	UnreadGearRecover UnreadFact = "gear_recovery"
	UnreadGearNeeds   UnreadFact = "gear_replacements"
	UnreadGearDemand  UnreadFact = "gear_demand"
	UnreadArmoryTier  UnreadFact = "armory_tier"
	UnreadWeapons     UnreadFact = "weapon_demand"
	UnreadShells      UnreadFact = "shell_targets"
	UnreadFoodFacts   UnreadFact = "food_facts"
	UnreadFoodPlan    UnreadFact = "food_plan"
	UnreadFoodStorage UnreadFact = "food_storage"
	UnreadBabyFeeding UnreadFact = "baby_feeding"
	UnreadMechs       UnreadFact = "mech_gestation"
	UnreadWort        UnreadFact = "wort"
	UnreadQuestOffers UnreadFact = "quest_offers"
	UnreadQuestAsker  UnreadFact = "quest_asker"
	UnreadSilverGap   UnreadFact = "silver_gap"
	UnreadWorkers     UnreadFact = "workers"
	UnreadTraderCash  UnreadFact = "trader_cash"
	UnreadBuyers      UnreadFact = "buyers"
	UnreadSurplus     UnreadFact = "surplus"
	UnreadRunways     UnreadFact = "runways"
	UnreadSupply      UnreadFact = "supply"
	UnreadSources     UnreadFact = "sources"
)

// UnreadFacts is every UnreadFact, in the order the view lists them. A policy
// test holds it equal to the constants above.
var UnreadFacts = []UnreadFact{
	UnreadReview, UnreadStock, UnreadBenches, UnreadBenchDef, UnreadBills, UnreadRecipes, UnreadDefinitions, UnreadNativeRead,
	UnreadMedicine, UnreadArtists, UnreadArtNeed, UnreadGearRecover, UnreadGearNeeds, UnreadGearDemand, UnreadArmoryTier,
	UnreadWeapons, UnreadShells, UnreadFoodFacts, UnreadFoodPlan, UnreadFoodStorage, UnreadBabyFeeding, UnreadMechs, UnreadWort,
	UnreadQuestOffers, UnreadQuestAsker, UnreadSilverGap, UnreadWorkers, UnreadTraderCash, UnreadBuyers, UnreadSurplus,
	UnreadRunways, UnreadSupply, UnreadSources,
}

// Abstain is one concern's refusal to speak this Round: Fact is the input it
// lacked. Its silence about a bill proves nothing, so the ledger removes
// nothing while any concern abstains. The declaring planner stamps Concern
// (Declared.For); the policy site that finds the gap only knows the fact.
type Abstain struct {
	Concern ConcernID
	Fact    UnreadFact
}

// Abstaining is a declaration that lacked the fact.
func Abstaining(fact UnreadFact) Declared {
	return Declared{Abstains: []Abstain{{Fact: fact}}}
}

// Unread records that the declaration lacked fact, once per fact.
func (d *Declared) Unread(fact UnreadFact) {
	if !slices.ContainsFunc(d.Abstains, func(a Abstain) bool { return a.Fact == fact }) {
		d.Abstains = append(d.Abstains, Abstain{Fact: fact})
	}
}

// Abstained is whether any concern abstained.
func (d Declared) Abstained() bool { return len(d.Abstains) > 0 }

// For is the declaration made on behalf of concern: every order without an
// owner is owned by it and every abstain without a concern names it. A
// declarer serving several concerns calls For per concern's part.
func (d Declared) For(concern ConcernID) Declared {
	out := Declared{Orders: slices.Clone(d.Orders), Abstains: slices.Clone(d.Abstains)}
	for i := range out.Orders {
		if out.Orders[i].Owner == "" {
			out.Orders[i].Owner = concern
		}
	}
	for i := range out.Abstains {
		if out.Abstains[i].Concern == "" {
			out.Abstains[i].Concern = concern
		}
	}
	return out
}

// Merge appends other's orders and abstains.
func (d *Declared) Merge(other Declared) {
	d.Orders = append(d.Orders, other.Orders...)
	d.Abstains = append(d.Abstains, other.Abstains...)
}

// OrderOwners is every concern that declared each wanted spec, by spec Key.
// Two concerns declaring one spec coalesce into one order that both own.
func OrderOwners(declared []Declared) map[string][]ConcernID {
	owners := map[string][]ConcernID{}
	for _, d := range declared {
		for _, o := range d.Orders {
			k := o.Key()
			if o.Owner != "" && !slices.Contains(owners[k], o.Owner) {
				owners[k] = append(owners[k], o.Owner)
			}
		}
	}
	for _, list := range owners {
		slices.Sort(list)
	}
	return owners
}

// abstainUnless is a declaration of orders that abstains on unread, when set.
func abstainUnless(unread UnreadFact, orders ...OrderSpec) Declared {
	out := Declared{Orders: orders}
	if unread != "" {
		out.Unread(unread)
	}
	return out
}

// unreadIf is fact when unread holds, else empty.
func unreadIf(unread bool, fact UnreadFact) UnreadFact {
	if unread {
		return fact
	}
	return ""
}
