package buildingruntime

import (
	"context"
	"fmt"
	"log/slog"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// Drafts are plan-owned (#939): a plan drafts the pawns it needs, and the
// undraft sweep undrafts every drafted colonist no live plan needs. There
// is no native claim; the journal's plans and fights are the record.

// plannedDrafts is every pawn a live plan needs drafted:
//   - an owned draft not yet settled, or one its plan still holds
//     (workerPlanHoldsDraft);
//   - the capturer of an open capture or arrest plan;
//   - the roster of an open combat fight.
func plannedDrafts(ctx context.Context, journal *store.Store) (map[domain.PawnID]bool, error) {
	plans, err := journal.LoadPlans(ctx)
	if err != nil {
		return nil, err
	}
	needed := map[domain.PawnID]bool{}
	for _, plan := range plans {
		open := store.PlanOpen(plan)
		for _, progress := range plan.Progress {
			v := progress.View()
			if draft, ok := progress.Action().OwnedDraft(); ok {
				switch {
				case v.Unresolved, v.Stage == domain.Pending, v.Stage == domain.Prepared, v.Stage == domain.Dispatched, v.Stage == domain.AwaitingObservation:
					needed[draft.Pawn()] = true
				case workerPlanHoldsDraft(plan, v):
					needed[draft.Pawn()] = true
				}
			}
			if capture, ok := progress.Action().Capture(); ok && open {
				needed[capture.Capturer()] = true
			}
		}
	}
	fights, err := journal.OpenCombatFights(ctx)
	if err != nil {
		return nil, err
	}
	for _, fight := range fights {
		for pawn := range fight.Roster {
			needed[pawn] = true
		}
	}
	return needed, nil
}

// custodyJobs are the census jobs whose pawn stays drafted though no plan
// names it: an arrest or capture native is carrying out.
var custodyJobs = map[string]bool{"Arrest": true, "Capture": true}

// undraftCandidates are the drafted, live, sane colonists of rows that no
// plan needs (needed) and that run no arrest or capture job, sorted.
func undraftCandidates(rows []*n.PawnState, needed map[domain.PawnID]bool) []domain.PawnID {
	var out []domain.PawnID
	for _, row := range rows {
		if row == nil || row.Pawn == nil {
			continue
		}
		id := domain.PawnID(row.Pawn.GetId())
		if needed[id] || id == "" || slices.Contains(out, id) {
			continue
		}
		if boundary.FactBool(row.Colonist) != domain.Known(true) || boundary.FactBool(row.Drafted) != domain.Known(true) ||
			boundary.FactBool(row.Dead) != domain.Known(false) || boundary.FactBool(row.Downed) != domain.Known(false) ||
			boundary.FactPresence(row.MentalState, row.Issues, "mental_state") != domain.Known(false) {
			continue
		}
		if row.Job == nil || custodyJobs[row.Job.GetDefName()] {
			continue
		}
		out = append(out, id)
	}
	slices.Sort(out)
	return out
}

// idleDrafts are the undraft candidates of a quiet frame: none while the
// census is incomplete or a hostile, hostile building or near hunting
// predator stands (a drafted colonist then waits for the fight's roster),
// and none from a pawn read of another generation.
func idleDrafts(state ControlState, emergency policy.EmergencyFacts, observed *n.PawnSnapshot, needed map[domain.PawnID]bool) ([]domain.PawnID, error) {
	if emergency.ColonistsComplete != domain.Known(true) || len(emergency.Colonists) == 0 || observed == nil {
		return nil, nil
	}
	for _, threat := range emergency.Threats {
		if threat.Kind == policy.Hostile || threat.Building() || threat.Kind == policy.HuntingPredator && !threat.DistantThreat() {
			return nil, nil
		}
	}
	if _, err := boundary.Context(observed.Context, state.Snapshot); err != nil {
		return nil, fmt.Errorf("%w: idleDrafts: pawn read context mismatch", ErrControl)
	}
	return undraftCandidates(observed.GetPawns(), needed), nil
}

// undraftWriter applies the undraft intents.
type undraftWriter interface {
	Apply(context.Context, *c.Identity, []*o.Action) (*o.ApplyReply, bridge.Result, error)
}

// undraft sends one Draft intent per pawn, drafted false, keyed by tick.
// A refusal (the pawn died or left) is logged and retried by the next
// review's sweep if it still stands.
func undraft(ctx context.Context, writer undraftWriter, identity *c.Identity, tick domain.Tick, pawns []domain.PawnID) error {
	if len(pawns) == 0 {
		return nil
	}
	actions := make([]*o.Action, 0, len(pawns))
	for _, pawn := range pawns {
		action, err := bridge.UndraftAction(fmt.Sprintf("undraft-%s-%d", pawn, tick), pawn)
		if err != nil {
			return err
		}
		actions = append(actions, action)
	}
	reply, _, err := writer.Apply(ctx, identity, actions)
	if err != nil {
		return err
	}
	for i, result := range reply.GetResults() {
		verdict, level, detail := "applied", slog.LevelInfo, ""
		if refused := result.GetRefused(); refused != nil {
			verdict, detail = "refused", refused.GetReason()
		} else if failed := result.GetFailed(); failed != nil {
			verdict, level, detail = "failed", slog.LevelWarn, failed.GetDetail()
		}
		defenseAction(ctx, "undraft-sweep", level, verdict, "undraft", string(pawns[i]), map[string]any{"detail": detail})
	}
	return nil
}

// sweepDrafts undrafts every drafted colonist no live plan needs (#939).
// It reads a fresh frame, holds the pawns against the step's other
// planners and sends the Draft intents directly: an undraft owns no plan.
// A reviewer without an actions writer never undrafts.
func (r *Rounder) sweepDrafts(ctx, epoch context.Context, arbiter *stepArbiter) error {
	p := r.player
	state := p.session.State()
	if r.undraft == nil || !state.Enabled {
		return nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return fmt.Errorf("%w: sweepDrafts: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	started := r.clock.Now()
	frame, err := r.native.ReadRoundsFrame(ctx, boundary.Identity(state.Snapshot))
	if err != nil {
		return err
	}
	if _, err = boundary.Context(frame.Context, state.Snapshot); err != nil {
		return fmt.Errorf("%w: sweepDrafts: context mismatch", ErrControl)
	}
	needed, err := plannedDrafts(ctx, p.journal)
	if err != nil {
		return err
	}
	pawns, err := idleDrafts(state, frame.Emergency.Facts, frame.Pawns, needed)
	if err != nil {
		return err
	}
	if len(pawns) == 0 || !arbiter.tryClaim(pawns) {
		return nil
	}
	if err = p.current(ctx, epoch); err != nil {
		return err
	}
	elapsed := r.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.maxAge {
		return fmt.Errorf("%w: sweepDrafts: session changed or read too old", ErrControl)
	}
	return undraft(ctx, r.undraft, boundary.Identity(state.Snapshot), domain.Tick(frame.Context.GetTick()), pawns)
}
