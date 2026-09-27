package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// IntentInspection anchors an intent's dispatch to a native read of the
// current world and tick. It admits nothing: native validates the intent
// when it applies it (#856).
type IntentInspection struct {
	StartedAt, ObservedAt time.Time
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
}

// plainIntents are the intent-mode kinds runIntent dispatches: no
// prerequisite action and no journal admission. Native validates each
// against live state and treats a setting that already holds as applied.
var plainIntents = map[domain.ActionKind]bool{
	domain.BuildingAction:            true,
	domain.ApparelPolicyAction:       true,
	domain.ResearchSelectAction:      true,
	domain.NamingConfirmationAction:  true,
	domain.DialogAnswerAction:        true,
	domain.PrisonerInteractionAction: true,
	domain.QuestAcceptAction:         true,
	domain.CaravanDepartureAction:    true,
	domain.BedAssignAction:           true,
	domain.WorkAssignmentAction:      true,
	domain.HusbandryAction:           true,
	domain.ProductionBillAction:      true,
	domain.ZoneCreateAction:          true,
	domain.ZoneDeleteAction:          true,
	domain.ZoneCellEditAction:        true,
	domain.StockpilePatchAction:      true,
	domain.FoundationRemovalAction:   true,
	domain.HomeCoverageAction:        true,
	domain.CoverClearanceAction:      true,
	domain.CutPlantAction:            true,
	domain.SupplyAllowAction:         true,
	domain.SupplyForbidAction:        true,
	domain.DeconstructionAction:      true,
	domain.ExcavationAction:          true,
	domain.WasteAction:               true,
	domain.RecoveryServiceAction:     true,
	domain.MoveBuildingAction:        true,
	domain.UninstallBuildingAction:   true,
	domain.BuildingTemperatureAction: true,
	domain.BedUseAction:              true,
	domain.GrowerCropAction:          true,
	domain.ClaimBuildingAction:       true,
}

// runIntent dispatches one plain intent. The receipt is terminal: applied
// (the blueprint is placed, or the setting holds) is done, refused is over
// and the owning routine replans, and a lost reply is sent again under a
// new attempt.
func (e *Executor) runIntent(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	if !plainIntents[action.Kind()] || !action.Kind().IntentMode() || p.Action() != action {
		return result, ErrEvidence
	}
	v := p.View()
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
	if err := e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	inspection, err := e.boundary.InspectIntent(ctx, Target{action, expected})
	if err != nil {
		return result, err
	}
	if err = e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	if !inspection.Current.Matches(expected) || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return result, ErrHeld
	}
	tick := max(v.Tick, inspection.Tick)
	next, err := e.journal.Prepare(ctx, v.Plan, v.Action, expected, tick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	next, err = e.journal.Dispatch(ctx, v.Plan, v.Action, expected, tick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	attempt := Placement{action, next.View().Attempt, expected, tick}
	if err = e.guard(ctx, expected, generation); err != nil {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, err)
	}
	result.NativeCalled = true
	receipt, err := e.boundary.WriteIntent(ctx, attempt)
	kind := receipt.Kind
	if err != nil {
		kind = receiptAfterCallError(err)
	} else if receipt.Action != v.Action || receipt.Attempt != attempt.Attempt || receipt.Snapshot != expected {
		kind, err = domain.ReceiptUnknown, ErrEvidence
	}
	if _, check := next.RecordReceipt(attempt.Attempt, kind); check != nil {
		kind, err = domain.ReceiptUnknown, errors.Join(err, ErrEvidence)
	}
	return e.recordZone(result, v.Plan, attempt, kind, receipt.Zone, errors.Join(err, ctx.Err()))
}
