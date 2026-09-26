package store

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RoutineShrineStep is the shrine planner's last step (#680) as it judged
// it, not the review's advisory ShrineHolds: Reason is the step's answer,
// Shrine and Hold the shrine it held on (or acted on) and why, and Skipped
// every candidate it judged and passed over before or after that one, with
// its own reason. Tick is the review tick the step planned under.
type RoutineShrineStep struct {
	Tick    domain.Tick
	Reason  string
	Shrine  string              `json:",omitempty"`
	Hold    string              `json:",omitempty"`
	Plan    domain.PlanID       `json:",omitempty"`
	Skipped []policy.ShrineHold `json:",omitempty"`
}

// ShrinePlannerHeld and ShrinePlannerSkipped mark a ShrineHolds row with
// what the planner's last step did with that shrine (policy.ShrineHold.Planner).
const (
	ShrinePlannerHeld    = "held"
	ShrinePlannerSkipped = "skipped"
)

func (s RoutineShrineStep) validate(tick domain.Tick) error {
	if s.Tick > tick || s.Reason == "" || len(s.Reason) > 64 || len(s.Hold) > 64 || len(s.Shrine) > 256 || len(s.Skipped) > 256 {
		return errors.New("invalid routine shrine step")
	}
	return nil
}

// RecordShrineStep files the shrine planner's step on the review loaded
// under revision and marks its ShrineHolds rows: the shrine the step held
// on as held, every candidate it passed over as skipped. A review that has
// moved past revision is ErrConflict.
func (s *Store) RecordShrineStep(ctx context.Context, revision uint64, step RoutineShrineStep) (RoutineReview, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return RoutineReview{}, err
	}
	defer tx.Rollback()
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return RoutineReview{}, err
	}
	if review.Revision != revision {
		return RoutineReview{}, fmt.Errorf("%w: routine review revision %d, shrine step under %d", ErrConflict, review.Revision, revision)
	}
	if err = step.validate(review.Tick); err != nil {
		return RoutineReview{}, err
	}
	markShrineHolds(review.ShrineHolds, &step)
	copied := step
	review.ShrineStep = &copied
	data, err := json.Marshal(review)
	if err != nil {
		return RoutineReview{}, err
	}
	if len(data) > 1024*1024 {
		return RoutineReview{}, ErrCapacity
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO routine_review(singleton,payload) VALUES(1,?) ON CONFLICT(singleton) DO UPDATE SET payload=excluded.payload", data); err != nil {
		return RoutineReview{}, err
	}
	return review, tx.Commit()
}

// markShrineHolds marks each shrine row of holds with what step did with
// that shrine; a nil step clears the marks.
func markShrineHolds(holds []policy.ShrineHold, step *RoutineShrineStep) {
	skipped := map[string]bool{}
	if step != nil {
		for _, hold := range step.Skipped {
			skipped[hold.Shrine] = true
		}
	}
	for i := range holds {
		row := &holds[i]
		if row.Casket != "" || row.Occupant != "" {
			continue
		}
		row.Planner = ""
		switch {
		case step != nil && step.Hold != "" && row.Shrine == step.Shrine:
			row.Planner = ShrinePlannerHeld
		case skipped[row.Shrine]:
			row.Planner = ShrinePlannerSkipped
		}
	}
}
