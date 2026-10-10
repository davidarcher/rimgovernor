package bridge

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The AbilityDef, RoyalTitleDef, ThingDef, BiomeDef and IncidentDef rows behind
// the royalty and policy facts the native read used to copy beside a pawn or
// colony: a colonist's psycasts name their ability and the rest is the row.

const (
	psyfocusCostStat = "Ability_PsyfocusCost"
	entropyGainStat  = "Ability_EntropyGain"
)

// abilityStat is the named stat of an AbilityDef's statBases, 0 when it lists none.
func abilityStat(row *d.AbilityDef, stat string) float64 {
	for _, mod := range row.GetStatBases() {
		if mod.GetValue().GetStat() == stat {
			return decimal32(mod.GetValue().GetValue())
		}
	}
	return 0
}

// Psycast is the ability row's static half of a policy.Psycast: the psylink
// level that unlocks it, the psyfocus it spends (statBases
// Ability_PsyfocusCost), the neural heat it adds (Ability_EntropyGain), the
// longest cooldown (cooldownTicksRange.max) and what it targets (the verb's
// targetParams). A def with no row, or a row naming a value out of range, is an
// error.
func (catalog *DefinitionCatalog) Psycast(name string) (policy.Psycast, error) {
	row := DefRow[*d.AbilityDef](catalog, name)
	if row == nil {
		return policy.Psycast{}, contract("catalog has no ability row for %s", name)
	}
	cost, entropy, cooldown := abilityStat(row, psyfocusCostStat), abilityStat(row, entropyGainStat), row.GetCooldownTicksRange().GetMax()
	if cost < 0 || cost > 1 || entropy < 0 || cooldown < 0 || row.GetLevel() < 0 {
		return policy.Psycast{}, contract("ability %s has an invalid psycast row", name)
	}
	return policy.Psycast{Def: name, Level: domain.Known(int(row.GetLevel())), PsyfocusCost: domain.Known(cost), Entropy: domain.Known(entropy),
		Target: psycastTarget(row.GetVerbProperties().GetTargetParams()), CooldownTicks: domain.Known(int(cooldown))}, nil
}

// psycastTarget is the kind of target the verb's targetParams allows: the
// caster alone, a pawn, a thing (building or item), a cell, or "" when none.
func psycastTarget(t *d.TargetingParameters) policy.PsycastTarget {
	switch {
	case t.GetCanTargetSelf() && !t.GetCanTargetPawns() && !t.GetCanTargetLocations() && !t.GetCanTargetBuildings() && !t.GetCanTargetItems():
		return policy.PsycastTargetSelf
	case t.GetCanTargetPawns():
		return policy.PsycastTargetPawn
	case t.GetCanTargetBuildings() || t.GetCanTargetItems():
		return policy.PsycastTargetThing
	case t.GetCanTargetLocations():
		return policy.PsycastTargetCell
	}
	return ""
}

// WithNeuroformerDefs is c with each neuroformer's trainer ability (the
// ability its CompProperties_UseEffect_GainAbility teaches) and tradeability
// (a trader can sell a Sellable or All thing) read from its ThingDef row. c is
// not changed; a neuroformer with no row is an error.
func (catalog *DefinitionCatalog) WithNeuroformerDefs(c policy.RoyaltyColony) (policy.RoyaltyColony, error) {
	out := c
	out.Neuroformers = make(map[string]policy.Neuroformer, len(c.Neuroformers))
	for name, n := range c.Neuroformers {
		row, err := catalog.thingRow(name)
		if err != nil {
			return policy.RoyaltyColony{}, err
		}
		n.TeachesPsycast = ""
		if gain := compOf(row, (*d.CompPropertiesAny).GetCompProperties_UseEffect_GainAbility); gain != nil {
			n.TeachesPsycast = gain.GetAbility()
		}
		n.Tradeable = domain.Known(row.GetTradeability() == d.Tradeability_TRADEABILITY_SELLABLE || row.GetTradeability() == d.Tradeability_TRADEABILITY_ALL)
		out.Neuroformers[name] = n
	}
	return out, nil
}

// RoyalTitleOf is the bedroom picture of a holder's most senior title: the
// RoyalTitleDef row's seniority and bedroom requirements, less those the
// holder waives (an ascetic has none; a requirement whose disablingPrecepts
// the holder's ideoligion holds is dropped). A title with no row is an error.
func (catalog *DefinitionCatalog) RoyalTitleOf(title string, ascetic bool, precepts []string) (*policy.RoyalTitle, error) {
	row := DefRow[*d.RoyalTitleDef](catalog, title)
	if row == nil {
		return nil, contract("catalog has no royal title row for %s", title)
	}
	out := &policy.RoyalTitle{Definition: title, Seniority: int(row.GetSeniority())}
	if ascetic {
		return out, nil
	}
	req, err := bedroomRequirements(row, func(disabling []string) bool {
		return slices.ContainsFunc(disabling, func(p string) bool { return slices.Contains(precepts, p) })
	})
	if err != nil {
		return nil, err
	}
	out.BedroomMinArea, out.BedroomMinImpressiveness, out.BedroomFloored, out.BedroomThings = req.area, req.impressiveness, req.floored, req.things
	return out, nil
}

// BiomeDiseases is the hediffs of the named biome's disease incidents: each
// BiomeDef.diseases record with commonality, resolved through its IncidentDef's
// diseaseIncident, sorted and unique. A biome or incident with no row is an
// error; a nil catalog has none.
func (catalog *DefinitionCatalog) BiomeDiseases(biome string) ([]string, error) {
	if catalog == nil {
		return nil, nil
	}
	row := DefRow[*d.BiomeDef](catalog, biome)
	if row == nil {
		return nil, contract("catalog has no biome row for %s", biome)
	}
	var hediffs []string
	for _, rec := range row.GetDiseases() {
		if rec.GetValue().GetCommonality() <= 0 {
			continue
		}
		incident := DefRow[*d.IncidentDef](catalog, rec.GetValue().GetDiseaseInc())
		if incident == nil {
			return nil, contract("catalog has no incident row for %s", rec.GetValue().GetDiseaseInc())
		}
		if incident.GetDiseaseIncident() != "" {
			hediffs = append(hediffs, incident.GetDiseaseIncident())
		}
	}
	slices.Sort(hediffs)
	return slices.Compact(hediffs), nil
}

// RefuelFuels is the fuel the named def's CompProperties_Refuelable accepts
// (fuelFilter.thingDefs less disallowedThingDefs), sorted; none for a def
// without the comp or a nil catalog. A filter that lists categories is an
// error: the row does not expand them.
func (catalog *DefinitionCatalog) RefuelFuels(name string) ([]string, error) {
	if catalog == nil {
		return nil, nil
	}
	row, err := catalog.thingRow(name)
	if err != nil {
		return nil, err
	}
	fuel := compOf(row, (*d.CompPropertiesAny).GetCompProperties_Refuelable)
	if fuel == nil {
		return nil, nil
	}
	filter := fuel.GetFuelFilter()
	if len(filter.GetCategories()) > 0 {
		return nil, contract("def %s fuel filter lists categories", name)
	}
	var out []string
	for _, def := range filter.GetThingDefs() {
		if !slices.Contains(filter.GetDisallowedThingDefs(), def) {
			out = append(out, def)
		}
	}
	slices.Sort(out)
	return out, nil
}
