package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"

	"crypto/rand"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ResearchSelectSubmissionRequest is explicit player intent to set the
// native current research project to one already-queued,
// prerequisite-ordered ResearchProjectDef. It mirrors
// QuestAcceptSubmissionRequest: this family is player-command-driven, not a
// fixed-priority routine producer, so a submitted request commits its own
// one-action plan immediately instead of being composed by a routine
// planner.
type ResearchSelectSubmissionRequest struct {
	RequestID string
	World     World
	Select    domain.ResearchSelect
}
type ResearchSelectSubmission struct {
	Request  ResearchSelectSubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}

type researchSelectPayload struct {
	Project string
}

func (r ResearchSelectSubmissionRequest) validate() error {
	if err := submissionID(r.RequestID); err != nil {
		return err
	}
	if err := r.World.Validate(); err != nil {
		return err
	}
	_, err := domain.NewResearchSelect(r.Select.Project())
	return err
}

// SubmitResearchSelect atomically stores one explicit player intent and its
// one-action plan, the same shape SubmitQuestAccept uses. Submission
// neither acquires authority nor issues the native SelectResearch command;
// a worker later admits and dispatches the committed action.
func (s *Store) SubmitResearchSelect(ctx context.Context, r ResearchSelectSubmissionRequest) (ResearchSelectSubmission, bool, error) {
	if err := r.validate(); err != nil {
		return ResearchSelectSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ResearchSelectSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupResearchSelectSubmission(ctx, tx, r.RequestID)
	if err == nil {
		if old.Request != r {
			return ResearchSelectSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return ResearchSelectSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return ResearchSelectSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return ResearchSelectSubmission{}, false, err
	}
	result := ResearchSelectSubmission{Request: r, Plan: domain.PlanID("research-select-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("research-select-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewResearchSelectAction(result.Action, r.Select)
	if err != nil {
		return ResearchSelectSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return ResearchSelectSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return ResearchSelectSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, r.RequestID, "research_select", r.World, result.Plan, result.Action); err != nil {
		return ResearchSelectSubmission{}, false, err
	}
	data, err := json.Marshal(researchSelectPayload{r.Select.Project()})
	if err != nil {
		return ResearchSelectSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO research_select_submissions(request_id,payload) VALUES(?,?)", r.RequestID, data); err != nil {
		return ResearchSelectSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return ResearchSelectSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupResearchSelectSubmission(ctx context.Context, requestID string) (ResearchSelectSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return ResearchSelectSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ResearchSelectSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupResearchSelectSubmission(ctx, tx, requestID)
	if err != nil {
		return ResearchSelectSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return ResearchSelectSubmission{}, err
	}
	return result, nil
}
func lookupResearchSelectSubmission(ctx context.Context, tx *sql.Tx, id string) (ResearchSelectSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "research_select")
	if err != nil {
		return ResearchSelectSubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM research_select_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return ResearchSelectSubmission{}, ErrNotFound
	}
	if err != nil {
		return ResearchSelectSubmission{}, err
	}
	if len(data) > 32768 {
		return ResearchSelectSubmission{}, errors.New("research select submission exceeds bound")
	}
	var payload researchSelectPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return ResearchSelectSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return ResearchSelectSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return ResearchSelectSubmission{}, errors.New("noncanonical research select submission")
	}
	value, err := domain.NewResearchSelect(payload.Project)
	if err != nil {
		return ResearchSelectSubmission{}, err
	}
	result := ResearchSelectSubmission{Request: ResearchSelectSubmissionRequest{RequestID: id, World: h.World, Select: value}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return ResearchSelectSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return ResearchSelectSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return ResearchSelectSubmission{}, errors.New("research select submission plan is corrupt")
	}
	actual, ok := actions[0].ResearchSelect()
	if !ok || actual != value {
		return ResearchSelectSubmission{}, errors.New("research select submission differs from intent")
	}
	return result, nil
}
