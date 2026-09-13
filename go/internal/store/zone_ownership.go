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
func stockpileClaims(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, tick domain.Tick) (domain.Fact[[]policy.OwnedStockpile], error) {
	unknown := domain.Unknown[[]policy.OwnedStockpile]()
	rows, err := tx.QueryContext(ctx, `SELECT m.plan_id,m.goal_id FROM goal_methods m JOIN goals g ON g.id=m.goal_id
 WHERE json_extract(g.payload,'$.Source')=? AND json_extract(g.payload,'$.Snapshot.Colony')=?
 AND json_extract(g.payload,'$.Snapshot.Load')=? AND json_extract(g.payload,'$.Snapshot.Map')=?
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
	for _, link := range links {
		g, err := loadGoal(ctx, tx, link.goal)
		if err != nil {
			return unknown, err
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
