package store

import (
	"context"
	"database/sql"
	"encoding/json"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// removedProjectConcerns are Project Concerns the catalog no longer holds
// (AllowStartingSupplies folded into ManageSupplySafety, #2188).
var removedProjectConcerns = map[policy.ConcernID]bool{"AllowStartingSupplies": true}

// migrateRounds drops what an older rounds row held for removed Concerns: the
// persisted `StartingSupplies` history and the removed Project bindings, whose
// project rows retire. It reports whether the row changed; the next review
// files the migrated row.
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
	return migrated, nil
}
