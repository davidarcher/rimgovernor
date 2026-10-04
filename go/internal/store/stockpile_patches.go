package store

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// AppliedStockpile is the last completed autopilot stockpile_patch on one
// storage target (a zone, or a storage building such as a shelf): the
// settings (and role, when the patch carried one) the target holds
// since, as of Tick. On a zone it supersedes the zone_create's settings in
// OwnedZone.
type AppliedStockpile struct {
	Target   string
	Kind     domain.StorageTargetKind
	Filter   domain.StockpileFilter
	Priority domain.StockpilePriority
	Role     string
	Tick     domain.Tick
}

// StockpilePatches lists, per target id, the latest completed autopilot
// stockpile_patch of the current world scope at or before tick, zones and
// storage buildings alike.
func (s *Store) StockpilePatches(ctx context.Context, current domain.GenerationSnapshot, tick domain.Tick) (map[string]AppliedStockpile, error) {
	if current.Validate() != nil || tick < 0 {
		return nil, ErrConflict
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	result, err := stockpilePatches(ctx, tx, current, tick)
	if err != nil {
		return nil, err
	}
	return result, tx.Commit()
}

func stockpilePatches(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, tick domain.Tick) (map[string]AppliedStockpile, error) {
	rows, err := tx.QueryContext(ctx, `SELECT m.plan_id,COALESCE(m.goal_id,m.project_id) FROM goal_methods m LEFT JOIN goals g ON g.id=m.goal_id LEFT JOIN projects pr ON pr.id=m.project_id
 WHERE json_extract(COALESCE(g.payload,pr.payload),'$.Snapshot.Colony')=?
 AND json_extract(COALESCE(g.payload,pr.payload),'$.Snapshot.Load')=? AND json_extract(COALESCE(g.payload,pr.payload),'$.Snapshot.Map')=?
 AND EXISTS(SELECT 1 FROM actions a JOIN transitions t ON t.action_id=a.id WHERE a.plan_id=m.plan_id AND a.kind='stockpile_patch' AND json_extract(t.payload,'$.Kind') IN ('observe','receipt'))
 ORDER BY m.plan_id LIMIT 1025`, current.Colony, current.Load, current.Map)
	if err != nil {
		return nil, err
	}
	var plans []domain.PlanID
	for rows.Next() {
		var plan domain.PlanID
		var goal domain.GoalID
		if err = rows.Scan(&plan, &goal); err != nil {
			rows.Close()
			return nil, err
		}
		plans = append(plans, plan)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return nil, err
	}
	result := map[string]AppliedStockpile{}
	for _, id := range plans {
		plan, err := load(ctx, tx, id)
		if err != nil {
			return nil, err
		}
		for _, progress := range plan.Progress {
			v := progress.View()
			patch, isPatch := progress.Action().StockpilePatch()
			effect, ek := v.Effect.Value()
			if !isPatch || !ek || effect != domain.EffectCompleted || v.Stage != domain.Completed || v.Tick > tick || v.Snapshot.Colony != current.Colony || v.Snapshot.Load != current.Load || v.Snapshot.Map != current.Map {
				continue
			}
			if prior, seen := result[patch.Target()]; seen && prior.Tick > v.Tick {
				continue
			}
			result[patch.Target()] = AppliedStockpile{Target: patch.Target(), Kind: patch.TargetKind(), Filter: patch.Filter(), Priority: patch.Priority(), Role: patch.Role(), Tick: v.Tick}
		}
	}
	return result, nil
}
