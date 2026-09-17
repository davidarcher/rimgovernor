package store

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// stockpileClaims mirrors constructionClaims: it joins durable autopilot goal
// methods for the current world scope to find completed ZoneCreate actions of
// kind StockpileZone, so ReviewHomeCoverage can protect player edits to a zone
// this colony already created instead of silently reclaiming or recreating it.
// The link query keeps only plans with an observed zone_create action, as in
// constructionClaims.
func stockpileClaims(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, tick domain.Tick) (domain.Fact[[]policy.OwnedStockpile], error) {
	unknown := domain.Unknown[[]policy.OwnedStockpile]()
	rows, err := tx.QueryContext(ctx, `SELECT m.plan_id,m.goal_id FROM goal_methods m JOIN goals g ON g.id=m.goal_id
 WHERE json_extract(g.payload,'$.Source')=? AND json_extract(g.payload,'$.Snapshot.Colony')=?
 AND json_extract(g.payload,'$.Snapshot.Load')=? AND json_extract(g.payload,'$.Snapshot.Map')=?
 AND EXISTS(SELECT 1 FROM actions a JOIN transitions t ON t.action_id=a.id WHERE a.plan_id=m.plan_id AND a.kind='zone_create' AND json_extract(t.payload,'$.Kind')='observe')
 ORDER BY m.plan_id LIMIT 257`, domain.AutopilotGoal, current.Colony, current.Load, current.Map)
	if err != nil {
		return unknown, err
	}
	type link struct {
		plan domain.PlanID
		goal domain.GoalID
	}
	links := []link{}
	for rows.Next() {
		var v link
		if err = rows.Scan(&v.plan, &v.goal); err != nil {
			rows.Close()
			return unknown, err
		}
		links = append(links, v)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return unknown, err
	}
	if len(links) > 256 {
		return unknown, nil
	}
	result := []policy.OwnedStockpile{}
	goals := map[domain.GoalID]GoalState{}
	for _, link := range links {
		g, cached := goals[link.goal]
		if !cached {
			if g, err = loadGoal(ctx, tx, link.goal); err != nil {
				return unknown, err
			}
			goals[link.goal] = g
		}
		scope := g.Goal.Snapshot
		if g.Goal.Source != domain.AutopilotGoal || g.Goal.Status == domain.GoalCancelled || scope.Colony != current.Colony || scope.Load != current.Load || scope.Map != current.Map {
			continue
		}
		plan, err := load(ctx, tx, link.plan)
		if err != nil {
			return unknown, err
		}
		for _, progress := range plan.Progress {
			v := progress.View()
			zone, isZone := progress.Action().ZoneCreate()
			effect, ek := v.Effect.Value()
			if !isZone || zone.Kind() != domain.StockpileZone || !ek || effect != domain.EffectCompleted || v.Stage != domain.Completed || v.Tick > tick || v.Snapshot.Colony != current.Colony || v.Snapshot.Load != current.Load || v.Snapshot.Map != current.Map {
				continue
			}
			result = append(result, policy.OwnedStockpile{ID: string(v.Action), Cells: zone.Cells()})
			if len(result) > 256 {
				return unknown, nil
			}
		}
	}
	return domain.Known(result), nil
}

// StockpileClaims exposes stockpileClaims outside the routine review
// transaction, mirroring Store.ConstructionClaims, so a routine scheduler can
// re-derive a fresh HomeCoverage target list on its own tick.
func (s *Store) StockpileClaims(ctx context.Context, current domain.GenerationSnapshot, tick domain.Tick) (domain.Fact[[]policy.OwnedStockpile], error) {
	if current.Validate() != nil || tick < 0 {
		return domain.Unknown[[]policy.OwnedStockpile](), ErrConflict
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.Unknown[[]policy.OwnedStockpile](), err
	}
	defer tx.Rollback()
	result, err := stockpileClaims(ctx, tx, current, tick)
	if err != nil {
		return result, err
	}
	return result, tx.Commit()
}
