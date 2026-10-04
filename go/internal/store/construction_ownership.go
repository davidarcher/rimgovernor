package store

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Every applied building intent of an autopilot goal claims what its key
// builds (policy.OwnedConstructions matches the census row's intent key),
// through catalog retirement and Manual. Explicit goal cancellation revokes
// upkeep ownership; world changes and future receipts confer none.
//
// Only a plan holding a building action with an accepted receipt can carry
// an applied intent, so the link query excludes the rest before the full
// plan load; every historical method is otherwise replayed on each routine
// review (#84).
func constructionClaims(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, tick domain.Tick) (domain.Fact[[]policy.ConstructionClaim], error) {
	unknown := domain.Unknown[[]policy.ConstructionClaim]()
	rows, err := tx.QueryContext(ctx, `SELECT m.plan_id,COALESCE(m.goal_id,m.project_id) FROM goal_methods m LEFT JOIN goals g ON g.id=m.goal_id LEFT JOIN projects pr ON pr.id=m.project_id
 WHERE json_extract(COALESCE(g.payload,pr.payload),'$.Snapshot.Colony')=?
 AND json_extract(COALESCE(g.payload,pr.payload),'$.Snapshot.Load')=? AND json_extract(COALESCE(g.payload,pr.payload),'$.Snapshot.Map')=?
 AND EXISTS(SELECT 1 FROM actions a JOIN transitions t ON t.action_id=a.id WHERE a.plan_id=m.plan_id AND a.kind='building' AND json_extract(t.payload,'$.Kind')='receipt' AND json_extract(t.payload,'$.Receipt')='accepted')
 ORDER BY m.plan_id LIMIT 257`, current.Colony, current.Load, current.Map)
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
	result := []policy.ConstructionClaim{}
	goals := map[domain.GoalID]OwnerSummary{}
	for _, link := range links {
		g, cached := goals[link.goal]
		if !cached {
			owner, err := loadOwner(ctx, tx, string(link.goal))
			if err != nil {
				return unknown, err
			}
			g, _ = SummarizeOwner(owner)
			goals[link.goal] = g
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
			building, isBuilding := progress.Action().Building()
			effect, ek := v.Effect.Value()
			if !isBuilding || !ek || effect != domain.EffectCompleted || v.Stage != domain.Completed || v.Tick > tick || v.Snapshot.Colony != current.Colony || v.Snapshot.Load != current.Load || v.Snapshot.Map != current.Map {
				continue
			}
			result = append(result, policy.ConstructionClaim{Plan: link.plan, Action: v.Action, Goal: link.goal, Building: building})
			if len(result) > 256 {
				return unknown, nil
			}
		}
	}
	return domain.Known(result), nil
}

func (s *Store) ConstructionClaims(ctx context.Context, current domain.GenerationSnapshot, tick domain.Tick) (domain.Fact[[]policy.ConstructionClaim], error) {
	if current.Validate() != nil || tick < 0 {
		return domain.Unknown[[]policy.ConstructionClaim](), ErrConflict
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.Unknown[[]policy.ConstructionClaim](), err
	}
	defer tx.Rollback()
	result, err := constructionClaims(ctx, tx, current, tick)
	if err != nil {
		return result, err
	}
	return result, tx.Commit()
}
