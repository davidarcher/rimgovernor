package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// IntentInspection anchors an intent's dispatch to a native read of the
// current world and tick. It admits nothing: native validates the intent
// when it applies it.
type IntentInspection struct {
	StartedAt, ObservedAt time.Time
	Current               domain.GenerationSnapshot
	Tick                  domain.Tick
}

// plainIntents are the intent-mode kinds runIntent dispatches: no
// prerequisite action and no journal admission. Native validates each
// against live state and treats a setting that already holds as applied.
var plainIntents = map[domain.ActionKind]bool{
	domain.BuildingAction:             true,
	domain.ApparelPolicyAction:        true,
	domain.ResearchSelectAction:       true,
	domain.DialogAnswerAction:         true,
	domain.PrisonerInteractionAction:  true,
	domain.QuestAcceptAction:          true,
	domain.QuestShuttleAction:         true,
	domain.RitualAction:               true,
	domain.IdeoligionReformAction:     true,
	domain.AbilityAction:              true,
	domain.IgniteAction:               true,
	domain.RemoveProductionBillAction: true,
	domain.DoorControlAction:          true,
	domain.CaravanDepartureAction:     true,
	domain.AssignAction:               true,
	domain.WorkAssignmentAction:       true,
	domain.HusbandryAction:            true,
	domain.ProductionBillAction:       true,
	domain.ZoneCreateAction:           true,
	domain.ZoneDeleteAction:           true,
	domain.ZoneCellEditAction:         true,
	domain.StockpilePatchAction:       true,
	domain.FoundationRemovalAction:    true,
	domain.FloorRemovalAction:         true,
	domain.CoverClearanceAction:       true,
	domain.WastepackHaulAction:        true,
	domain.CutPlantAction:             true,
	domain.SupplyAllowAction:          true,
	domain.SupplyForbidAction:         true,
	domain.DeconstructionAction:       true,
	domain.ExcavationAction:           true,
	domain.WallRemovalAction:          true,
	domain.RecoveryServiceAction:      true,
	domain.MoveBuildingAction:         true,
	domain.UninstallBuildingAction:    true,
	domain.AcquisitionAction:          true,
	domain.MineAcquisitionAction:      true,
	domain.AcquisitionWithdrawAction:  true,
	domain.BuildingTemperatureAction:  true,
	domain.BedUseAction:               true,
	domain.GrowerCropAction:           true,
	domain.ClaimBuildingAction:        true,
	domain.AutoRefuelAction:           true,
	domain.SurgeryAction:              true,
	domain.AreaAction:                 true,
	domain.PolicyPruneAction:          true,
	domain.RemoveRoofAction:           true,
	domain.AreaPlantCutAction:         true,
	domain.ReadingPolicyAction:        true,
	domain.DrugPolicyAction:           true,
	domain.FoodPolicyAction:           true,
	domain.PawnSettingsAction:         true,
	domain.AutoHomeAreaAction:         true,
	domain.RepairAction:               true,
	domain.CleanAction:                true,
	domain.OpenCasketAction:           true,
	domain.TendAction:                 true,
	domain.OwnedDraftAction:           true,
	domain.SubdueAction:               true,
	domain.EquipAction:                true,
	domain.DropEquipmentAction:        true,
	domain.RescueAction:               true,
	domain.CaptureAction:              true,
	domain.MoodReliefAction:           true,
	domain.GearReplaceAction:          true,
	domain.UseItemAction:              true,
	domain.StripAction:                true,
	domain.RulesAttachAction:          true,
}

// BatchItem is one action's outcome of RunBatch: its dispatch result and error.
type BatchItem struct {
	Action domain.ActionID
	Result Result
	Err    error
}

type intentItem struct {
	action   domain.Action
	progress domain.Progress
}

// RunBatch is the executor's one entry point. It dispatches a plan's plain
// intents in one native Actions/Apply call: one writer slot, one plan load, one inspection whose
// snapshot and tick anchor every action, one journal transaction per stage.
// An unresolved attempt is settled from the journal. Other kinds run their own
// flow per action, in input order, before the plain intents dispatch. The returned error is
// the batch's own (stopped, cancelled, plan load); per-action errors are in
// the items, in input order.
func (e *Executor) RunBatch(ctx context.Context, plan domain.PlanID, actions []domain.ActionID) ([]BatchItem, error) {
	out := make([]BatchItem, len(actions))
	err := e.withPlan(ctx, plan, actions, func(ctx context.Context, state store.PlanState, authority Authority, generation context.Context) error {
		var items []intentItem
		var at []int
		flowed := false
		for i, id := range actions {
			out[i].Action = id
			action, progress := find(state, id)
			if action.Kind().IntentMode() && progress.View().Unresolved {
				out[i].Result, out[i].Err = e.settleIntent(progress)
				continue
			}
			if action.Kind() == domain.CombatBatchAction && progress.View().Unresolved {
				out[i].Result.Progress = progress
				continue
			}
			if plainIntents[action.Kind()] || action.Kind() == domain.CombatBatchAction {
				items = append(items, intentItem{action, progress})
				at = append(at, i)
				continue
			}
			// A kind with its own flow (admission, prerequisites) runs
			// alone, in input order, on durable state reloaded after any
			// earlier flow wrote to it.
			if flowed {
				var err error
				if state, err = e.journal.LoadPlan(ctx, plan); err != nil {
					return err
				}
			}
			out[i].Result, out[i].Err = e.runLoaded(ctx, state, id, authority, generation)
			flowed = true
		}
		for j, item := range e.runIntents(ctx, items, authority, generation) {
			out[at[j]].Result, out[at[j]].Err = item.Result, item.Err
		}
		return nil
	})
	return out, err
}

// runIntents dispatches plain intents of one plan. Each receipt is terminal:
// applied (the blueprint is placed, or the setting holds) is done, refused is
// over and the owning routine replans, and a lost reply is sent again under
// a new attempt.
func (e *Executor) runIntents(ctx context.Context, items []intentItem, authority Authority, generation context.Context) []BatchItem {
	out := make([]BatchItem, len(items))
	fail := func(at []int, err error) {
		for _, i := range at {
			out[i].Err = err
		}
	}
	var live []int
	var expected domain.GenerationSnapshot
	for i, item := range items {
		out[i].Action, out[i].Result.Progress = item.action.ID(), item.progress
		if (!plainIntents[item.action.Kind()] || !item.action.Kind().IntentMode()) && item.action.Kind() != domain.CombatBatchAction || item.progress.Action() != item.action {
			out[i].Err = ErrEvidence
			continue
		}
		v := item.progress.View()
		switch v.Stage {
		case domain.Completed, domain.Cancelled, domain.Unsuccessful:
			continue
		case domain.Pending, domain.Prepared:
		default:
			out[i].Err = ErrEvidence
			continue
		}
		want := authority.Snapshot
		if want.Plan != v.Plan || want.Revision != v.Revision {
			if e.roundsScope == nil {
				out[i].Err = ErrAuthority
				continue
			}
			want.Plan, want.Revision = v.Plan, v.Revision
		}
		if len(live) > 0 && want != expected {
			out[i].Err = ErrAuthority
			continue
		}
		expected = want
		live = append(live, i)
	}
	if len(live) == 0 {
		return out
	}
	if err := e.guard(ctx, expected, generation); err != nil {
		fail(live, err)
		return out
	}
	// One bounds read anchors the whole batch.
	inspection, err := e.boundary.InspectIntent(ctx, Target{items[live[0]].action, expected})
	if err != nil {
		fail(live, err)
		return out
	}
	if err = e.guard(ctx, expected, generation); err != nil {
		fail(live, err)
		return out
	}
	if !inspection.Current.Matches(expected) || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		fail(live, ErrHeld)
		return out
	}
	attempts := make([]store.BatchAttempt, len(live))
	for j, i := range live {
		v := items[i].progress.View()
		attempts[j] = store.BatchAttempt{Plan: v.Plan, Action: v.Action, Snapshot: expected, Tick: max(v.Tick, inspection.Tick)}
	}
	prepared, err := e.journal.PrepareBatch(ctx, attempts)
	if err != nil {
		fail(live, err)
		return out
	}
	var ready []int
	var readyAttempts []store.BatchAttempt
	for j, r := range prepared {
		i := live[j]
		if r.Err != nil {
			out[i].Err = r.Err
			continue
		}
		out[i].Result.Progress = r.Progress
		ready, readyAttempts = append(ready, i), append(readyAttempts, attempts[j])
	}
	dispatched, err := e.journal.DispatchBatch(ctx, readyAttempts)
	if err != nil {
		fail(ready, err)
		return out
	}
	var sent []int
	var placements []Placement
	var nexts []domain.Progress
	for j, r := range dispatched {
		i, a := ready[j], readyAttempts[j]
		if errors.Is(r.Err, store.ErrActionVetoed) {
			// An action Safeguard refused only this action; record why.
			if held, holdErr := e.journal.Hold(ctx, a.Plan, a.Action, []domain.HeldReason{domain.HeldUnsafeItem}, a.Tick); holdErr == nil {
				out[i].Result.Progress = held
			}
			out[i].Err = errors.Join(ErrHeld, r.Err)
			continue
		}
		if r.Err != nil {
			out[i].Err = r.Err
			continue
		}
		out[i].Result.Progress = r.Progress
		sent, nexts = append(sent, i), append(nexts, r.Progress)
		placements = append(placements, Placement{items[i].action, r.Progress.View().Attempt, expected, a.Tick})
	}
	if len(sent) == 0 {
		return out
	}
	kinds := make([]domain.Receipt, len(sent))
	zones := make([]string, len(sent))
	stockpiles := make([][]domain.CreatedZone, len(sent))
	bills := make([]string, len(sent))
	combat := make([][]domain.CombatResult, len(sent))
	causes := make([]error, len(sent))
	if err = e.guard(ctx, expected, generation); err != nil {
		for j := range sent {
			kinds[j], causes[j] = domain.ReceiptUnknown, err
		}
	} else {
		for _, i := range sent {
			out[i].Result.NativeCalled = true
		}
		receipts, err := e.boundary.WriteIntents(ctx, placements)
		for j, p := range placements {
			switch {
			case err != nil:
				kinds[j], causes[j] = receiptAfterCallError(err), err
			case len(receipts) != len(placements):
				kinds[j], causes[j] = domain.ReceiptUnknown, ErrEvidence
			case receipts[j].Action != p.Action.ID() || receipts[j].Attempt != p.Attempt || receipts[j].Snapshot != expected:
				kinds[j], causes[j] = domain.ReceiptUnknown, ErrEvidence
			default:
				kinds[j], zones[j], bills[j] = receipts[j].Kind, receipts[j].Zone, receipts[j].Bill
				stockpiles[j] = receipts[j].Stockpiles
				combat[j] = receipts[j].Combat
			}
			var check error
			if p.Action.Kind() == domain.CombatBatchAction {
				_, check = nexts[j].RecordCombatReceipt(p.Attempt, kinds[j], combat[j])
			} else {
				_, check = nexts[j].RecordReceipt(p.Attempt, kinds[j])
			}
			if check != nil {
				kinds[j], causes[j] = domain.ReceiptUnknown, errors.Join(causes[j], ErrEvidence)
				combat[j] = nil
			}
			causes[j] = errors.Join(causes[j], ctx.Err())
		}
	}
	e.recordReceipts(out, sent, placements, kinds, zones, stockpiles, bills, combat, causes)
	return out
}

// recordReceipts writes the receipts of the sent items in one journal
// transaction; an applied zone_create or bill placement records its zone or bill id on its own. Like
// recordZone, it uses a fresh bounded context so cancellation never erases
// an attempt.
func (e *Executor) recordReceipts(out []BatchItem, sent []int, placements []Placement, kinds []domain.Receipt, zones []string, stockpiles [][]domain.CreatedZone, bills []string, combat [][]domain.CombatResult, causes []error) {
	ctx, cancel := context.WithTimeout(context.Background(), e.limits.JournalTimeout)
	defer cancel()
	var receipts []store.BatchReceipt
	var at []int
	for j, i := range sent {
		p, plan := placements[j], out[i].Result.Progress.View().Plan
		out[i].Err = causes[j]
		if len(stockpiles[j]) > 0 && kinds[j] == domain.ReceiptAccepted {
			progress, err := e.journal.RecordStockpileReceipt(ctx, plan, p.Action.ID(), p.Attempt, stockpiles[j])
			if err == nil {
				out[i].Result.Progress = progress
			}
			out[i].Err = errors.Join(out[i].Err, err)
			continue
		}
		if zones[j] != "" && kinds[j] == domain.ReceiptAccepted {
			progress, err := e.journal.RecordZoneReceipt(ctx, plan, p.Action.ID(), p.Attempt, zones[j])
			if err == nil {
				out[i].Result.Progress = progress
			}
			out[i].Err = errors.Join(out[i].Err, err)
			continue
		}
		if bills[j] != "" && kinds[j] == domain.ReceiptAccepted {
			progress, err := e.journal.RecordBillReceipt(ctx, plan, p.Action.ID(), p.Attempt, bills[j])
			if err == nil {
				out[i].Result.Progress = progress
			}
			out[i].Err = errors.Join(out[i].Err, err)
			continue
		}
		receipts = append(receipts, store.BatchReceipt{Plan: plan, Action: p.Action.ID(), Attempt: p.Attempt, Receipt: kinds[j], Combat: combat[j]})
		at = append(at, i)
	}
	results, err := e.journal.RecordReceipts(ctx, receipts)
	for k, i := range at {
		switch {
		case err != nil:
			out[i].Err = errors.Join(out[i].Err, err)
		case results[k].Err != nil:
			out[i].Err = errors.Join(out[i].Err, results[k].Err)
		default:
			out[i].Result.Progress = results[k].Progress
		}
	}
}

// PlainIntent reports whether kind dispatches with its plan's other plain
// intents in one native Apply; other kinds run their own flow per action.
func PlainIntent(kind domain.ActionKind) bool { return plainIntents[kind] }
