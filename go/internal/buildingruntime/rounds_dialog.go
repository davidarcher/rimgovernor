package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"log/slog"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoundsDialogSource is the native colony census RoundsDialogPlanner reads
// to find the exact observed window/options of the force-pausing choice
// dialog the game opened by itself (#156), the same ReadColonyFacts call
// Rounder itself uses to raise the AnswerDialog goal
// (observation.colony.go's own r.Facts.ChoiceDialog derivation).
type RoundsDialogSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error)
}
type RoundsDialogPlanner struct {
	reviewer *Rounder
	native   RoundsDialogSource
	policy   policy.DialogAnswerPolicy
}
type RoundsDialogResult struct {
	Verdict
	Plan domain.PlanID
	// Option is the label the planner chose when it admitted a plan.
	Option string
}

func NewRoundsDialogPlanner(reviewer *Rounder, native RoundsDialogSource, answer policy.DialogAnswerPolicy) (*RoundsDialogPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsDialogPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsDialogPlanner{reviewer, native, answer}, nil
}
func (r *RoundsDialogPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsDialogResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsDialogResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsDialogResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsDialogResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsDialogResult{Verdict: BuildingReasonNoReview}, nil
	}
	incident, found, err := incidentDeficit(call, p.journal, review, policy.AnswerDialog)
	if err != nil {
		return RoundsDialogResult{}, err
	}
	if !found {
		return RoundsDialogResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	if open, err := incidentOpenWork(call, p.journal, incident); err != nil || open {
		return RoundsDialogResult{Verdict: BuildingReasonExistingWork}, err
	}
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.reviewer.colonyFacts(call, r.native, identity, false)
	if err != nil {
		return RoundsDialogResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoundsDialogResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoundsDialogResult{}, fmt.Errorf("%w: step: err != nil || observed.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	dialog := observed.Dialog
	if dialog == nil || dialog.WindowId == nil {
		return RoundsDialogResult{Verdict: fieldUnavailable("dialog")}, nil
	}
	if !dialog.GetInteractive() {
		// Dialog_NodeTree.delayInteractivity greys the options for a second
		// of real time after opening; native refuses an answer until then, so
		// the planner waits for the next review rather than reporting the
		// dialog unanswerable (#179).
		return RoundsDialogResult{Verdict: BuildingReasonNotInteractive}, nil
	}
	options := make([]policy.DialogOption, 0, len(dialog.Options))
	for _, option := range dialog.Options {
		options = append(options, policy.DialogOption{Index: option.GetIndex(), Label: option.GetLabel(), Keys: option.GetKeys(), Selectable: option.GetSelectable(), Resolves: option.GetResolves()})
	}
	if policy.VoidNodeDisruptAbsent(options) {
		defenseAction(call, "routine-dialog", slog.LevelError, "refused", "void_node_disrupt_absent", "void_node", map[string]any{"window": dialog.GetWindowId()})
	}
	chosen, ok := policy.ChooseDialogOption(options, r.policy)
	if !ok {
		// Every option is disabled, a hyperlink or opens another window:
		// nothing the controller can activate answers this dialog, so the
		// hold stays with the player.
		return RoundsDialogResult{Verdict: refuse(RefusalRetriesSpent, "no_selectable_dialog_option", "")}, nil
	}
	windowID := dialog.GetWindowId()
	digestNext := sha256.Sum256([]byte(fmt.Sprintf("%d/%d/%s", windowID, chosen.Index, chosen.Label)))
	method := domain.MethodID(fmt.Sprintf("dialog-%x", digestNext[:16]))
	if slices.ContainsFunc(incident.Methods, func(m store.IncidentMethod) bool { return m.Method == method }) {
		return RoundsDialogResult{Verdict: waitFor(WaitMethodUsed, "dialog_method")}, nil
	}
	id := domain.MintPlanID()
	value, err := domain.NewDialogAnswer(windowID, chosen.Index, chosen.Label)
	if err != nil {
		return RoundsDialogResult{}, err
	}
	action, err := domain.NewDialogAnswerAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoundsDialogResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsDialogResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsDialogResult{}, err
	}
	if p.session.State() != state {
		return RoundsDialogResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	if _, err = p.journal.CommitIncidentMethod(call, incident.Incident.ID, method, "", plan); err != nil {
		return RoundsDialogResult{}, err
	}
	return RoundsDialogResult{Verdict: BuildingReasonAdmitted, Plan: id, Option: chosen.Label}, nil
}
