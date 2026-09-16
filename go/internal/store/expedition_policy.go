package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ExpeditionPolicySubmissionRequest is explicit player intent to change some
// of a world's expedition risk limits. Like PopulationPolicySubmissionRequest
// it produces no plan and no action, because setting limits issues no native
// call, and so owns its own request table rather than a row in the shared
// submissions table.
//
// Unlike the population policy this is a partial patch, not a replacement:
// the patch is merged over the
// policy already in force, so Patch names only what the player is changing
// and everything else keeps its established value.
type ExpeditionPolicySubmissionRequest struct {
	RequestID string
	World     World
	Patch     domain.ExpeditionPolicyPatch
}

// ExpeditionPolicySubmission is one stored request, the policy that request
// produced when it was accepted, and the world's policy as it stands now.
type ExpeditionPolicySubmission struct {
	Request ExpeditionPolicySubmissionRequest
	// Applied is the merged whole this request produced at the moment it was
	// accepted. It is recorded rather than recomputed so replaying an old
	// request ID reports what that request actually did, even after later
	// requests have moved the current value on.
	Applied domain.ExpeditionPolicy
	// Current is the world's policy now, which is Applied for a freshly
	// accepted request and a newer value when an older ID is replayed.
	Current domain.ExpeditionPolicy
}

// expeditionPolicyPatchWire is the stored JSON shape of one partial request.
// Absence must survive a round trip, so every field is a pointer and an
// unset field is omitted entirely. The store follows the precedent set by
// zone_edit_submissions and work_preference_requests in keeping a structured
// request payload as an opaque blob rather than widening the row.
type expeditionPolicyPatchWire struct {
	MinimumHomeColonists          *int32   `json:"minimumHomeColonists,omitempty"`
	MinimumHomeFoodDays           *float64 `json:"minimumHomeFoodDays,omitempty"`
	TravelFoodMarginDays          *float64 `json:"travelFoodMarginDays,omitempty"`
	MaximumTravelDays             *float64 `json:"maximumTravelDays,omitempty"`
	MaximumCaravans               *int32   `json:"maximumCaravans,omitempty"`
	MinimumGoodwill               *int32   `json:"minimumGoodwill,omitempty"`
	MinimumDestinationTemperature *float64 `json:"minimumDestinationTemperature,omitempty"`
	MaximumDestinationTemperature *float64 `json:"maximumDestinationTemperature,omitempty"`
	KeepHomeDoctor                *bool    `json:"keepHomeDoctor,omitempty"`
	RequireReturnStorage          *bool    `json:"requireReturnStorage,omitempty"`
}

func optionalWire[T comparable](o domain.Optional[T]) *T {
	if value, ok := o.Get(); ok {
		return &value
	}
	return nil
}

func optionalDomain[T comparable](v *T) domain.Optional[T] {
	if v == nil {
		return domain.Optional[T]{}
	}
	return domain.Some(*v)
}

func encodeExpeditionPolicyPatch(patch domain.ExpeditionPolicyPatch) ([]byte, error) {
	return json.Marshal(expeditionPolicyPatchWire{
		MinimumHomeColonists:          optionalWire(patch.MinimumHomeColonists),
		MinimumHomeFoodDays:           optionalWire(patch.MinimumHomeFoodDays),
		TravelFoodMarginDays:          optionalWire(patch.TravelFoodMarginDays),
		MaximumTravelDays:             optionalWire(patch.MaximumTravelDays),
		MaximumCaravans:               optionalWire(patch.MaximumCaravans),
		MinimumGoodwill:               optionalWire(patch.MinimumGoodwill),
		MinimumDestinationTemperature: optionalWire(patch.MinimumDestinationTemperature),
		MaximumDestinationTemperature: optionalWire(patch.MaximumDestinationTemperature),
		KeepHomeDoctor:                optionalWire(patch.KeepHomeDoctor),
		RequireReturnStorage:          optionalWire(patch.RequireReturnStorage),
	})
}

func decodeExpeditionPolicyPatch(payload []byte) (domain.ExpeditionPolicyPatch, error) {
	var wire expeditionPolicyPatchWire
	if err := json.Unmarshal(payload, &wire); err != nil {
		return domain.ExpeditionPolicyPatch{}, err
	}
	patch := domain.ExpeditionPolicyPatch{
		MinimumHomeColonists:          optionalDomain(wire.MinimumHomeColonists),
		MinimumHomeFoodDays:           optionalDomain(wire.MinimumHomeFoodDays),
		TravelFoodMarginDays:          optionalDomain(wire.TravelFoodMarginDays),
		MaximumTravelDays:             optionalDomain(wire.MaximumTravelDays),
		MaximumCaravans:               optionalDomain(wire.MaximumCaravans),
		MinimumGoodwill:               optionalDomain(wire.MinimumGoodwill),
		MinimumDestinationTemperature: optionalDomain(wire.MinimumDestinationTemperature),
		MaximumDestinationTemperature: optionalDomain(wire.MaximumDestinationTemperature),
		KeepHomeDoctor:                optionalDomain(wire.KeepHomeDoctor),
		RequireReturnStorage:          optionalDomain(wire.RequireReturnStorage),
	}
	return patch, patch.Validate()
}

// expeditionPolicyColumns is the stored column order of a whole policy,
// shared by both tables so the row helpers below stay in step.
const expeditionPolicyColumns = "minimum_home_colonists,minimum_home_food_days,travel_food_margin_days,maximum_travel_days,maximum_caravans,minimum_goodwill,minimum_destination_temperature,maximum_destination_temperature,keep_home_doctor,require_return_storage"

func expeditionPolicyValues(p domain.ExpeditionPolicy) []any {
	f := p.Fields()
	keepDoctor, returnStorage := int64(0), int64(0)
	if f.KeepHomeDoctor {
		keepDoctor = 1
	}
	if f.RequireReturnStorage {
		returnStorage = 1
	}
	return []any{f.MinimumHomeColonists, f.MinimumHomeFoodDays, f.TravelFoodMarginDays, f.MaximumTravelDays,
		f.MaximumCaravans, f.MinimumGoodwill, f.MinimumDestinationTemperature, f.MaximumDestinationTemperature,
		keepDoctor, returnStorage}
}

// expeditionPolicyScan builds the scan targets for expeditionPolicyColumns,
// returning them alongside the constructor that turns them into a value.
func expeditionPolicyScan() ([]any, func() (domain.ExpeditionPolicy, error)) {
	var fields domain.ExpeditionPolicyFields
	var keepDoctor, returnStorage int64
	targets := []any{&fields.MinimumHomeColonists, &fields.MinimumHomeFoodDays, &fields.TravelFoodMarginDays,
		&fields.MaximumTravelDays, &fields.MaximumCaravans, &fields.MinimumGoodwill,
		&fields.MinimumDestinationTemperature, &fields.MaximumDestinationTemperature, &keepDoctor, &returnStorage}
	return targets, func() (domain.ExpeditionPolicy, error) {
		if keepDoctor&^1 != 0 || returnStorage&^1 != 0 {
			return domain.ExpeditionPolicy{}, errors.New("invalid stored expedition policy flag")
		}
		fields.KeepHomeDoctor, fields.RequireReturnStorage = keepDoctor == 1, returnStorage == 1
		return domain.NewExpeditionPolicy(fields)
	}
}

func (q ExpeditionPolicySubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	return q.Patch.Validate()
}

// SubmitExpeditionPolicy atomically records one explicit player request,
// merges it over the world's policy in force (or over the contract defaults
// when the player has never set one), and makes the merged whole the new
// current policy. Replay is decided by request ID exactly as the plan-bearing
// submissions decide it: the same ID with the same requested fields returns
// the stored request and reports no creation, and with different fields
// returns ErrConflict. Because the stored request records both the patch and
// the policy it produced, a replay never re-merges and so can never apply an
// old request on top of a newer current value.
func (s *Store) SubmitExpeditionPolicy(ctx context.Context, q ExpeditionPolicySubmissionRequest) (ExpeditionPolicySubmission, bool, error) {
	if err := q.validate(); err != nil {
		return ExpeditionPolicySubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ExpeditionPolicySubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupExpeditionPolicySubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return ExpeditionPolicySubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return ExpeditionPolicySubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return ExpeditionPolicySubmission{}, false, err
	}
	current, err := currentExpeditionPolicy(ctx, tx, q.World)
	if err != nil {
		return ExpeditionPolicySubmission{}, false, err
	}
	applied, err := q.Patch.Apply(current)
	if err != nil {
		return ExpeditionPolicySubmission{}, false, err
	}
	payload, err := encodeExpeditionPolicyPatch(q.Patch)
	if err != nil {
		return ExpeditionPolicySubmission{}, false, err
	}
	values := append([]any{q.RequestID, q.World.Colony, q.World.Load, q.World.Map, payload}, expeditionPolicyValues(applied)...)
	if _, err = tx.ExecContext(ctx, "INSERT INTO expedition_policy_submissions(request_id,colony,load_token,map_id,patch,"+expeditionPolicyColumns+") VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", values...); err != nil {
		return ExpeditionPolicySubmission{}, false, conflict(err)
	}
	values = append([]any{q.World.Colony, q.World.Load, q.World.Map, q.RequestID}, expeditionPolicyValues(applied)...)
	if _, err = tx.ExecContext(ctx, "INSERT INTO expedition_policies(colony,load_token,map_id,request_id,"+expeditionPolicyColumns+") VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(colony,load_token,map_id) DO UPDATE SET request_id=excluded.request_id,minimum_home_colonists=excluded.minimum_home_colonists,minimum_home_food_days=excluded.minimum_home_food_days,travel_food_margin_days=excluded.travel_food_margin_days,maximum_travel_days=excluded.maximum_travel_days,maximum_caravans=excluded.maximum_caravans,minimum_goodwill=excluded.minimum_goodwill,minimum_destination_temperature=excluded.minimum_destination_temperature,maximum_destination_temperature=excluded.maximum_destination_temperature,keep_home_doctor=excluded.keep_home_doctor,require_return_storage=excluded.require_return_storage", values...); err != nil {
		return ExpeditionPolicySubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return ExpeditionPolicySubmission{}, false, err
	}
	return ExpeditionPolicySubmission{Request: q, Applied: applied, Current: applied}, true, nil
}

// LookupExpeditionPolicySubmission returns one stored request by request ID.
func (s *Store) LookupExpeditionPolicySubmission(ctx context.Context, requestID string) (ExpeditionPolicySubmission, error) {
	if err := submissionID(requestID); err != nil {
		return ExpeditionPolicySubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ExpeditionPolicySubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupExpeditionPolicySubmission(ctx, tx, requestID)
	if err != nil {
		return ExpeditionPolicySubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return ExpeditionPolicySubmission{}, err
	}
	return result, nil
}

// CurrentExpeditionPolicy returns the world's expedition policy. Unlike
// CurrentPopulationPolicy this never reports ErrNotFound: every field of the
// contract carries a default, so a world whose player has never
// submitted a policy is governed by DefaultExpeditionPolicy rather than by no
// policy at all, and that is also the base a first partial patch merges onto.
func (s *Store) CurrentExpeditionPolicy(ctx context.Context, w World) (domain.ExpeditionPolicy, error) {
	if err := w.Validate(); err != nil {
		return domain.ExpeditionPolicy{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.ExpeditionPolicy{}, err
	}
	defer tx.Rollback()
	policy, err := currentExpeditionPolicy(ctx, tx, w)
	if err != nil {
		return domain.ExpeditionPolicy{}, err
	}
	if err = tx.Commit(); err != nil {
		return domain.ExpeditionPolicy{}, err
	}
	return policy, nil
}

func currentExpeditionPolicy(ctx context.Context, tx *sql.Tx, w World) (domain.ExpeditionPolicy, error) {
	targets, build := expeditionPolicyScan()
	err := tx.QueryRowContext(ctx, "SELECT "+expeditionPolicyColumns+" FROM expedition_policies WHERE colony=? AND load_token=? AND map_id=?", w.Colony, w.Load, w.Map).Scan(targets...)
	if errors.Is(err, sql.ErrNoRows) {
		return domain.DefaultExpeditionPolicy(), nil
	}
	if err != nil {
		return domain.ExpeditionPolicy{}, err
	}
	return build()
}

func lookupExpeditionPolicySubmission(ctx context.Context, tx *sql.Tx, id string) (ExpeditionPolicySubmission, error) {
	var world World
	var payload []byte
	targets, build := expeditionPolicyScan()
	err := tx.QueryRowContext(ctx, "SELECT colony,load_token,map_id,patch,"+expeditionPolicyColumns+" FROM expedition_policy_submissions WHERE request_id=?", id).
		Scan(append([]any{&world.Colony, &world.Load, &world.Map, &payload}, targets...)...)
	if errors.Is(err, sql.ErrNoRows) {
		return ExpeditionPolicySubmission{}, ErrNotFound
	}
	if err != nil {
		return ExpeditionPolicySubmission{}, err
	}
	applied, err := build()
	if err != nil {
		return ExpeditionPolicySubmission{}, err
	}
	patch, err := decodeExpeditionPolicyPatch(payload)
	if err != nil {
		return ExpeditionPolicySubmission{}, err
	}
	result := ExpeditionPolicySubmission{
		Request: ExpeditionPolicySubmissionRequest{RequestID: id, World: world, Patch: patch},
		Applied: applied,
	}
	if err = result.Request.validate(); err != nil {
		return ExpeditionPolicySubmission{}, err
	}
	if result.Current, err = currentExpeditionPolicy(ctx, tx, world); err != nil {
		return ExpeditionPolicySubmission{}, err
	}
	return result, nil
}
