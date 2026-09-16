package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"reflect"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Work preferences belong to explicit player plan intent, never to an adviser
// or an automatic method. Replacing with an empty list clears all overrides.
type WorkPreferences struct {
	Plan      domain.PlanID
	World     World
	Revision  uint64
	Overrides []policy.WorkOverride
}
type WorkPreferenceRequest struct {
	RequestID        string
	Plan             domain.PlanID
	World            World
	ExpectedRevision uint64
	Overrides        []policy.WorkOverride
}
type WorkPreferenceRecord struct {
	Request     WorkPreferenceRequest
	Preferences WorkPreferences
}

func (q WorkPreferenceRequest) Validate() error {
	if submissionID(q.RequestID) != nil || submissionID(string(q.Plan)) != nil || q.World.Validate() != nil || q.Overrides == nil {
		return errors.New("invalid work preference request")
	}
	_, err := policy.AssignWork(nil, nil, q.Overrides)
	return err
}
func decodeWorkRecord(data []byte, target any) error {
	if len(data) > 4*1024*1024 {
		return ErrCapacity
	}
	if err := json.Unmarshal(data, target); err != nil {
		return err
	}
	canonical, err := json.Marshal(target)
	if err != nil || !bytes.Equal(canonical, data) {
		return errors.New("invalid work preference record")
	}
	return nil
}
func loadWorkPreferences(ctx context.Context, tx *sql.Tx, plan domain.PlanID) (WorkPreferences, error) {
	world, err := planWorld(ctx, tx, plan)
	if err != nil {
		return WorkPreferences{}, err
	}
	result := WorkPreferences{Plan: plan, World: world, Overrides: []policy.WorkOverride{}}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM work_preferences WHERE plan_id=?", plan).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return result, nil
	}
	if err != nil {
		return WorkPreferences{}, err
	}
	if err = decodeWorkRecord(data, &result); err != nil {
		return WorkPreferences{}, err
	}
	if result.Plan != plan || result.World != world || result.Revision == 0 {
		return WorkPreferences{}, ErrConflict
	}
	if err := (WorkPreferenceRequest{RequestID: "validate", Plan: plan, World: world, Overrides: result.Overrides}).Validate(); err != nil {
		return WorkPreferences{}, err
	}
	return result, nil
}
func (s *Store) LoadWorkPreferences(ctx context.Context, plan domain.PlanID) (WorkPreferences, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return WorkPreferences{}, err
	}
	defer tx.Rollback()
	v, err := loadWorkPreferences(ctx, tx, plan)
	if err != nil {
		return WorkPreferences{}, err
	}
	return v, tx.Commit()
}
func lookupWorkPreference(ctx context.Context, tx *sql.Tx, id string) (WorkPreferenceRecord, error) {
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM work_preference_requests WHERE request_id=?", id).Scan(&data); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return WorkPreferenceRecord{}, ErrNotFound
		}
		return WorkPreferenceRecord{}, err
	}
	var r WorkPreferenceRecord
	if err := decodeWorkRecord(data, &r); err != nil {
		return r, err
	}
	if r.Request.Validate() != nil || r.Request.RequestID != id || r.Request.ExpectedRevision == ^uint64(0) || r.Preferences.Revision != r.Request.ExpectedRevision+1 || r.Preferences.Plan != r.Request.Plan || r.Preferences.World != r.Request.World || !reflect.DeepEqual(r.Preferences.Overrides, r.Request.Overrides) {
		return WorkPreferenceRecord{}, ErrConflict
	}
	return r, nil
}
func (s *Store) LookupWorkPreference(ctx context.Context, id string) (WorkPreferenceRecord, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return WorkPreferenceRecord{}, err
	}
	defer tx.Rollback()
	v, err := lookupWorkPreference(ctx, tx, id)
	if err != nil {
		return WorkPreferenceRecord{}, err
	}
	return v, tx.Commit()
}
func (s *Store) SetWorkPreferences(ctx context.Context, q WorkPreferenceRequest) (WorkPreferenceRecord, error) {
	if err := q.Validate(); err != nil {
		return WorkPreferenceRecord{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return WorkPreferenceRecord{}, err
	}
	defer tx.Rollback()
	old, err := lookupWorkPreference(ctx, tx, q.RequestID)
	if err == nil {
		if !reflect.DeepEqual(old.Request, q) {
			return WorkPreferenceRecord{}, ErrConflict
		}
		return old, tx.Commit()
	}
	if !errors.Is(err, ErrNotFound) {
		return WorkPreferenceRecord{}, err
	}
	previous, err := loadWorkPreferences(ctx, tx, q.Plan)
	if err != nil {
		return WorkPreferenceRecord{}, err
	}
	if previous.World != q.World || previous.Revision != q.ExpectedRevision {
		return WorkPreferenceRecord{}, ErrConflict
	}
	if previous.Revision == ^uint64(0) {
		return WorkPreferenceRecord{}, ErrCapacity
	}
	var count int
	if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM work_preference_requests").Scan(&count); err != nil {
		return WorkPreferenceRecord{}, err
	}
	if count >= 4096 {
		return WorkPreferenceRecord{}, ErrCapacity
	}
	r := WorkPreferenceRecord{Request: q, Preferences: WorkPreferences{Plan: q.Plan, World: q.World, Revision: previous.Revision + 1, Overrides: append([]policy.WorkOverride{}, q.Overrides...)}}
	data, err := json.Marshal(r.Preferences)
	if err != nil {
		return WorkPreferenceRecord{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO work_preferences(plan_id,payload) VALUES(?,?) ON CONFLICT(plan_id) DO UPDATE SET payload=excluded.payload", q.Plan, data); err != nil {
		return WorkPreferenceRecord{}, err
	}
	data, err = json.Marshal(r)
	if err != nil {
		return WorkPreferenceRecord{}, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO work_preference_requests(request_id,payload) VALUES(?,?)", q.RequestID, data); err != nil {
		return WorkPreferenceRecord{}, err
	}
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return WorkPreferenceRecord{}, err
	}
	if review.Enabled && review.Snapshot.Plan == q.Plan {
		if _, err = reviewRoutineTx(ctx, tx, RoutineReviewRequest{Revision: review.Revision, Current: review.Snapshot, Tick: review.Tick, Enabled: false}); err != nil {
			return WorkPreferenceRecord{}, err
		}
	}
	return r, tx.Commit()
}
