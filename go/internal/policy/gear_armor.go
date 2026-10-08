package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ArmorResearchRungs is the armor ladder a colony with a soldier walks
// right after Electricity (#470): the simple helmet's smithy, the tailoring
// bench flak cloth needs, flak armor itself, then shield belts.
var ArmorResearchRungs = []string{"Smithing", "ComplexClothing", "FlakArmor", "Shields"}

// ArmorResearchLadder is ladder with ArmorResearchRungs spliced in directly
// after Electricity (at the front when the ladder has no Electricity rung)
// while a soldier role exists; rungs the ladder already names move up
// rather than repeat. Without a soldier the ladder is returned as is.
func ArmorResearchLadder(ladder []string, soldier bool) []string {
	if !soldier {
		return ladder
	}
	armor := map[string]bool{}
	for _, rung := range ArmorResearchRungs {
		armor[rung] = true
	}
	out := []string{}
	inserted := false
	insert := func() {
		if !inserted {
			out = append(out, ArmorResearchRungs...)
			inserted = true
		}
	}
	for _, rung := range ladder {
		if armor[rung] {
			continue
		}
		out = append(out, rung)
		if rung == "Electricity" {
			insert()
		}
	}
	if !inserted {
		out = append(append([]string{}, ArmorResearchRungs...), out...)
	}
	return out
}

// ArmorResearchPolicy is p with its ResearchLadder extended by
// ArmorResearchLadder; an empty ladder (roadmap disabled) stays empty.
func ArmorResearchPolicy(p RoundsPolicy, soldier bool) RoundsPolicy {
	if len(p.ResearchLadder) == 0 {
		return p
	}
	p.ResearchLadder = ArmorResearchLadder(p.ResearchLadder, soldier)
	return p
}

// GearSoldierPresent reports whether the gear census derives a soldier role
// for any pawn (its apparel-policy role, or its loadout model's when one is
// supplied). Unknown census: no soldier.
func GearSoldierPresent(gear domain.Fact[GearObservation]) bool {
	v, known := gear.Value()
	if !known {
		return false
	}
	for _, p := range v.Pawns {
		if model, ok := p.LoadoutModel.Value(); ok && DeriveGearRole(model.Role) == GearSoldier {
			return true
		}
		if state, ok := p.Policy.Value(); ok && DeriveGearRole(state.Role) == GearSoldier {
			return true
		}
	}
	return false
}

// gearAvailable is the ingredient stock a gear bill may spend: the known
// supply census less each concurrent hold. Known reports which resources the
// census measured at all.
func gearAvailable(stock []Stock, holds []Amount) (available map[Resource]int64, known map[Resource]bool) {
	available = map[Resource]int64{}
	known = map[Resource]bool{}
	for _, s := range stock {
		if n, ok := s.Available.Value(); ok {
			available[s.Resource] = n
			known[s.Resource] = true
		}
	}
	for _, hold := range holds {
		available[hold.Resource] = max(0, available[hold.Resource]-hold.Count)
	}
	return available, known
}

// armoryArmorRung is one step of an armor family's ladder (#1205): the
// definition and the lowest armory tier that may craft it. Rungs run from
// the cheapest to the best; a family's need falls back down its rungs when
// the tier or the stock (plasteel, components) cannot fund the higher one.
type armoryArmorRung struct {
	Definition Resource
	Tier       ArmoryTier
}

// armoryArmorFamilies is the body-armor and helmet ladder the armory owns:
// flak -> recon -> marine, with the simple helmet at smithing.
var armoryArmorFamilies = [][]armoryArmorRung{
	{{"Apparel_FlakVest", ArmoryTierMachining}, {"Apparel_FlakJacket", ArmoryTierMachining}, {"Apparel_ArmorRecon", ArmoryTierFabrication}, {"Apparel_PowerArmor", ArmoryTierFabrication}},
	{{"Apparel_FlakPants", ArmoryTierMachining}},
	{{"Apparel_SimpleHelmet", ArmoryTierSmithing}, {"Apparel_AdvancedHelmet", ArmoryTierMachining}, {"Apparel_ArmorHelmetRecon", ArmoryTierFabrication}, {"Apparel_PowerArmorHelmet", ArmoryTierFabrication}},
}

// armoryArmorRungs is the rungs a need for definition may be crafted as
// under tier, best first: the need's own rung and every lower one the tier
// allows. ok is false for a definition outside the armory's ladder.
func armoryArmorRungs(definition Resource, tier ArmoryTier) (rungs []Resource, ok bool) {
	for _, family := range armoryArmorFamilies {
		for i, rung := range family {
			if rung.Definition != definition {
				continue
			}
			for j := i; j >= 0; j-- {
				if family[j].Tier <= tier {
					rungs = append(rungs, family[j].Definition)
				}
			}
			return rungs, true
		}
	}
	return nil, false
}

// ArmoryArmor reports whether definition is body armor or a helmet the
// armory crafts; the gear planner leaves its bills to the armory.
func ArmoryArmor(definition Resource) bool {
	_, ok := armoryArmorRungs(definition, ArmoryTierFabrication)
	return ok
}

// SelectArmoryArmorMethod proposes one demand-sized armor bill for the
// loadout gaps the gear model filled from a bill (#1205). Each need is
// capped at tier and falls back down its family's rungs until a bench
// recipe the stock (less r.Holds) funds covers it; a need no rung funds is
// skipped, so scarce plasteel drops marine to recon to flak instead of
// waiting on stock. A pending wear candidate defers the bill.
func SelectArmoryArmorMethod(r GearPlanningRequest, tier ArmoryTier) (GearMethod, error) {
	review, err := ReviewGear(r.Observation)
	if err != nil {
		return GearMethod{}, err
	}
	if _, known := review.Recovered.Value(); !known || tier == ArmoryTierUnknown {
		return GearMethod{Kind: GearUnknown}, nil
	}
	seen, err := gearSeen(r.Seen)
	if err != nil {
		return GearMethod{}, err
	}
	if err := validateGearProduction(nil, r); err != nil {
		return GearMethod{}, err
	}
	v, _ := r.Observation.Value()
	v = modeledGearObservation(v, review.Loadouts)
	demand := map[Resource]int{}
	for _, p := range v.Pawns {
		if candidates, _ := p.Candidates.Value(); len(candidates) > 0 {
			return GearMethod{Kind: GearBlocked}, nil
		}
		if p.Blocked {
			continue
		}
		needs, _ := p.Replacements.Value()
		for _, n := range needs {
			if ArmoryArmor(n.Definition) {
				demand[n.Definition]++
			}
		}
	}
	if len(demand) == 0 {
		return GearMethod{Kind: GearRecovered}, nil
	}
	definitions := make([]Resource, 0, len(demand))
	for d := range demand {
		definitions = append(definitions, d)
	}
	sort.Slice(definitions, func(i, j int) bool { return definitions[i] < definitions[j] })
	for _, d := range definitions {
		rungs, _ := armoryArmorRungs(d, tier)
		for _, rung := range rungs {
			needs := make([]gearNeed, demand[d])
			for i := range needs {
				needs[i] = gearNeed{GearPawn{Pawn: "armor-batch", Loadout: "colony"}, GearReplacement{Definition: rung, Reason: "armory"}}
			}
			choice, err := produceGear(needs, v, review, seen, r)
			if err != nil || choice.Kind != GearBlocked {
				return choice, err
			}
		}
	}
	return GearMethod{Kind: GearBlocked}, nil
}
