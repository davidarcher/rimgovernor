package store

import (
	"context"
	"database/sql"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// resourceHistory spans the journal's history in this world, from the first
// prepared or dispatched action. No production is charged to it: a bill
// intent's receipt is terminal, so nothing observes what its iterations
// consumed, and the runway reads a zero rate over the window.
func resourceHistory(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, tick domain.Tick) (policy.ResourceHistory, error) {
	h := policy.ResourceHistory{Start: tick, End: tick}
	var first sql.NullInt64
	err := tx.QueryRowContext(ctx, `SELECT min(json_extract(payload,'$.Tick')) FROM transitions
	 WHERE json_extract(payload,'$.Kind') IN ('prepare','dispatch')
 AND json_extract(payload,'$.Snapshot.Colony')=? AND json_extract(payload,'$.Snapshot.Load')=?
 AND json_extract(payload,'$.Snapshot.Map')=? AND json_extract(payload,'$.Tick')<=?`, current.Colony, current.Load, current.Map, tick).Scan(&first)
	if err != nil {
		return h, err
	}
	if first.Valid {
		h.Start = max(domain.Tick(first.Int64), tick-policy.ResourceHistoryWindow)
	}
	return h, nil
}

func resourceRunways(ctx context.Context, tx *sql.Tx, r RoutineReviewRequest) ([]policy.ResourceRunway, error) {
	history, err := resourceHistory(ctx, tx, r.Current, r.Tick)
	if err != nil {
		return nil, err
	}
	var result []policy.ResourceRunway
	for _, resource := range []policy.Resource{"Steel", "ComponentIndustrial", "Plasteel"} {
		stock := domain.Unknown[int64]()
		if rows, known := r.Facts.Resources.Value(); known {
			var n int64
			valid := true
			for _, row := range rows {
				if row.Resource == resource {
					if row.Count < 0 || row.Count > math.MaxInt64-n {
						valid = false
						break
					}
					n += row.Count
				}
			}
			if valid {
				stock = domain.Known(n)
			}
		}
		reserve := r.Policy.ResourceTargets[resource]
		result = append(result, policy.ForecastResourceRunway(resource, stock, r.Facts.ResourceSurfaceOre[resource], reserve, history))
	}
	return result, nil
}
