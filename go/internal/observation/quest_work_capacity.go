package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func frameQuestWorkerCapacity(read *bridge.WorldProgressionRead, home domain.MapID) (domain.Fact[[]policy.QuestWorkCapacity], domain.Fact[[]policy.QuestDeparturePawn]) {
	unknown := func() (domain.Fact[[]policy.QuestWorkCapacity], domain.Fact[[]policy.QuestDeparturePawn]) {
		return domain.Unknown[[]policy.QuestWorkCapacity](), domain.Unknown[[]policy.QuestDeparturePawn]()
	}
	if read == nil {
		return unknown()
	}
	for _, m := range read.Maps {
		if !m.Home || domain.MapID(m.ID) != home {
			continue
		}
		if len(m.QuestWorkers) != len(m.PawnIDs) {
			return unknown()
		}
		capacity := []policy.QuestWorkCapacity{}
		departures := []policy.QuestDeparturePawn{}
		present := map[string]bool{}
		for _, id := range m.PawnIDs {
			present[id] = true
		}
		for _, p := range m.QuestWorkers {
			if !present[p.GetPawnId()] {
				return unknown()
			}
			row := policy.QuestWorkCapacity{Pawn: policy.PawnID(p.GetPawnId()), HealthyAdult: optional(p.HealthyAdult), Rates: map[string]float64{}}
			for _, rate := range p.Rates {
				row.Rates[rate.GetStat()] = rate.GetRate()
			}
			capacity = append(capacity, row)
			departures = append(departures, policy.QuestDeparturePawn{ID: row.Pawn, HealthyAdult: row.HealthyAdult, CanFight: optional(p.CanFight), CarryCapacity: optional(p.CarryCapacity), CarriedMass: optional(p.CarriedMass)})
		}
		return domain.Known(capacity), domain.Known(departures)
	}
	return unknown()
}

func questWorkload(w *o.QuestWorkload, kind o.QuestObjectiveKind, catalog *bridge.DefinitionCatalog) domain.Fact[policy.QuestWorkload] {
	if w == nil {
		return domain.Unknown[policy.QuestWorkload]()
	}
	work := policy.WorkPlantCutting
	if kind == o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_PRODUCE_ITEM {
		if catalog == nil {
			return domain.Unknown[policy.QuestWorkload]()
		}
		facts, err := catalog.RecipeFacts()
		if err != nil {
			return domain.Unknown[policy.QuestWorkload]()
		}
		var known bool
		work, known = facts.BillWorkOf(w.GetRecipe())
		if !known {
			return domain.Unknown[policy.QuestWorkload]()
		}
	}
	return domain.Known(policy.QuestWorkload{Work: work, Stat: w.GetStat(), Amount: w.GetWork(), RateFactor: w.GetRateFactor()})
}
