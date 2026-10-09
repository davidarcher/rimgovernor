package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// immediateStep inspects protection from the coherent native frame, before
// layout, reserve acquisition, research and other derived development reads.
// The journal owns scope: omitted Concerns have not been inspected.
func (r *Rounder) immediateStep(ctx, epoch context.Context) (result store.RoundsResult, err error) {
	began := time.Now()
	defer func() {
		d := roundsStepDecision(err, time.Since(began), true)
		d.Attrs["scope"] = "immediate"
		if err == nil && result.Review.Enabled {
			d.Attrs["observation_tick"] = int64(result.Review.Tick)
		}
		telemetry.Decide(ctx, d)
	}()
	p := r.player
	state := p.session.State()
	if !state.Enabled {
		return p.stopRounds(ctx)
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return result, ErrControl
	}
	previous, err := p.journal.LoadRounds(ctx)
	if err != nil {
		return result, err
	}
	r.stage = policy.StageFoothold
	if previous.Stage != nil {
		r.stage = previous.Stage.Stage
	}
	expected, err := stepScope(ctx, r.native)
	if err != nil {
		return result, err
	}
	native, known := expected.NativeGeneration.Value()
	if expected.Colony != state.Snapshot.Colony || expected.Load != state.Snapshot.Load || expected.Map != state.Snapshot.Map || !known || native != state.Snapshot.Native {
		return result, ErrControl
	}
	reading, err := observation.ObserveRoundsProtection(ctx, r.native, r.clock, expected, r.maxAge)
	if err != nil {
		return result, err
	}
	r.publishFrame(expected, reading.Frame)
	if handed, handoffErr := p.expeditionHandoff(ctx, epoch, state, reading); handed || handoffErr != nil {
		if handoffErr != nil {
			return result, handoffErr
		}
		return p.stopRounds(ctx)
	}
	f := &reading.Projection.Facts
	// Saved room exclusions are intent, not a development survey. Preserve
	// killbox/isolation boundaries while building protective allowed areas.
	layout, haveLayout, err := r.layoutPlan(ctx, state.Snapshot, expected.Tick)
	if err != nil {
		return result, err
	}
	if haveLayout {
		reading.Projection.LayoutPlan = domain.Known(layout.Plan)
		if rooms, known := reading.Projection.Rooms.Value(); known {
			reading.Projection.Rooms = domain.Known(policy.MarkEnemyDoors(rooms, layout.Plan.KillboxCells()))
		}
	}
	if r.methodEnabled(policy.MaintainShelter) {
		if f.SafeAreaOwed, err = r.safeArea.review(stockpileWorld(state.Snapshot), reading.Projection); err != nil {
			return result, err
		}
	}
	if f.ShelterCombatants, err = shelterCombatants(ctx, p.journal, playerWorld(state.Snapshot)); err != nil {
		return result, err
	}
	emergency, err := policy.NewEmergencySnapshot(state.Snapshot, expected.Tick, reading.Emergency)
	if err != nil {
		return result, err
	}
	f.Hostiles, f.CriticalPatients = policy.EmergencyNeeds(emergency, state.Snapshot, expected.Tick)
	f.UrgentPatients = policy.UrgentPatients(emergency, state.Snapshot, expected.Tick)
	f.CriticalPatients, f.UrgentPatients = policy.AmputationNeeds(emergency, f.MedicalPawns, f.CriticalPatients, f.UrgentPatients)
	f.DangerSeeds = dangerSeedFact(f.Hostiles, reading.Emergency.Threats)
	seeds, _ := f.DangerSeeds.Value()
	f.DangerWindow = r.safeArea.dangerWindow(stockpileWorld(state.Snapshot), f.Hostiles, seeds, expected.Tick)
	f.DangerHaulers = policy.DangerHaulers(reading.Projection.WorkPawns)
	needed, err := plannedDrafts(ctx, p.journal)
	if err != nil {
		return result, err
	}
	stray, err := idleDrafts(state, reading.Emergency, reading.Frame.Pawns, needed)
	if err != nil {
		return result, err
	}
	f.CleanupPawns = domain.Known(len(stray) > 0)
	f.AvailableMethods = r.methods
	if err = p.current(ctx, epoch); err != nil {
		return result, err
	}
	if p.session.State() != state {
		return result, fmt.Errorf("%w: immediate review authority changed", ErrControl)
	}
	// Native section timestamps come from the frame. Derived ordinary facts
	// are neither recomputed nor published with this review's tick.
	r.census.retain(reading, true, domain.Unknown[[]policy.ConstructionClaim]())
	reading.Sections.File(r.store, facts.Scope{Load: string(expected.Load), Map: int32(expected.Map), Generation: uint64(native)})
	result, err = p.journal.ReviewRounds(ctx, store.RoundsRequest{Revision: previous.Revision, Current: state.Snapshot, Tick: reading.Projection.Identity.Tick, Enabled: true, Immediate: true, Policy: r.policy, Facts: *f})
	if err == nil {
		recordRoundsSnapshot(ctx, state.Snapshot, reading.Projection.Identity.Tick, result, reading.Projection)
	}
	return result, err
}

// immediatePlanner is the protection subset of the existing catalog. Broad
// critical planners such as reserve acquisition and surgery remain ordinary.
func immediatePlanner(e plannerEntry) bool {
	switch e.name {
	case "undraft", defensePlanner, "tend", "rescue", "recovery", "maintainShelter", "fireSafety":
		return true
	}
	return false
}

type immediateReviewKey struct{}
type ordinaryInterruptKey struct{}

func immediateReview(ctx context.Context) bool {
	v, _ := ctx.Value(immediateReviewKey{}).(bool)
	return v
}

func (s *ClockScheduler) immediateConfigured() bool {
	c := s.config
	return c.Rounds != nil && (c.Defense != nil || c.Tend != nil || c.Rescue != nil || c.Recovery != nil || c.MaintainShelter != nil || c.FireSafety != nil)
}

// runPlanners yields the existing player gate between protective admission and
// unrelated review. Hands uses that opportunity through its normal Worker;
// the ordinary wave remains on the same due queue.
func (s *ClockScheduler) runPlanners(call, epoch context.Context, out *ClockSchedulerResult, sel plannerSelectionResult, status *k.Status) ([]string, error) {
	if !s.immediateConfigured() {
		return s.runPlannerWave(call, epoch, out, sel, status, false)
	}
	selected := func(e plannerEntry) bool { return sel.pick == nil || sel.pick(e) }
	immediate := false
	for _, e := range s.queue.entries() {
		immediate = immediate || selected(e) && immediatePlanner(e)
	}
	var names []string
	if immediate {
		urgent := sel
		urgent.pick = func(e plannerEntry) bool { return selected(e) && immediatePlanner(e) }
		var err error
		names, err = s.runPlannerWave(call, epoch, out, urgent, status, true)
		if err != nil {
			return names, err
		}
		// A full selection becomes explicit queue marks before yielding, so
		// an unchanged incident cannot repeatedly select all urgent work and
		// starve development. Recovery's service work remains ordinary.
		s.queue.all = false
		for _, e := range s.queue.entries() {
			if selected(e) && (!immediatePlanner(e) || e.name == "recovery") {
				s.queue.mark(e.name)
			}
		}
		actions, err := s.immediateActions(call)
		if err != nil {
			return names, err
		}
		for _, id := range actions {
			if !slices.Contains(s.protection, id) {
				s.protection = append(s.protection, id)
			}
		}
		out.Immediate = true
		if len(out.HeldBy) > 0 {
			return names, nil
		}
	}
	// A Hands dispatch may have finished between steps while native time is
	// still paused. Yield once more for the normal clock admission before
	// allowing development to occupy the gate.
	pending, awaitNative, err := s.protectionPending(call)
	if err != nil {
		return names, err
	}
	if pending || status.GetRunning() == nil && (awaitNative || immediate && protectionNeedsTicks(out)) {
		out.Immediate = true
		return names, nil
	}
	ordinary := sel
	ordinary.began = s.queue.seq
	ordinary.pick = func(e plannerEntry) bool { return selected(e) && (!immediatePlanner(e) || e.name == "recovery") }
	hasOrdinary := false
	for _, e := range s.queue.entries() {
		hasOrdinary = hasOrdinary || ordinary.pick(e)
	}
	if !hasOrdinary {
		return names, nil
	}
	out.Immediate = false
	more, err := s.runPlannerWave(call, epoch, out, ordinary, status, false)
	if errors.Is(err, context.Canceled) && call.Err() == nil {
		// The native poll interrupted development under standing authority.
		// Classify it as fresh-state reconciliation, not a controller fault.
		err = errors.Join(ErrControl, err)
	}
	return append(names, more...), err
}

func protectionNeedsTicks(out *ClockSchedulerResult) bool {
	return out.Defense != nil && (out.Defense.Plan != "" || out.Defense.Verdict == BuildingReasonExistingWork) ||
		out.Tend != nil && out.Tend.NativeWorkTicks > 0 ||
		out.Rescue != nil && out.Rescue.NativeWorkTicks > 0 ||
		out.FireSafety != nil && out.FireSafety.NativeWorkTicks > 0 || positiveFact(clockShelterHeld(out.Rounds))
}

func (s *ClockScheduler) immediateActions(ctx context.Context) ([]domain.ActionID, error) {
	r, err := s.player.journal.LoadRounds(ctx)
	if err != nil {
		return nil, err
	}
	plans := map[domain.PlanID]bool{}
	for _, binding := range r.Incidents {
		if !policy.ImmediateConcern(binding.Kind) {
			continue
		}
		state, err := s.player.journal.LoadIncident(ctx, binding.Incident)
		if err != nil {
			return nil, err
		}
		for _, method := range state.Methods {
			plans[method.Plan] = true
		}
	}
	for _, binding := range r.Standards {
		if !policy.ImmediateConcern(binding.Concern) {
			continue
		}
		state, err := s.player.journal.LoadStandard(ctx, binding.Standard)
		if err != nil {
			return nil, err
		}
		for _, method := range state.Methods {
			plans[method.Plan] = true
		}
	}
	var actions []domain.ActionID
	kinds := map[domain.ActionKind]bool{}
	for _, entry := range s.queue.entries() {
		if immediatePlanner(entry) {
			for _, kind := range entry.kinds {
				if kind != domain.RecoveryServiceAction {
					kinds[kind] = true
				}
			}
		}
	}
	for id := range plans {
		plan, err := s.player.journal.LoadPlan(ctx, id)
		if err != nil {
			return nil, err
		}
		for _, p := range plan.Progress {
			// Service restoration is ordinary work under the same Incident.
			v := p.View()
			if !plan.Retired && kinds[p.Action().Kind()] && (v.Stage == domain.Pending || v.Stage == domain.Prepared) {
				actions = append(actions, p.View().Action)
			}
		}
	}
	slices.Sort(actions)
	return actions, nil
}

func (s *ClockScheduler) protectionPending(ctx context.Context) (pending, advance bool, err error) {
	if !s.config.Worker || len(s.protection) == 0 {
		return false, false, nil
	}
	plans, err := s.player.journal.LoadPlans(ctx)
	if err != nil {
		return false, false, err
	}
	watched := map[domain.ActionID]bool{}
	for _, id := range s.protection {
		watched[id] = true
	}
	for _, plan := range plans {
		if plan.Retired {
			continue
		}
		for _, p := range plan.Progress {
			v := p.View()
			if !watched[v.Action] {
				continue
			}
			if v.Stage == domain.Pending || v.Stage == domain.Prepared {
				if _, held := v.FreshHold(); !held {
					pending = true
				}
			} else if v.Attempt != 0 && v.Stage != domain.Cancelled && v.Stage != domain.Unsuccessful {
				advance = true
			}
		}
	}
	if !pending {
		s.protection = nil
	}
	return pending, advance, nil
}
