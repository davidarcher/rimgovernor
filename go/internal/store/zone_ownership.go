package store

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// OwnedZone is one zone this colony created, by native zone identity:
// the completed zone_create's kind, crop, cells and stockpile role key.
// Role is empty for a legacy role-less claim. Cells are the created
// footprint; a later zone_cell_edit keeps the id, so the claim survives it.
type OwnedZone struct {
	ID    string
	Kind  domain.ZoneKind
	Crop  string
	Cells []domain.Cell
	Role  string
	// Goal is the goal whose method created the zone; Filter and Priority
	// are the stockpile settings it was created with.
	Concern  domain.ConcernID
	Filter   domain.StockpileFilter
	Priority domain.StockpilePriority
}

// zoneClaims lists every completed autopilot zone_create of the current
// world scope by the native zone identity its receipt returned; the layout
// tidy (#611) reads a zone's kind and crop from these when it has one.
func zoneClaims(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, tick domain.Tick) (domain.Fact[[]OwnedZone], error) {
	unknown := domain.Unknown[[]OwnedZone]()
	rows, err := tx.QueryContext(ctx, `SELECT m.plan_id,m.owner_id FROM plan_methods m LEFT JOIN standards g ON g.id=m.owner_id AND m.kind='standard' LEFT JOIN projects pr ON pr.id=m.owner_id AND m.kind='project'
 WHERE json_extract(COALESCE(g.payload,pr.payload),'$.Snapshot.Colony')=?
 AND json_extract(COALESCE(g.payload,pr.payload),'$.Snapshot.Load')=? AND json_extract(COALESCE(g.payload,pr.payload),'$.Snapshot.Map')=?
 AND EXISTS(SELECT 1 FROM actions a JOIN transitions t ON t.action_id=a.id WHERE a.plan_id=m.plan_id AND a.kind='zone_create' AND json_extract(t.payload,'$.Kind') IN ('observe','receipt'))
 ORDER BY m.plan_id`, current.Colony, current.Load, current.Map)
	if err != nil {
		return unknown, err
	}
	type link struct {
		plan    domain.PlanID
		concern domain.ConcernID
	}
	links := []link{}
	for rows.Next() {
		var v link
		if err = rows.Scan(&v.plan, &v.concern); err != nil {
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
	result := []OwnedZone{}
	goals := map[domain.ConcernID]OwnerSummary{}
	for _, link := range links {
		g, cached := goals[link.concern]
		if !cached {
			owner, err := loadOwner(ctx, tx, string(link.concern))
			if err != nil {
				return unknown, err
			}
			g, _ = SummarizeOwner(owner)
			goals[link.concern] = g
		}
		scope := g.Snapshot
		if scope.Colony != current.Colony || scope.Load != current.Load || scope.Map != current.Map {
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
			result = append(result, OwnedZone{ID: id, Kind: zone.Kind(), Crop: zone.Crop(), Cells: zone.Cells(), Role: zone.Role(), Concern: link.concern, Filter: zone.Filter(), Priority: zone.Priority()})
		}
	}
	return domain.Known(result), nil
}

// ZoneClaims exposes zoneClaims outside the rounds transaction:
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
