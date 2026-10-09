package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// SoldierSquadRecord is the persistent soldier squad: bot intent,
// kept in the save as the family/soldier_squad blob. Members are sorted.
type SoldierSquadRecord struct {
	World   World
	Members []policy.PawnID
}

const maxSoldierSquadBytes = 64 * 1024

func (r SoldierSquadRecord) Validate() error {
	if err := r.World.Validate(); err != nil {
		return err
	}
	if len(r.Members) > 256 {
		return errors.New("invalid soldier squad")
	}
	for i, m := range r.Members {
		if !validIdentity(string(m)) || i > 0 && r.Members[i-1] >= m {
			return errors.New("invalid soldier squad member")
		}
	}
	return nil
}

func loadSoldierSquad(ctx context.Context, tx *sql.Tx) (SoldierSquadRecord, bool, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM soldier_squad WHERE singleton=1").Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return SoldierSquadRecord{}, false, nil
		}
		return SoldierSquadRecord{}, false, err
	}
	var r SoldierSquadRecord
	if err := json.Unmarshal(data, &r); err != nil {
		return SoldierSquadRecord{}, false, err
	}
	canonical, err := json.Marshal(r)
	if err != nil || !bytes.Equal(data, canonical) || r.Validate() != nil {
		return SoldierSquadRecord{}, false, errors.New("invalid stored soldier squad")
	}
	return r, true, nil
}

// LoadSoldierSquad returns the squad for w's colony and map, empty when none
// or when it belongs to another colony or map. A reload of the same colony
// keeps the squad.
func (s *Store) LoadSoldierSquad(ctx context.Context, w World) (policy.SoldierSquad, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return policy.SoldierSquad{}, err
	}
	defer tx.Rollback()
	r, ok, err := loadSoldierSquad(ctx, tx)
	if err != nil || !ok || r.World.Colony != w.Colony || r.World.Map != w.Map {
		return policy.SoldierSquad{}, err
	}
	return policy.SoldierSquad{Members: r.Members}, tx.Commit()
}

// SaveSoldierSquad replaces the stored squad; members are canonicalised.
func (s *Store) SaveSoldierSquad(ctx context.Context, r SoldierSquadRecord) error {
	r.Members = slices.Clone(r.Members)
	slices.Sort(r.Members)
	if err := r.Validate(); err != nil {
		return err
	}
	data, err := json.Marshal(r)
	if err != nil {
		return err
	}
	if len(data) > maxSoldierSquadBytes {
		return ErrCapacity
	}
	if _, err = s.db.ExecContext(ctx, "INSERT INTO soldier_squad(singleton,payload) VALUES(1,?) ON CONFLICT(singleton) DO UPDATE SET payload=excluded.payload", data); err != nil {
		return err
	}
	return nil
}
