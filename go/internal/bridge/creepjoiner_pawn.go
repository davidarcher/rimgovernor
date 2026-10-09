package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// CreepJoinerPawn lifts a combat-detail pawn row into the downside check's
// facts. A row with no Anomaly block is a game without Anomaly: no
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
// facts: the downside check's facts, whether the colonist can take an
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
	// An unset allowed area is an unrestricted pawn (native AllowedArea).
	hand.Area, hand.Hungry = domain.Unknown[string](), domain.Unknown[bool]()
	if s := row.Settings; s != nil && !failedFields(s.Issues)["allowed_area_id"] {
		hand.Area = domain.Known(s.GetAllowedAreaId())
	}
	if n := row.Needs; n != nil && n.HungerCategory != nil && n.GetHungerCategory() != o.HungerCategory_HUNGER_CATEGORY_UNSPECIFIED && !failedFields(n.Issues)["hunger_category"] {
		hand.Hungry = domain.Known(n.GetHungerCategory() != o.HungerCategory_HUNGER_CATEGORY_FED)
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
