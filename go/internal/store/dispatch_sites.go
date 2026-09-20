package store

import (
	"context"
	"database/sql"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DispatchSite is the explicit worker/target footprint already persisted with
// an action. It carries no prediction of the native route to that target.
type DispatchSite struct {
	Worker domain.PawnID
	Target string
	Cell   domain.Fact[domain.Cell]
}

func (s *Store) DispatchSites(ctx context.Context, plan domain.PlanID) (map[domain.ActionID]DispatchSite, error) {
	rows, err := s.db.QueryContext(ctx, "SELECT id,pawn,target,x,z FROM actions WHERE plan_id=?", plan)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[domain.ActionID]DispatchSite{}
	for rows.Next() {
		var id domain.ActionID
		var worker, target sql.NullString
		var x, z sql.NullInt64
		if err = rows.Scan(&id, &worker, &target, &x, &z); err != nil {
			return nil, err
		}
		site := DispatchSite{Worker: domain.PawnID(worker.String), Target: target.String}
		if x.Valid && z.Valid {
			site.Cell = domain.Known(domain.Cell{X: int32(x.Int64), Z: int32(z.Int64)})
		}
		out[id] = site
	}
	return out, rows.Err()
}
