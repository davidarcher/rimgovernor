package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// removedProjectConcerns are Project Concerns the catalog no longer holds
// (AllowStartingSupplies folded into ManageSupplySafety, #2188).
var removedProjectConcerns = map[policy.ConcernID]bool{"AllowStartingSupplies": true}

// removedStandardConcerns are Standard Concerns the catalog no longer holds
// (MaintainWaste, #2202).
var removedStandardConcerns = map[policy.ConcernID]bool{"MaintainWaste": true}

// migrateRounds drops what an older rounds row held for removed Concerns: the
// persisted `StartingSupplies` history, the removed Project bindings, whose
// project rows retire, and every record of a removed Standard (binding,
// ranking, progress, no-op, dependency). Plans holding the removed `waste`
// action retire. It reports whether the row changed; the next review files
// the migrated row.
func migrateRounds(ctx context.Context, tx *sql.Tx, data []byte, r *Rounds) (bool, error) {
	var legacy struct{ StartingSupplies json.RawMessage }
	if err := json.Unmarshal(data, &legacy); err != nil {
		return false, err
	}
	migrated := legacy.StartingSupplies != nil
	kept := r.Projects[:0]
	for _, binding := range r.Projects {
		if !removedProjectConcerns[binding.Concern] {
			kept = append(kept, binding)
			continue
		}
		migrated = true
		if _, err := tx.ExecContext(ctx, "UPDATE projects SET retired=1 WHERE id=?", binding.Project); err != nil {
			return false, err
		}
	}
	r.Projects = kept
	if len(r.Projects) == 0 {
		r.Projects = nil
	}
	if dropRemovedStandards(r) {
		migrated = true
	}
	res, err := tx.ExecContext(ctx, "UPDATE plans SET retired=1 WHERE retired=0 AND id IN (SELECT plan_id FROM actions WHERE kind='waste')")
	if err != nil {
		return false, err
	}
	if n, err := res.RowsAffected(); err == nil && n > 0 {
		migrated = true
	}
	return migrated, nil
}

// dropRemovedStandards filters a removed Standard Concern out of every list
// of the row that names Concerns, reporting whether anything went.
func dropRemovedStandards(r *Rounds) bool {
	removed := func(id policy.ConcernID) bool { return removedStandardConcerns[id] }
	before := len(r.Standards) + len(r.Emergency) + len(r.Progress) + len(r.NoOps) + len(r.Dependencies) +
		len(r.Development.Committed) + len(r.Development.Rows) + len(r.Development.Holds)
	r.Standards = slices.DeleteFunc(r.Standards, func(v RoundsStandard) bool { return removed(v.Concern) })
	r.Emergency = slices.DeleteFunc(r.Emergency, removed)
	r.Progress = slices.DeleteFunc(r.Progress, func(v policy.ConcernProgress) bool { return removed(v.Concern) })
	r.NoOps = slices.DeleteFunc(r.NoOps, func(v policy.NoOpRecord) bool { return removed(v.Concern) })
	r.Dependencies = slices.DeleteFunc(r.Dependencies, func(v DependencyRecord) bool { return removed(v.Need) || removed(v.Concern) })
	r.Development.Committed = slices.DeleteFunc(r.Development.Committed, removed)
	r.Development.Rows = slices.DeleteFunc(r.Development.Rows, func(v RoundsDevelopmentRow) bool { return removed(v.Concern) })
	r.Development.Holds = slices.DeleteFunc(r.Development.Holds, func(v policy.DevelopmentHold) bool { return removed(v.Concern) })
	after := len(r.Standards) + len(r.Emergency) + len(r.Progress) + len(r.NoOps) + len(r.Dependencies) +
		len(r.Development.Committed) + len(r.Development.Rows) + len(r.Development.Holds)
	return after != before
}
