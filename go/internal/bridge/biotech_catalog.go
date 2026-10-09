package bridge

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// MechCatalog is the catalog's mech kinds and the work modes the game names
// by role as the mech planner reads them; the zero value without
// Biotech.
func (c *BiotechCatalog) MechCatalog() policy.MechCatalog {
	out := policy.MechCatalog{Kinds: map[string]policy.MechKind{}}
	if c == nil {
		return out
	}
	for name, row := range c.mechKinds {
		kind := policy.MechKind{Name: name, WorkMech: row.workMech, BandwidthCost: row.bandwidthCost, CombatPower: row.combatPower}
		for _, w := range row.workTypes {
			kind.WorkTypes = append(kind.WorkTypes, policy.WorkType(w))
		}
		out.Kinds[name] = kind
	}
	out.Work, out.Escort, out.Recharge = c.work, c.escort, c.recharge
	return out
}

// mechKindOfRace is the name of the one mech kind whose race is race, "" when
// there is none; several kinds of one race are a contract failure.
func (c *BiotechCatalog) mechKindOfRace(race string) (string, error) {
	if c == nil {
		return "", nil
	}
	var kinds []string
	for name, row := range c.mechKinds {
		if row.race == race {
			kinds = append(kinds, name)
		}
	}
	slices.Sort(kinds)
	if len(kinds) > 1 {
		return "", contract("recipe produces race %s, which has several mech kinds %v", race, kinds)
	}
	if len(kinds) == 0 {
		return "", nil
	}
	return kinds[0], nil
}

// GeneEffects resolves the active genes of a pawn into their combined typed
// effects from the catalog's gene rows. A gene the catalog does not define is
// a contract failure, never skipped.
func (c *BiotechCatalog) GeneEffects(genes []policy.PawnGene) (policy.GeneEffects, error) {
	out := policy.GeneEffects{Stats: map[string]policy.StatModifier{}, DisabledNeeds: map[string]bool{}, EnabledNeeds: map[string]bool{}}
	for _, g := range genes {
		if active, ok := g.Active.Value(); ok && !active {
			continue
		}
		var row *d.GeneDef
		if c != nil {
			row = c.Genes[g.Name]
		}
		if row == nil {
			return policy.GeneEffects{}, contract("pawn gene %s is not in the biotech catalog", g.Name)
		}
		for _, e := range row.GetStatFactors() {
			m, seen := out.Stats[e.GetValue().GetStat()]
			if !seen {
				m.Factor = 1
			}
			m.Factor *= float64(e.GetValue().GetValue())
			out.Stats[e.GetValue().GetStat()] = m
		}
		for _, e := range row.GetStatOffsets() {
			m, seen := out.Stats[e.GetValue().GetStat()]
			if !seen {
				m.Factor = 1
			}
			m.Offset += float64(e.GetValue().GetValue())
			out.Stats[e.GetValue().GetStat()] = m
		}
		for _, n := range row.GetDisablesNeeds() {
			out.DisabledNeeds[n] = true
		}
		for _, n := range row.GetEnablesNeeds() {
			out.EnabledNeeds[n] = true
		}
	}
	return out, nil
}

// WorkMinAges is the race's minimum age in years per work type from the
// catalog. A race the catalog lacks is a contract failure: a child
// must never be left unrestricted for want of data.
func (c *BiotechCatalog) WorkMinAges(race string) (map[policy.WorkType]int, error) {
	var row *d.RaceProperties
	if c != nil {
		row = c.races[race]
	}
	if row == nil {
		return nil, contract("child race %s is not in the biotech catalog", race)
	}
	out := make(map[policy.WorkType]int, len(row.GetLifeStageWorkSettings()))
	for _, w := range row.GetLifeStageWorkSettings() {
		out[policy.WorkType(w.GetValue().GetWorkType())] = int(w.GetValue().GetMinAge())
	}
	return out, nil
}
