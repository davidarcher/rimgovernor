package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"slices"
	"sort"
)

type QuestGravInspectionWork struct {
	Quest   domain.QuestID
	Service *domain.RecoveryService
	Waiting bool
	Reason  QuestSkipReason
}

func SelectQuestGravInspection(f RoundsFacts, home domain.MapID) (QuestGravInspectionWork, error) {
	offers, known := f.QuestOffers.Value()
	if !known {
		return QuestGravInspectionWork{}, nil
	}
	offers = slices.Clone(offers)
	sort.Slice(offers, func(i, j int) bool { return offers[i].Quest < offers[j].Quest })
	for _, offer := range offers {
		if offer.State != "Ongoing" {
			continue
		}
		for _, objective := range offer.Objectives {
			if objective.Kind == o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_INSPECT_GRAV_ENGINE {
				if _, known := objective.GravEngine.Value(); !known {
					return QuestGravInspectionWork{Quest: offer.Quest, Waiting: true}, nil
				}
			}
			engine, known := objective.GravEngine.Value()
			if !known {
				continue
			}
			result := QuestGravInspectionWork{Quest: offer.Quest, Waiting: true}
			inspected, ik := engine.Inspected.Value()
			if !ik {
				result.Reason = "inspection_unknown"
				return result, nil
			}
			if inspected {
				continue
			}
			spawned, sk := engine.Spawned.Value()
			if !sk || !spawned {
				return result, nil
			}
			if engine.Map != home {
				result.Reason = "off_map"
				return result, nil
			}
			if len(engine.InspectingPawnIDs) > 0 {
				return result, nil
			}
			candidates := slices.Clone(engine.EligiblePawns)
			slices.Sort(candidates)
			if len(candidates) == 0 {
				result.Reason = "inspector_unavailable"
				return result, nil
			}
			service, err := domain.NewRecoveryService(candidates[0], engine.ID, domain.RecoveryServiceInspectGravEngine)
			result.Service = &service
			result.Waiting = false
			return result, err
		}
	}
	return QuestGravInspectionWork{}, nil
}

func GravInspectionDeficit(f RoundsFacts) bool {
	rows, known := f.QuestOffers.Value()
	if !known {
		return false
	}
	for _, offer := range rows {
		if offer.State != "Ongoing" {
			continue
		}
		for _, objective := range offer.Objectives {
			if objective.Kind == o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_INSPECT_GRAV_ENGINE {
				if _, known := objective.GravEngine.Value(); !known {
					return true
				}
			}
			if engine, known := objective.GravEngine.Value(); known {
				if inspected, known := engine.Inspected.Value(); !known || !inspected {
					return true
				}
			}
		}
	}
	return false
}
