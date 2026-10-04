package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/gabp"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// GovernorStateSchemaVersion versions every governor state blob (#882).
//
// Shadow blobs (#974) mirror the store into the save's GovernorState
// component; the store stays authoritative. Each value is ASCII JSON
// (non-ASCII escaped as \uXXXX) with a top-level "schemaVersion".
//
//	standard/<concern id>     GovernorStandardBlob, one per unretired standard;
//	                          a retired standard's key is deleted.
//	project/<project id>      GovernorProjectBlob, one per unretired project
//	                          (finished ones included: they are the record);
//	                          a retired project's key is deleted.
//	family/layout_plan        GovernorFamilyBlob, Record = policy.LayoutPlan
//	family/tidies             GovernorFamilyBlob, Record = []LayoutTidy
//	                          (latest state per item, sorted by item)
//	family/defense_layout     GovernorFamilyBlob, Record = DefenseLayoutRecord
//	family/production_ladder  GovernorFamilyBlob, Record = ProductionLadderRecord
//	family/soldier_squad      GovernorFamilyBlob, Record = SoldierSquadRecord
//
// Layout plan and tidies are world rows: the blob carries the scope
// (colony, map, tick) of the newest row written. Field names are the
// Go struct field names encoding/json emits; a breaking change bumps
// GovernorStateSchemaVersion.
const GovernorStateSchemaVersion = 4

const (
	GovernorStandardKeyPrefix   = "standard/"
	GovernorProjectKeyPrefix    = "project/"
	GovernorLayoutPlanKey       = "family/layout_plan"
	GovernorTidiesKey           = "family/tidies"
	GovernorDefenseLayoutKey    = "family/defense_layout"
	GovernorProductionLadderKey = "family/production_ladder"
	GovernorSoldierSquadKey     = "family/soldier_squad"
)

// GovernorStandardBlob is one goal: its payload and CAS revision. Methods and
// admission counts are session state, re-planned after a load (#997).
type GovernorStandardBlob struct {
	SchemaVersion int             `json:"schemaVersion"`
	Standard      domain.Standard `json:"standard"`
	Revision      uint64          `json:"revision"`
}

// GovernorProjectBlob is one project: its payload and CAS revision. Methods
// are session state, re-planned after a load, as a goal's.
type GovernorProjectBlob struct {
	SchemaVersion int            `json:"schemaVersion"`
	Project       domain.Project `json:"project"`
	Revision      uint64         `json:"revision"`
}

// GovernorFamilyBlob is one family record; Scope is set for timeline rows.
type GovernorFamilyBlob struct {
	SchemaVersion int             `json:"schemaVersion"`
	Scope         *GovernorScope  `json:"scope,omitempty"`
	Record        json.RawMessage `json:"record"`
}

type GovernorScope struct {
	Colony domain.ColonyID `json:"colony"`
	Map    domain.MapID    `json:"map"`
	Tick   domain.Tick     `json:"tick"`
}

// GovernorStateBlobs renders the store as governor state blobs by key.
func (s *Store) GovernorStateBlobs(ctx context.Context) (map[string]string, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	out := map[string]string{}
	put := func(key string, value any) error {
		data, err := json.Marshal(value)
		if err != nil {
			return err
		}
		out[key] = string(gabp.ASCIIJSON(data))
		return nil
	}
	family := func(key string, scope *GovernorScope, record any) error {
		data, err := json.Marshal(record)
		if err != nil {
			return err
		}
		return put(key, GovernorFamilyBlob{SchemaVersion: GovernorStateSchemaVersion, Scope: scope, Record: data})
	}
	rows, err := tx.QueryContext(ctx, "SELECT id FROM standards WHERE retired=0 ORDER BY id")
	if err != nil {
		return nil, err
	}
	var ids []domain.ConcernID
	for rows.Next() {
		var id domain.ConcernID
		if err = rows.Scan(&id); err != nil {
			rows.Close()
			return nil, err
		}
		ids = append(ids, id)
	}
	rows.Close()
	if err = rows.Err(); err != nil {
		return nil, err
	}
	for _, id := range ids {
		g, err := loadStandard(ctx, tx, id)
		if err != nil {
			return nil, fmt.Errorf("standard %s: %w", id, err)
		}
		if err = put(GovernorStandardKeyPrefix+string(id), GovernorStandardBlob{SchemaVersion: GovernorStateSchemaVersion, Standard: g.Standard, Revision: g.Revision}); err != nil {
			return nil, err
		}
	}
	projectRows, err := tx.QueryContext(ctx, "SELECT id FROM projects WHERE retired=0 ORDER BY id")
	if err != nil {
		return nil, err
	}
	var projectIDs []domain.ProjectID
	for projectRows.Next() {
		var id domain.ProjectID
		if err = projectRows.Scan(&id); err != nil {
			projectRows.Close()
			return nil, err
		}
		projectIDs = append(projectIDs, id)
	}
	projectRows.Close()
	if err = projectRows.Err(); err != nil {
		return nil, err
	}
	for _, id := range projectIDs {
		p, err := loadProject(ctx, tx, id)
		if err != nil {
			return nil, fmt.Errorf("project %s: %w", id, err)
		}
		if err = put(GovernorProjectKeyPrefix+string(id), GovernorProjectBlob{SchemaVersion: GovernorStateSchemaVersion, Project: p.Project, Revision: p.Revision}); err != nil {
			return nil, err
		}
	}
	var scope GovernorScope
	var plan string
	err = tx.QueryRowContext(ctx, "SELECT colony,map_id,tick,plan FROM colony_layout_plans ORDER BY rowid DESC LIMIT 1").Scan(&scope.Colony, &scope.Map, &scope.Tick, &plan)
	if err == nil {
		err = put(GovernorLayoutPlanKey, GovernorFamilyBlob{SchemaVersion: GovernorStateSchemaVersion, Scope: &scope, Record: json.RawMessage(plan)})
	}
	if err != nil && !errors.Is(err, sql.ErrNoRows) {
		return nil, err
	}
	if tidyScope, tidies, err := newestLayoutTidies(ctx, tx); err != nil {
		return nil, err
	} else if tidyScope != nil {
		if err = family(GovernorTidiesKey, tidyScope, tidies); err != nil {
			return nil, err
		}
	}
	if r, ok, err := loadDefenseLayout(ctx, tx); err != nil {
		return nil, err
	} else if ok {
		if err = family(GovernorDefenseLayoutKey, nil, r); err != nil {
			return nil, err
		}
	}
	if r, ok, err := loadProductionLadder(ctx, tx); err != nil {
		return nil, err
	} else if ok {
		if err = family(GovernorProductionLadderKey, nil, r); err != nil {
			return nil, err
		}
	}
	if r, ok, err := loadSoldierSquad(ctx, tx); err != nil {
		return nil, err
	} else if ok {
		if err = family(GovernorSoldierSquadKey, nil, r); err != nil {
			return nil, err
		}
	}
	return out, tx.Commit()
}

// newestLayoutTidies returns the latest state per item in the scope of the
// newest tidy row, nil scope when none.
func newestLayoutTidies(ctx context.Context, tx *sql.Tx) (*GovernorScope, []LayoutTidy, error) {
	var scope GovernorScope
	err := tx.QueryRowContext(ctx, "SELECT colony,map_id,tick FROM layout_tidies ORDER BY id DESC LIMIT 1").Scan(&scope.Colony, &scope.Map, &scope.Tick)
	if errors.Is(err, sql.ErrNoRows) {
		return nil, nil, nil
	}
	if err != nil {
		return nil, nil, err
	}
	rows, err := tx.QueryContext(ctx, "SELECT tick,item,kind,status,from_x,from_z,from_w,from_h,to_x,to_z,to_w,to_h,crop,new_zone,plan_id,explanation FROM layout_tidies WHERE colony=? AND map_id=? ORDER BY id DESC", scope.Colony, scope.Map)
	if err != nil {
		return nil, nil, err
	}
	defer rows.Close()
	latest := map[string]LayoutTidy{}
	for rows.Next() {
		var t LayoutTidy
		var kind, status string
		if err := rows.Scan(&t.Tick, &t.Item, &kind, &status, &t.From.X, &t.From.Z, &t.From.Width, &t.From.Height, &t.To.X, &t.To.Z, &t.To.Width, &t.To.Height, &t.Crop, &t.NewZone, &t.PlanID, &t.Explanation); err != nil {
			return nil, nil, err
		}
		t.Kind, t.Status = policy.TidyKind(kind), LayoutTidyStatus(status)
		if _, seen := latest[t.Item]; !seen {
			latest[t.Item] = t
		}
	}
	if err := rows.Err(); err != nil {
		return nil, nil, err
	}
	out := make([]LayoutTidy, 0, len(latest))
	for _, t := range latest {
		out = append(out, t)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Item < out[j].Item })
	return &scope, out, nil
}
