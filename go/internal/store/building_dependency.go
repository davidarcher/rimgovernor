package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// checkDependencies is every admission's prerequisite gate (#937):
// domain.CheckDependencies, then the last rounds's census for each
// prerequisite whose receipt does not mean the work is done. An accepted
// building intent completes once its blueprint is placed, so a dependent (a
// bed on a floor) waits for the building to stand built (Rounds.Built).
// An accepted wall removal completes once its demolition is designated, so a
// dependent (the replacement wall on the same cell) waits for the census to
// show no wall left in that cell (Rounds.WallCells, #989).
func checkDependencies(ctx context.Context, tx *sql.Tx, state PlanState, action domain.ActionID, current domain.GenerationSnapshot, tick domain.Tick) error {
	if err := state.Spec.CheckDependencies(action, state.Progress, current, tick); err != nil {
		return err
	}
	actions := map[domain.ActionID]domain.Action{}
	for _, a := range state.Spec.Actions() {
		actions[a.ID()] = a
	}
	var built []domain.ActionID
	var cleared []domain.Cell
	for _, d := range state.Spec.Dependencies() {
		if d.Action != action {
			continue
		}
		required := actions[d.Requires]
		if required.Kind() == domain.BuildingAction {
			built = append(built, d.Requires)
		}
		if removal, ok := required.WallRemoval(); ok {
			cleared = append(cleared, removal.Cell())
		}
	}
	if len(built) == 0 && len(cleared) == 0 {
		return nil
	}
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	s := review.Snapshot
	if s.Colony != current.Colony || s.Load != current.Load || s.Map != current.Map {
		return domain.ErrDependency
	}
	for _, r := range built {
		if !slices.Contains(review.Built, r) {
			return domain.ErrDependency
		}
	}
	for _, c := range cleared {
		// A cell the review never censused (the plan is newer than it)
		// is not known clear.
		if !slices.Contains(review.WallCells, WallCell{Cell: c}) {
			return domain.ErrDependency
		}
	}
	return nil
}

// WallCell is one cell a live plan's wall removal names and whether a
// colony Wall still stood on it in the construction census.
type WallCell struct {
	Cell     domain.Cell
	Standing bool `json:",omitempty"`
}

// maxWallCells bounds Rounds.WallCells: one stone-shell bundle names
// at most four removal cells, and few bundles are ever live at once.
const maxWallCells = 256

// wallCells answers, from a complete colony construction census, whether a
// colony Wall stands on each cell a wall removal of a live plan names.
func wallCells(ctx context.Context, tx *sql.Tx, census policy.CurrentConstruction) ([]WallCell, error) {
	rows, err := tx.QueryContext(ctx, "SELECT a.wall_removal_payload FROM actions a JOIN plans p ON p.id=a.plan_id WHERE a.kind='wall_removal' AND p.retired=0")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	named := map[domain.Cell]bool{}
	for rows.Next() {
		var data []byte
		var payload wallRemovalPayload
		if err = rows.Scan(&data); err != nil {
			return nil, err
		}
		if json.Unmarshal(data, &payload) != nil {
			return nil, errors.New("invalid wall removal payload")
		}
		named[domain.Cell{X: payload.X, Z: payload.Z}] = true
	}
	if err = rows.Err(); err != nil {
		return nil, err
	}
	if len(named) > maxWallCells {
		return nil, ErrCapacity
	}
	standing := map[domain.Cell]bool{}
	for _, b := range census.Buildings {
		if b.Building.Definition() != "Wall" {
			continue
		}
		for _, c := range b.Cells {
			standing[c] = true
		}
	}
	out := make([]WallCell, 0, len(named))
	for c := range named {
		out = append(out, WallCell{Cell: c, Standing: standing[c]})
	}
	slices.SortFunc(out, func(a, b WallCell) int {
		if a.Cell.X != b.Cell.X {
			return int(a.Cell.X - b.Cell.X)
		}
		return int(a.Cell.Z - b.Cell.Z)
	})
	return out, nil
}

// abandonStuckWallRemovals is the census-side give-up for a wall removal
// native never carries out (#1001): once a completed removal's wall still
// stands in the census more than policy.DevelopmentStallTicks after its
// dispatch, the removal and every action waiting on it are cancelled, so
// the plan settles and its goal re-plans instead of checkDependencies
// holding the replacement forever.
func abandonStuckWallRemovals(ctx context.Context, tx *sql.Tx, cells []WallCell, tick domain.Tick) error {
	standing := map[domain.Cell]bool{}
	for _, c := range cells {
		if c.Standing {
			standing[c.Cell] = true
		}
	}
	if len(standing) == 0 {
		return nil
	}
	rows, err := tx.QueryContext(ctx, "SELECT DISTINCT a.plan_id FROM actions a JOIN plans p ON p.id=a.plan_id WHERE a.kind='wall_removal' AND p.retired=0")
	if err != nil {
		return err
	}
	var plans []domain.PlanID
	for rows.Next() {
		var id domain.PlanID
		if err = rows.Scan(&id); err != nil {
			rows.Close()
			return err
		}
		plans = append(plans, id)
	}
	rows.Close()
	if err = rows.Err(); err != nil {
		return err
	}
	for _, plan := range plans {
		state, err := load(ctx, tx, plan)
		if err != nil {
			return err
		}
		withdraw := map[domain.ActionID]bool{}
		for _, p := range state.Progress {
			v := p.View()
			removal, ok := p.Action().WallRemoval()
			if ok && v.Stage == domain.Completed && standing[removal.Cell()] && tick-v.Tick > policy.DevelopmentStallTicks {
				withdraw[v.Action] = true
			}
		}
		if len(withdraw) == 0 {
			continue
		}
		for _, d := range state.Spec.Dependencies() {
			if withdraw[d.Requires] {
				withdraw[d.Action] = true
			}
		}
		for _, p := range state.Progress {
			v := p.View()
			if !withdraw[v.Action] || v.Stage == domain.Cancelled || v.Stage == domain.Unsuccessful || v.Stage == domain.Completed && !p.Action().Kind().IntentMode() {
				continue
			}
			if _, err = advanceInTransaction(ctx, tx, plan, v.Action, transition{Kind: "cancel"}); err != nil {
				return err
			}
		}
	}
	return nil
}

// builtActions is every live plan's building action whose building stands
// built in the census, matched by definition, stuff, anchor and rotation
// (#1355), sorted.
func builtActions(ctx context.Context, tx *sql.Tx, census policy.CurrentConstruction) ([]domain.ActionID, error) {
	rows, err := tx.QueryContext(ctx, "SELECT a.id,a.definition,a.x,a.z,a.rotation,a.stuff FROM actions a JOIN plans p ON p.id=a.plan_id WHERE a.kind='building' AND p.retired=0")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []domain.ActionID{}
	for rows.Next() {
		var id domain.ActionID
		var definition, rotation, stuff string
		var x, z int32
		if err = rows.Scan(&id, &definition, &x, &z, &rotation, &stuff); err != nil {
			return nil, err
		}
		b, err := domain.NewBuilding(definition, domain.Cell{X: x, Z: z}, domain.Rotation(rotation), stuff)
		if err != nil {
			return nil, err
		}
		if _, ok := census.Built(b); ok {
			out = append(out, id)
		}
	}
	if err = rows.Err(); err != nil {
		return nil, err
	}
	slices.Sort(out)
	return out, nil
}
