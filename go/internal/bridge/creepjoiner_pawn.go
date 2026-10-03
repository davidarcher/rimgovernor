package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Downsides is what the catalog's creepjoiner downside defs add: the trait
// and hediff def names over every downside def (#1740). A nil catalog (the
// game has no Anomaly) adds none.
func (c *AnomalyCatalog) Downsides() policy.CreepJoinerDownsides {
	out := policy.CreepJoinerDownsides{Traits: map[string]bool{}, Hediffs: map[string]bool{}}
	if c == nil {
		return out
	}
	for _, row := range c.CreepJoinerDownsides {
		for _, t := range row.Traits {
			out.Traits[t] = true
		}
		for _, h := range row.Hediffs {
			out.Hediffs[h] = true
		}
	}
	return out
}

// CreepJoinerDownsides is the catalog's downside defs; none without a catalog
// or without Anomaly.
func (catalog *DefinitionCatalog) CreepJoinerDownsides() policy.CreepJoinerDownsides {
	if catalog == nil {
		return (*AnomalyCatalog)(nil).Downsides()
	}
	return catalog.Anomaly.Downsides()
}

// CreepJoinerPawn lifts a combat-detail pawn row into the downside check's
// facts (#1740). A row with no Anomaly block is a game without Anomaly: no
// creepjoiner. A failed creepjoiner read stays unknown. The hediffs are the
// visible ones, known only from a complete hediff read.
func CreepJoinerPawn(row *o.PawnState) policy.CreepJoinerPawn {
	out := policy.CreepJoinerPawn{CreepJoiner: domain.Known[*policy.CreepJoiner](nil), Traits: domain.Unknown[[]string](), Hediffs: domain.Unknown[[]string]()}
	if row.GetAnomaly() != nil {
		anomaly, _ := PawnAnomaly(row.Anomaly).Value()
		out.CreepJoiner = anomaly.CreepJoiner
	}
	if b := row.Biography; b != nil && !failedFields(b.Issues)["traits"] {
		traits := []string{}
		for _, t := range b.Traits {
			traits = append(traits, t.GetDefName())
		}
		out.Traits = domain.Known(traits)
	}
	if h := row.Health; h != nil && !failedFields(row.Issues)["health"] && !failedFields(h.Issues)["hediffs"] && h.HediffCompleteness != nil && h.GetHediffCompleteness().GetFiltered() == 0 {
		hediffs, known := []string{}, true
		for _, v := range h.Hediffs {
			if v.Visible == nil {
				known = false
				break
			}
			if v.GetVisible() {
				hediffs = append(hediffs, v.GetDefName())
			}
		}
		if known {
			out.Hediffs = domain.Known(hediffs)
		}
	}
	return out
}

// CreepJoinerHand lifts a combat-detail pawn row into the weapon drop's
// facts (#1740): the downside check's facts, whether the colonist can take an
// order (alive, standing, undrafted, out of a mental state) and the weapon in
// its hands, its surgery operations and bills.
func CreepJoinerHand(row *o.PawnState) policy.CreepJoinerHand {
	hand := policy.CreepJoinerHand{Pawn: policy.PawnID(row.GetPawn().GetId()), Facts: CreepJoinerPawn(row), Available: domain.Unknown[bool](), Weapon: domain.Unknown[string](),
		Operations: domain.Unknown[[]policy.SurgeryOperation](), QueuedSurgeries: domain.Unknown[int]()}
	if h := row.Health; h != nil && !failedFields(row.Issues)["health"] {
		// The weapon drop and inspections read recipe names only, no part items.
		_, hand.Operations, _ = SurgeryFacts(h, nil)
		hand.QueuedSurgeries = QueuedSurgeries(h)
		hand.QueuedRecipes = QueuedSurgeryRecipes(h)
	}
	mental := CellPresence(row.MentalState, row.Issues, "mental_state", false)
	if row.GetDead() || row.GetDowned() || row.GetDrafted() {
		hand.Available = domain.Known(false)
	} else if m, known := mental.Value(); known && row.Dead != nil && row.Downed != nil && row.Drafted != nil {
		hand.Available = domain.Known(!m)
	}
	if e := row.Equipment; e != nil && e.Armed != nil && !failedFields(e.Issues)["armed"] {
		switch {
		case !e.GetArmed():
			hand.Weapon = domain.Known("")
		case e.PrimaryId != nil:
			hand.Weapon = domain.Known(e.GetPrimaryId())
		}
	}
	return hand
}
