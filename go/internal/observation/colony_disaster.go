package observation

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// colonyConditions are the frame's active game conditions, each with what its
// def row says (a condition the catalog has no row for is an error); known is
// false when the frame could not read the environment.
func colonyConditions(v *o.ColonyFactsSnapshot, catalog *bridge.DefinitionCatalog) (conditions []policy.DisasterCondition, known bool, err error) {
	if hasIssue(v.Issues, "environment") {
		return nil, false, nil
	}
	conditions = make([]policy.DisasterCondition, 0, len(v.Environment))
	for _, row := range v.Environment {
		condition := policy.DisasterCondition{ID: row.GetId(), Definition: row.GetDefName(), Permanent: row.GetPermanent()}
		if condition.DisablesPower, err = catalog.DisablesElectricity(condition.Definition); err != nil {
			return nil, false, err
		}
		if row.TicksLeft != nil && !condition.Permanent && row.GetTicksLeft() >= 0 {
			condition.TicksLeft = proto.Int64(row.GetTicksLeft())
		}
		conditions = append(conditions, condition)
	}
	return conditions, true, nil
}

// colonyOutdoorsDark is whether the frame's biome keeps the sky dark for good
// (its map conditions' class family, #1712). A frame with no biome, or no
// catalog to read its conditions from, is an error naming which is missing, as
// is a biome the catalog has no row for: lit is never assumed.
func colonyOutdoorsDark(v *o.ColonyFactsSnapshot, catalog *bridge.DefinitionCatalog) (domain.Fact[bool], error) {
	if v.Biome == nil {
		return domain.Unknown[bool](), fmt.Errorf("%w: the colony frame carried no biome", policy.ErrOutdoorsDarkUnknown)
	}
	if catalog == nil {
		return domain.Unknown[bool](), fmt.Errorf("%w: no definition catalog was loaded", policy.ErrOutdoorsDarkUnknown)
	}
	dark, err := catalog.OutdoorsPermanentlyDark(v.GetBiome())
	if err != nil {
		return domain.Unknown[bool](), err
	}
	return domain.Known(dark), nil
}

func colonyDisaster(v *o.ColonyFactsSnapshot, facts *policy.RoundsFacts, buildings bridge.Buildings, conditions []policy.DisasterCondition, conditionsKnown bool) {
	if conditionsKnown {
		facts.DisasterConditions = domain.Known(conditions)
	}
	facts.DisasterTick = domain.Tick(v.Context.GetTick())
	if recovery := v.GetRecovery().GetObserved(); recovery != nil && resolved(buildings, recovery.Buildings, func(ref *c.Ref) *c.Ref { return ref }) {
		safety := policy.RecoverySafety{}
		for _, restriction := range recovery.Restrictions {
			safety.Restrictions = append(safety.Restrictions, policy.RecoveryRestriction{Pawn: policy.PawnID(restriction.Pawn.GetId()), Area: domain.Known(restriction.GetAreaId())})
		}
		facts.RecoverySafety = domain.Known(safety)
		rows := make([]policy.RecoveryBuilding, 0, len(recovery.Buildings))
		for _, ref := range recovery.Buildings {
			row, _ := buildings.Row(ref)
			s := row.Service
			b := policy.RecoveryBuilding{ID: ref.GetId(), UsesHitPoints: optional(row.UsesHitPoints), Broken: optional(s.BrokenDown), Forbidden: optional(row.Settings.Forbidden), Burning: optional(row.Burning), Fuel: optional(s.Fuel), FuelTarget: optional(s.TargetFuel)}
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
			rows = append(rows, b)
		}
		facts.RecoveryBuildings = domain.Known(rows)
	}
}

func recoveryWorkers(pawns domain.Fact[[]policy.MoodPawn]) domain.Fact[[]policy.RecoveryWorker] {
	rows, known := pawns.Value()
	if !known {
		return domain.Unknown[[]policy.RecoveryWorker]()
	}
	workers := make([]policy.RecoveryWorker, 0, len(rows))
	for _, p := range rows {
		workers = append(workers, policy.RecoveryWorker{Pawn: p.ID, Dead: p.Dead, Downed: p.Downed, Drafted: p.Drafted, Mental: p.Mental, PlayerForced: p.PlayerForced})
	}
	return domain.Known(workers)
}
