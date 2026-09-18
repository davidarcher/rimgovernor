package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// ProductionLadderRecord is the workshop planner's last observed rung for a
// MaintainResource deficit: which bench and recipe it settled on and the
// research projects still gating them. The routine review reads it back as
// the derived EnsureResearch target, so a research-gated bench raises its
// own deficit without a native read per review. Research empty means the
// bench is buildable or standing and nothing is owed to research.
type ProductionLadderRecord struct {
	World    World
	Tick     domain.Tick
	Resource policy.Resource
	Bench    string
	Recipe   string
	Research []string
}

const maxProductionLadderBytes = 64 * 1024

func (r ProductionLadderRecord) Validate() error {
	if err := r.World.Validate(); err != nil {
		return err
	}
	if r.Tick < 0 || !validIdentity(string(r.Resource)) || r.Bench != "" && !validIdentity(r.Bench) || r.Recipe != "" && !validIdentity(r.Recipe) || len(r.Research) > 256 {
		return errors.New("invalid production ladder record")
	}
	for i, project := range r.Research {
		if !validIdentity(project) || i > 0 && r.Research[i-1] >= project {
			return errors.New("invalid production ladder research")
		}
	}
	return nil
}

func loadProductionLadder(ctx context.Context, tx *sql.Tx) (ProductionLadderRecord, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM production_ladder WHERE singleton=1").Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return ProductionLadderRecord{}, false, nil
		}
		return ProductionLadderRecord{}, false, err
	}
	if len(data) > maxProductionLadderBytes {
		return ProductionLadderRecord{}, false, ErrCapacity
	}
	var r ProductionLadderRecord
	if err := json.Unmarshal(data, &r); err != nil {
		return ProductionLadderRecord{}, false, err
	}
	canonical, err := json.Marshal(r)
	if err != nil || !bytes.Equal(data, canonical) || r.Validate() != nil {
		return ProductionLadderRecord{}, false, errors.New("invalid stored production ladder")
	}
	return r, true, nil
}

// LoadProductionLadder returns the stored rung for w, false when none or when
// it belongs to another world: a reload observes the ladder afresh.
func (s *Store) LoadProductionLadder(ctx context.Context, w World) (ProductionLadderRecord, bool, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ProductionLadderRecord{}, false, err
	}
	defer tx.Rollback()
	r, ok, err := loadProductionLadder(ctx, tx)
	if err != nil {
		return ProductionLadderRecord{}, false, err
	}
	if !ok || r.World != w {
		return ProductionLadderRecord{}, false, tx.Commit()
	}
	return r, true, tx.Commit()
}

// SaveProductionLadder replaces the stored rung; Research is canonicalised.
func (s *Store) SaveProductionLadder(ctx context.Context, r ProductionLadderRecord) error {
	r.Research = append([]string{}, r.Research...)
	sort.Strings(r.Research)
	if err := r.Validate(); err != nil {
		return err
	}
	data, err := json.Marshal(r)
	if err != nil {
		return err
	}
	if len(data) > maxProductionLadderBytes {
		return ErrCapacity
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err = tx.ExecContext(ctx, "INSERT INTO production_ladder(singleton,payload) VALUES(1,?) ON CONFLICT(singleton) DO UPDATE SET payload=excluded.payload", data); err != nil {
		return err
	}
	return tx.Commit()
}
