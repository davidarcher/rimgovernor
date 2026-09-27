package store

import (
	"context"
	"database/sql"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// resourceHistory includes retired and player plans, scoped to this world.
// The first placed-build observation counts once; repeated inspections and
// completion never charge the same placement again.
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
	rows, err := tx.QueryContext(ctx, `SELECT a.kind,t.payload FROM transitions t
 JOIN actions a ON a.id=t.action_id
 WHERE a.kind='production_bill' AND json_extract(t.payload,'$.Kind')='observe'
 AND json_extract(t.payload,'$.Observation.Snapshot.Colony')=?
 AND json_extract(t.payload,'$.Observation.Snapshot.Load')=?
 AND json_extract(t.payload,'$.Observation.Snapshot.Map')=?
 AND json_extract(t.payload,'$.Observation.Effect')='completed'
 GROUP BY a.id HAVING min(t.sequence)=t.sequence
 AND json_extract(t.payload,'$.Observation.Tick')>? AND json_extract(t.payload,'$.Observation.Tick')<=?
 ORDER BY t.sequence LIMIT 4097`, current.Colony, current.Load, current.Map, h.Start, tick)
	if err != nil {
		return h, err
	}
	defer rows.Close()
	count := 0
	for rows.Next() {
		count++
		if count > 4096 {
			h.Start = tick
			h.Uses = nil
			return h, nil
		}
		var kind string
		var payload []byte
		if err := rows.Scan(&kind, &payload); err != nil {
			return h, err
		}
		var event transition
		if err := decode(payload, &event); err != nil {
			return h, err
		}
		o := event.Observation
		if o.Tick <= h.Start || o.Tick > tick {
			continue
		}
		var steel, components, plasteel domain.Fact[int64]
		if use := o.BillConsumption; use != nil {
			if use.Steel != nil {
				steel = domain.Known(*use.Steel)
			}
			if use.Components != nil {
				components = domain.Known(*use.Components)
			}
		}
		h.Uses = append(h.Uses, policy.ResourceUse{Tick: o.Tick, Resource: "Steel", Count: steel}, policy.ResourceUse{Tick: o.Tick, Resource: "ComponentIndustrial", Count: components}, policy.ResourceUse{Tick: o.Tick, Resource: "Plasteel", Count: plasteel})
	}
	return h, rows.Err()
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
