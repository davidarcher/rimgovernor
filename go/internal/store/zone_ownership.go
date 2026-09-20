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
// The claim's ID is the native zone identity the completion receipt returned
// (the zone's unique load id), the same form the Home coverage census names
// stockpiles by (#315); a completion recorded without one owns nothing.
// The link query keeps only plans with an observed zone_create action, as in
// constructionClaims.
func stockpileClaims(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, tick domain.Tick) (domain.Fact[[]policy.OwnedStockpile], error) {
	zones, err := zoneClaims(ctx, tx, current, tick)
	if err != nil {
		return domain.Unknown[[]policy.OwnedStockpile](), err
	}
	owned, known := zones.Value()
	if !known {
		return domain.Unknown[[]policy.OwnedStockpile](), nil
	}
	result := []policy.OwnedStockpile{}
	for _, z := range owned {
		if z.Kind == domain.StockpileZone {
			result = append(result, policy.OwnedStockpile{ID: z.ID, Cells: z.Cells})
		}
	}
	return domain.Known(result), nil
}

// OwnedZone is one zone this colony created, by native zone identity:
// the completed zone_create's kind, crop and cells.
type OwnedZone struct {
	ID    string
	Kind  domain.ZoneKind
	Crop  string
	Cells []domain.Cell
}

// zoneClaims lists every completed autopilot zone_create of the current
// world scope by the native zone identity its receipt returned; the layout
// tidy (#611) treats only these zones as managed.
func zoneClaims(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, tick domain.Tick) (domain.Fact[[]OwnedZone], error) {
	unknown := domain.Unknown[[]OwnedZone]()
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
	result := []OwnedZone{}
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
			id, known := v.Zone.Value()
			if !isZone || !ek || !known || effect != domain.EffectCompleted || v.Stage != domain.Completed || v.Tick > tick || v.Snapshot.Colony != current.Colony || v.Snapshot.Load != current.Load || v.Snapshot.Map != current.Map {
				continue
			}
			result = append(result, OwnedZone{ID: id, Kind: zone.Kind(), Crop: zone.Crop(), Cells: zone.Cells()})
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

// ZoneClaims exposes zoneClaims outside the routine review transaction:
// every zone this colony created in the current world scope.
func (s *Store) ZoneClaims(ctx context.Context, current domain.GenerationSnapshot, tick domain.Tick) (domain.Fact[[]OwnedZone], error) {
	if current.Validate() != nil || tick < 0 {
		return domain.Unknown[[]OwnedZone](), ErrConflict
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.Unknown[[]OwnedZone](), err
	}
	defer tx.Rollback()
	result, err := zoneClaims(ctx, tx, current, tick)
	if err != nil {
		return result, err
	}
	return result, tx.Commit()
}
