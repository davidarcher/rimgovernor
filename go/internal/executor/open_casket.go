package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// OpenCasket (#460) is a pawn/thing order like Repair: the opener walks to a
// filled ancient casket's interaction cell and runs the vanilla Open job.
// It shares Repair's admission row (pawn, structure, cell and the two CAS
// tokens are the same shape), so the journal is the RepairJournal.

type OpenCasketInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.OpenCasketFacts
}

type OpenCasketDispatch struct {
	Attempt   Placement
	Admission store.RepairAdmission
}

type OpenCasketEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn                  domain.PawnID
	Casket                string
}

// OpenCasketBoundary is optionally composed like RepairBoundary. The opener
// is drafted by the melee lock ahead of the order; the draft is the plan's,
// so no ownership beyond the routine's own is needed here.
type OpenCasketBoundary interface {
	InspectOpenCasket(context.Context, Target) (OpenCasketInspection, error)
	OpenCasket(context.Context, OpenCasketDispatch) (Receipt, error)
	ObserveOpenCasket(context.Context, OpenCasketDispatch, domain.GenerationSnapshot) (OpenCasketEvidence, error)
}

// EnableOpenCasket activates the casket-opening capability; see
// EnableAcquisition for why capabilities are wired explicitly.
func (e *Executor) EnableOpenCasket(open OpenCasketBoundary) error {
	if open == nil {
		return errors.New("open casket boundary required")
	}
	j, ok := e.journal.(RepairJournal)
	if !ok {
		return errors.New("open casket boundary requires typed journal")
	}
	e.openCasket, e.repairJournal = open, j
	return nil
}

func (e *Executor) runOpenCasket(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	open, ok := action.OpenCasket()
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	v := p.View()
	if v.Unresolved {
		return e.reconcileOpenCasket(ctx, result, generation)
	}
	switch v.Stage {
	case domain.Completed, domain.Cancelled, domain.Unsuccessful:
		return result, nil
	case domain.Pending, domain.Prepared:
	default:
		return result, ErrEvidence
	}
	expected := authority.Snapshot
	if expected.Plan != v.Plan || expected.Revision != v.Revision {
		if e.routineScope == nil {
			return result, ErrAuthority
		}
		expected.Plan, expected.Revision = v.Plan, v.Revision
	}
	admissionOf := func(f policy.OpenCasketFacts) store.RepairAdmission {
		return store.RepairAdmission{Snapshot: expected, Tick: f.PreviewTick, Pawn: open.Pawn(), Structure: open.Casket(), Cell: open.Cell(), PawnSnapshotToken: f.Pawn.SnapshotToken, StructureSnapshotToken: f.CasketSnapshotToken}
	}
	minimum := v.Tick
	var inspection OpenCasketInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.openCasket.InspectOpenCasket(ctx, Target{action, expected})
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if inspection.Facts.Snapshot != expected || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
			return result, ErrHeld
		}
		facts := inspection.Facts
		minimum = max(minimum, facts.PawnTick)
		decision := policy.EvaluateOpenCasket(policy.OpenCasketRequest{Action: action, Progress: result.Progress, Current: expected, MinimumTick: minimum, Facts: facts})
		result.Refused = decision.Refused
		if !decision.Admitted {
			result.Progress = e.holdRefusal(ctx, v.Plan, v.Action, decision.Refused, minimum, result.Progress)
			return result, ErrHeld
		}
		next, err := e.repairJournal.PrepareRepair(ctx, v.Plan, v.Action, admissionOf(facts))
		if err != nil {
			return result, err
		}
		result.Progress = next
		minimum = max(minimum, facts.PreviewTick)
	}
	if err := e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return result, ErrHeld
	}
	next, err := e.journal.Dispatch(ctx, v.Plan, v.Action, expected, inspection.Facts.PreviewTick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	attempt := Placement{action, next.View().Attempt, expected, inspection.Facts.PreviewTick}
	if err = e.guard(ctx, expected, generation); err != nil {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, err)
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, ErrHeld)
	}
	result.NativeCalled = true
	receipt, err := e.openCasket.OpenCasket(ctx, OpenCasketDispatch{attempt, admissionOf(inspection.Facts)})
	kind := receipt.Kind
	if err != nil {
		kind = receiptAfterCallError(err)
	} else if receipt.Action != v.Action || receipt.Attempt != attempt.Attempt || receipt.Snapshot != expected {
		kind, err = domain.ReceiptUnknown, ErrEvidence
	}
	if _, check := next.RecordReceipt(attempt.Attempt, kind); check != nil {
		kind, err = domain.ReceiptUnknown, errors.Join(err, ErrEvidence)
	}
	return e.record(result, v.Plan, attempt, kind, errors.Join(err, ctx.Err()))
}

func (e *Executor) reconcileOpenCasket(ctx context.Context, result Result, generation context.Context) (Result, error) {
	p := result.Progress
	v := p.View()
	current := e.current().Snapshot
	if current.Validate() != nil || current.Native == 0 || current.Native < v.Snapshot.Native || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
		return result, ErrAuthority
	}
	state, err := e.journal.LoadPlan(ctx, v.Plan)
	if err != nil {
		return result, err
	}
	var admission store.RepairAdmission
	found := false
	for _, record := range state.RepairAdmissions {
		if record.Action == v.Action {
			admission, found = record.Admission, true
		}
	}
	if !found || admission.Snapshot != v.Snapshot || admission.Tick > v.Tick {
		return result, ErrEvidence
	}
	evidence, err := e.openCasket.ObserveOpenCasket(ctx, OpenCasketDispatch{Placement{p.Action(), v.Attempt, v.Snapshot, v.Tick}, admission}, current)
	if err != nil {
		return result, err
	}
	if ctx.Err() != nil {
		return result, ctx.Err()
	}
	if generation.Err() != nil || e.current().Snapshot != current {
		return result, ErrAuthority
	}
	open, _ := p.Action().OpenCasket()
	o := evidence.Observation
	if o.Action != v.Action || o.Attempt != v.Attempt || o.Snapshot != current || evidence.Pawn != open.Pawn() || evidence.Casket != open.Casket() || !e.fresh(evidence.StartedAt, evidence.ObservedAt) {
		return result, ErrEvidence
	}
	switch o.Effect {
	case domain.EffectCompleted, domain.EffectAbsent, domain.EffectUnsuccessful:
		if !evidence.Complete || o.Causality != domain.AfterDispatch {
			return result, ErrEvidence
		}
	case domain.EffectUnknown, domain.EffectPending:
	default:
		return result, ErrEvidence
	}
	next, err := e.journal.Observe(ctx, v.Plan, o, current)
	if err == nil {
		result.Progress = next
	}
	return result, err
}
