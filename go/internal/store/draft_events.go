package store

import (
	"context"
	"database/sql"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type draftReceiptEvent struct {
	Attempt domain.AttemptID
	Receipt domain.Receipt
	Claim   *domain.DraftClaim
}
type draftObserveEvent struct {
	Observation domain.Observation
	Current     domain.GenerationSnapshot
	Claim       *domain.DraftClaim
}
type draftResultEvent struct {
	Release domain.DraftRelease
	Outcome domain.DraftCleanupOutcome
}

func claimFact(v *domain.DraftClaim) domain.Fact[domain.DraftClaim] {
	if v == nil {
		return domain.Unknown[domain.DraftClaim]()
	}
	return domain.Known(*v)
}
func claimPointer(v domain.Fact[domain.DraftClaim]) *domain.DraftClaim {
	c, ok := v.Value()
	if !ok {
		return nil
	}
	return &c
}
func checkClaimSession(ctx context.Context, tx *sql.Tx, c domain.DraftClaim) error {
	id, err := identity(ctx, tx)
	if err != nil {
		return err
	}
	if string(id) != string(c.Session) {
		return errors.New("claim controller namespace mismatch")
	}
	return nil
}

func validateDraftEvent(e transition) error {
	count := 0
	for _, present := range []bool{e.DraftReceipt != nil, e.DraftObserve != nil, e.DraftBegin != nil, e.DraftResult != nil, e.DraftCleanupObserve != nil} {
		if present {
			count++
		}
	}
	match := e.Kind == "draft_receipt" && e.DraftReceipt != nil || e.Kind == "draft_observe" && e.DraftObserve != nil || e.Kind == "draft_begin" && e.DraftBegin != nil || e.Kind == "draft_result" && e.DraftResult != nil || e.Kind == "draft_cleanup_observe" && e.DraftCleanupObserve != nil
	if count == 0 {
		if e.Kind == "draft_receipt" || e.Kind == "draft_observe" || e.Kind == "draft_begin" || e.Kind == "draft_result" || e.Kind == "draft_cleanup_observe" {
			return errors.New("draft event payload missing")
		}
		return nil
	}
	if count != 1 || !match || e.Snapshot != (domain.GenerationSnapshot{}) || e.Tick != 0 || e.Attempt != 0 || e.Receipt != "" || e.Observation != (domain.Observation{}) {
		return errors.New("invalid draft event union")
	}
	return nil
}
func applyDraft(p domain.Progress, e transition) (domain.Progress, error) {
	switch e.Kind {
	case "draft_receipt":
		v := e.DraftReceipt
		return p.RecordDraftReceipt(v.Attempt, v.Receipt, claimFact(v.Claim))
	case "draft_observe":
		v := e.DraftObserve
		return p.ObserveDraft(v.Observation, v.Current, claimFact(v.Claim))
	case "draft_begin":
		next, err := p.BeginDraftCleanup(e.DraftBegin.Request)
		if err != nil {
			return p, err
		}
		cleanup, _ := next.View().DraftCleanup.Value()
		release, _ := cleanup.Release.Value()
		if e.DraftBegin.Sequence != 0 && release != *e.DraftBegin {
			return p, errors.New("cleanup sequence or request corrupt")
		}
		return next, nil
	case "draft_result":
		return p.RecordDraftCleanup(e.DraftResult.Release, e.DraftResult.Outcome)
	case "draft_cleanup_observe":
		return p.ObserveDraftCleanup(*e.DraftCleanupObserve)
	}
	return p, errors.New("unknown draft event")
}
func guardDraftAdvance(ctx context.Context, tx *sql.Tx, state PlanState, action domain.ActionID, e transition) error {
	if e.DraftReceipt != nil && e.DraftReceipt.Claim != nil {
		if err := checkClaimSession(ctx, tx, *e.DraftReceipt.Claim); err != nil {
			return err
		}
	}
	if e.DraftObserve != nil && e.DraftObserve.Claim != nil {
		if err := checkClaimSession(ctx, tx, *e.DraftObserve.Claim); err != nil {
			return err
		}
	}
	for _, a := range state.Spec.Actions() {
		if a.ID() != action {
			continue
		}
		if _, ok := a.OwnedDraft(); !ok {
			return nil
		}
		if e.Kind == "prepare" {
			return errors.New("draft preparation requires typed admission")
		}
		if e.Kind == "dispatch" {
			for _, v := range state.DraftAdmissions {
				if v.Action == action {
					if v.Admission.Snapshot != e.Snapshot || e.Tick < v.Admission.Tick {
						return errors.New("draft dispatch differs from admission")
					}
					return nil
				}
			}
			return errors.New("draft admission required")
		}
	}
	return nil
}
func (s *Store) RecordDraftReceipt(ctx context.Context, plan domain.PlanID, action domain.ActionID, attempt domain.AttemptID, receipt domain.Receipt, claim domain.Fact[domain.DraftClaim]) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "draft_receipt", DraftReceipt: &draftReceiptEvent{attempt, receipt, claimPointer(claim)}})
}
func (s *Store) ObserveDraft(ctx context.Context, plan domain.PlanID, observation domain.Observation, current domain.GenerationSnapshot, claim domain.Fact[domain.DraftClaim]) (domain.Progress, error) {
	return s.advance(ctx, plan, observation.Action, transition{Kind: "draft_observe", DraftObserve: &draftObserveEvent{observation, current, claimPointer(claim)}})
}
func (s *Store) BeginDraftCleanup(ctx context.Context, plan domain.PlanID, action domain.ActionID, request domain.DraftReleaseRequest) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "draft_begin", DraftBegin: &domain.DraftRelease{Request: request}})
}
func (s *Store) RecordDraftCleanup(ctx context.Context, plan domain.PlanID, action domain.ActionID, release domain.DraftRelease, outcome domain.DraftCleanupOutcome) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "draft_result", DraftResult: &draftResultEvent{release, outcome}})
}
func (s *Store) ObserveDraftCleanup(ctx context.Context, plan domain.PlanID, action domain.ActionID, observation domain.DraftCleanupObservation) (domain.Progress, error) {
	return s.advance(ctx, plan, action, transition{Kind: "draft_cleanup_observe", DraftCleanupObserve: &observation})
}
