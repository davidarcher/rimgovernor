package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func colonyDisaster(v *o.ColonyFactsSnapshot, facts *policy.RoutineFacts) {
	if !hasIssue(v.Issues, "environment") {
		conditions := make([]policy.DisasterCondition, 0, len(v.Environment))
		for _, row := range v.Environment {
			conditions = append(conditions, policy.DisasterCondition{ID: row.GetId(), Definition: row.GetDefName()})
		}
		facts.DisasterConditions = domain.Known(conditions)
	}
	facts.DisasterTick = domain.Tick(v.Context.GetTick())
	if recovery := v.GetRecovery().GetObserved(); recovery != nil {
		buildings := make([]policy.RecoveryBuilding, 0, len(recovery.Buildings))
		for _, row := range recovery.Buildings {
			s := row.Service
			b := policy.RecoveryBuilding{ID: row.Building.GetId(), UsesHitPoints: optional(row.UsesHitPoints), Broken: optional(s.BrokenDown), Forbidden: optional(row.Settings.Forbidden), Burning: optional(row.Burning), Fuel: optional(s.Fuel), FuelTarget: optional(s.TargetFuel)}
			if row.HitPoints != nil {
				b.HitPoints = domain.Known(int64(row.GetHitPoints()))
			}
			if row.MaxHitPoints != nil {
				b.MaxHitPoints = domain.Known(int64(row.GetMaxHitPoints()))
			}
			if s.Fuel != nil || s.TargetFuel != nil {
				b.Refuelable = domain.Known(true)
			}
			for _, issue := range s.Issues {
				if issue.GetField() == "fuel" && issue.GetUnavailable().GetReason() == c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE {
					b.Refuelable = domain.Known(false)
				}
			}
			buildings = append(buildings, b)
		}
		facts.RecoveryBuildings = domain.Known(buildings)
	}
}
