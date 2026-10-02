package buildingruntime

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// RoutineWorkBenchSource is the optional fresh bench census a work planner
// resolves open production bills against; without it bills contribute no
// work requirement (Construction alone, the pre-bill behaviour).
type RoutineWorkBenchSource interface {
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
}

type RoutineWorkPlanner struct {
	reviewer *RoutineReviewer
	benches  RoutineWorkBenchSource
}
type RoutineWorkResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineWorkPlanner(reviewer *RoutineReviewer) (*RoutineWorkPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoutineWorkPlanner: reviewer == nil", ErrControl)
	}
	benches, _ := reviewer.native.(RoutineWorkBenchSource)
	return &RoutineWorkPlanner{reviewer: reviewer, benches: benches}, nil
}
func (r *RoutineWorkPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineWorkResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineWorkResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineWorkResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineWorkResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	deficit := false
	for _, binding := range review.Goals {
		switch binding.Need {
		case policy.EnsureWorkAssignments:
			goal, err = p.journal.LoadGoal(call, binding.Goal)
		case policy.MaintainResource:
			// A resource deficit needs its bench work type covered before
			// the bill can be admitted natively (routineDeficitWork).
			var resource store.GoalState
			resource, err = p.journal.LoadGoal(call, binding.Goal)
			deficit = err == nil && resource.Goal.Status == domain.GoalActive && resource.Goal.Need == domain.NeedDeficit
		}
		if err != nil {
			return RoutineWorkResult{}, err
		}
	}
	if goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit || review.Veto(goal.Goal) != "" {
		return RoutineWorkResult{Reason: BuildingMethodNoDeficit}, nil
	}
	// Open work no longer gates the fresh decision outright: an undispatched
	// action whose premise moved (what the policy now wants for the pawn) is cancelled below so the goal can
	// re-plan instead of holding the stale plan forever (#305).
	var open []store.PlanState
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineWorkResult{}, err
		}
		if store.PlanOpen(plan) {
			open = append(open, plan)
		}
	}
	existing := func(unknown RoutineWorkResult) RoutineWorkResult {
		if len(open) > 0 {
			clockSchedulerLog("Work.step: open plan kept, fresh decision unknown reason=%v", unknown.Reason)
			return RoutineWorkResult{Reason: BuildingMethodExistingWork}
		}
		return unknown
	}
	plans, err := p.journal.LoadPlans(call, 256)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	playerPlans, err := p.journal.PlayerPlans(call, playerWorld(state.Snapshot))
	if err != nil {
		return RoutineWorkResult{}, err
	}
	definitions := routineProjectDefinitions(plans, state.Snapshot, playerPlans)
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineWorkResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims, definitions...)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	pawns, known := read.Projection.WorkPawns.Value()
	if !known {
		return existing(RoutineWorkResult{Reason: BuildingMethodUnknown}), nil
	}
	squad, err := reviewSoldierSquad(call, p.journal, state.Snapshot, pawns)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	required, known := routineProjectWork(definitions, read.Projection.Definitions).Value()
	if !known {
		return existing(RoutineWorkResult{Reason: BuildingMethodUnknown}), nil
	}
	resourceTargets, err := r.reviewer.resourceTargets(call, state.Snapshot, read.Projection.Facts.Resources)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	targets := routineDeficitTargets(resourceTargets, deficit, read.Projection.Facts.Gear)
	benchWork, err := routineBenchWork(call, r.reviewer.benchSource(r.benches, expected, false), state.Snapshot, plans, playerPlans, targets, len(targets) > 0)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	if rows, known := benchWork.Value(); !known {
		return existing(RoutineWorkResult{Reason: BuildingMethodUnknown}), nil
	} else {
		required = mergeWorkRequirements(required, rows)
	}
	needs, err := routineResearchNeeds(call, p.journal, r.reviewer.policy, state.Snapshot)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	needs = r.reviewer.censusResearchNeeds(needs)
	required = mergeWorkRequirements(required, routineResearchWork(policy.ArmorResearchPolicy(r.reviewer.policy, review.Latches.Soldiers), needs, read.Projection.Facts.Research))
	required = mergeWorkRequirements(required, fishingWork(read.Projection))
	demand, err := routineDiseaseDemand(read.Projection, definitions, review, state.Snapshot)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	decision, err := policy.PlanWork(pawns, required, demand)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	_, known = decision.Capacity.Value()
	if !known {
		return existing(RoutineWorkResult{Reason: BuildingMethodUnknown}), nil
	}
	byID := map[policy.PawnID]policy.WorkPawn{}
	for _, pawn := range pawns {
		byID[pawn.ID] = pawn
	}
	// The schedule planner rides the same WorkSettingsIntent: a pawn whose
	// timetable differs from its role template gets the timetable in the
	// same assignment as its priorities (or alone), under the same token.
	schedules := map[policy.PawnID][]string{}
	meditate, _ := read.Projection.MeditateAvailable.Value()
	for _, row := range policy.PlanSchedules(pawns, read.Projection.Facts.Comfort, meditate).Schedules {
		if !row.Matches {
			schedules[row.Pawn] = row.Slots
		}
	}
	var work []domain.WorkAssignment
	for _, assignment := range decision.Assignments {
		pawn := byID[assignment.Pawn]
		manual, mk := pawn.Manual.Value()
		// Native flips numbered priorities on at attach (#1276); a pawn still
		// reading checkbox mode is unknown this round, never written.
		if _, ck := pawn.Work.Value(); !mk || !ck || !manual {
			continue
		}
		if defs := policy.FoodPolicyChanges(pawn); len(defs) > 0 {
			w, err := domain.NewFoodAssignment(domain.PawnID(pawn.ID), defs)
			if err != nil {
				return RoutineWorkResult{}, err
			}
			work = append(work, w)
			continue
		}
		changed, ok := policy.WorkChanges(pawn, assignment)
		if !ok {
			return RoutineWorkResult{}, fmt.Errorf("%w: step: !ok", ErrControl)
		}
		schedule := schedules[assignment.Pawn]
		if len(changed) == 0 && len(schedule) == 0 {
			continue
		}
		var w domain.WorkAssignment
		if len(schedule) == 0 {
			w, err = domain.NewWorkAssignment(domain.PawnID(assignment.Pawn), changed)
		} else {
			w, err = domain.NewScheduleAssignment(domain.PawnID(assignment.Pawn), changed, schedule)
		}
		if err != nil {
			return RoutineWorkResult{}, err
		}
		work = append(work, w)
	}
	// Hostility responses (#1299) ride the same goal: one PawnSettingsIntent
	// per colonist whose response differs from the one it should hold.
	// Self-tend (#1305) rides it too.
	settings := policy.HostilityChanges(hostilityRows(pawns), read.Emergency.Threats)
	settings = append(settings, policy.SelfTendChanges(selfTendRows(pawns))...)
	// Medicine carry (#1307) too: counts by role, capped above the reserve.
	reserve, err := policy.ReviewMedicalReserve(read.Projection.Facts.MedicalReserve, false, r.reviewer.policy.MedicalReserve)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	settings = append(settings, policy.MedicineCarryChanges(policy.MedicineCarryRows(pawns), reserve.Stock, reserve.Target)...)
	// Unique short names (#1310): a newer owned pawn holding an older
	// one's short name is renamed from its own name bank.
	if names, ok := read.Projection.Facts.OwnedNames.Value(); ok {
		settings = append(settings, policy.NicknameChanges(names)...)
	}
	// Medical care caps (#1301) ride it too: colonists, prisoners, guests
	// and animals, at most eight per plan.
	care := policy.MedicalCareChanges(read.Projection.Facts, r.reviewer.policy.MedicalReserve)
	if len(care) > 8 {
		care = care[:8]
	}
	settings = append(settings, care...)
	// Per-pawn reading policies (#1306): the contents first, then the
	// assignment, at most eight pawns per plan.
	var reading []domain.ReadingPolicy
	if changes := routineReadingChanges(read.Projection.Policies, read.Projection.Facts.OwnedNames, pawns); len(changes) > 0 {
		if len(changes) > 8 {
			changes = changes[:8]
		}
		for _, c := range changes {
			if c.Write != nil {
				reading = append(reading, *c.Write)
			}
			if c.Assign != nil {
				settings = append(settings, *c.Assign)
			}
		}
	}
	// Per-pawn drug policies (#1537) the same way.
	var drugs []domain.DrugPolicy
	if changes := routineDrugChanges(read.Projection.Policies, read.Projection.Facts.OwnedNames, pawns, read.Projection.Facts.Resources, squad); len(changes) > 0 {
		if len(changes) > 8 {
			changes = changes[:8]
		}
		for _, c := range changes {
			if c.Write != nil {
				drugs = append(drugs, *c.Write)
			}
			if c.Assign != nil {
				settings = append(settings, *c.Assign)
			}
		}
	}
	for _, plan := range open {
		if err := cancelStaleWorkActions(call, p.journal, plan, work, settings); err != nil {
			return RoutineWorkResult{}, err
		}
		if plan, err = p.journal.LoadPlan(call, plan.Spec.ID()); err != nil {
			return RoutineWorkResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineWorkResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	if len(work) == 0 && len(settings) == 0 && len(reading) == 0 && len(drugs) == 0 {
		return RoutineWorkResult{Reason: BuildingMethodUnknown}, nil
	}
	// Staleness above judged every pawn; the plan itself carries at most eight.
	if len(work) > 8 {
		work = work[:8]
	}
	// The goal epoch is part of the identity: the same edit in a new epoch
	// is a fresh method, not the used one.
	hash := sha256.New()
	fmt.Fprintf(hash, "epoch:%d\n", goal.Goal.Epoch)
	for _, w := range work {
		data, _ := json.Marshal(w.Settings())
		fmt.Fprintf(hash, "%s/%s\n", w.Pawn(), data)
		if defs := w.FoodAllow(); len(defs) > 0 {
			// An identical Manual edit after a completed repair needs another
			// method even when the settings token returns to its old value.
			fmt.Fprintf(hash, "food/%q/%d\n", defs, len(goal.Methods))
		}
		// A timetable is not in Settings(): without it a schedule-only
		// change repeats an earlier method (a Drowsy extension and its
		// revert, #1318) and reads as used, so it never applies.
		if slots := w.Schedule(); len(slots) > 0 {
			fmt.Fprintf(hash, "schedule/%q/%d\n", slots, len(goal.Methods))
		}
	}
	for _, s := range settings {
		carry, _ := s.MedicineCarry()
		book, _ := s.ReadingPolicy()
		drug, _ := s.DrugPolicy()
		fmt.Fprintf(hash, "%s/%s/%s/%t/%q/%d/%s/%q/%q/%d\n", s.Kind(), s.Pawn(), s.Hostility(), s.SelfTend(), s.LeaveName(), carry, s.MedicalCare(), book, drug, len(goal.Methods))
	}
	for _, d := range drugs {
		data, _ := json.Marshal(d.Entries())
		fmt.Fprintf(hash, "drugs/%q/%s/%d\n", d.Name(), data, len(goal.Methods))
	}
	for _, r := range reading {
		fmt.Fprintf(hash, "reading/%q/%q/%d\n", r.Name(), r.Definitions(), len(goal.Methods))
	}
	method := domain.MethodID(fmt.Sprintf("work-%x", hash.Sum(nil)[:16]))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineWorkResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineWorkResult{}, err
	}
	id := domain.MintPlanID()
	var actions []domain.Action
	for i, w := range work {
		action, err := domain.NewWorkAssignmentAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), w)
		if err != nil {
			return RoutineWorkResult{}, err
		}
		actions = append(actions, action)
	}
	for _, r := range reading {
		action, err := domain.NewReadingPolicyAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), r)
		if err != nil {
			return RoutineWorkResult{}, err
		}
		actions = append(actions, action)
	}
	for _, d := range drugs {
		action, err := domain.NewDrugPolicyAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), d)
		if err != nil {
			return RoutineWorkResult{}, err
		}
		actions = append(actions, action)
	}
	for _, s := range settings {
		action, err := domain.NewPawnSettingsAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), s)
		if err != nil {
			return RoutineWorkResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineWorkResult{}, err
	}
	if p.session.State() != state {
		return RoutineWorkResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineWorkResult{}, err
	}
	return RoutineWorkResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// cancelStaleWorkActions cancels every undispatched work assignment on the
// plan whose premise no longer holds against the fresh decision: the pawn's
// the pawn dropped out of the decision, or the policy
// now wants a different priority for a work type the action sets. A pending
// action the fresh decision still agrees with stays open.
func cancelStaleWorkActions(ctx context.Context, journal *store.Store, plan store.PlanState, fresh []domain.WorkAssignment, settings []domain.PawnSettings) error {
	wantedSettings := map[domain.PawnSettings]bool{}
	for _, s := range settings {
		wantedSettings[s] = true
	}
	wanted := map[domain.PawnID]domain.WorkAssignment{}
	for _, w := range fresh {
		wanted[w.Pawn()] = w
	}
	assignments := map[domain.ActionID]domain.WorkAssignment{}
	staleSettings := map[domain.ActionID]bool{}
	for _, action := range plan.Spec.Actions() {
		if w, ok := action.WorkAssignment(); ok {
			assignments[action.ID()] = w
		}
		if s, ok := action.PawnSettings(); ok {
			staleSettings[action.ID()] = !wantedSettings[s]
		}
	}
	for _, progress := range plan.Progress {
		v := progress.View()
		if v.Stage != domain.Pending && v.Stage != domain.Prepared {
			continue
		}
		stale, isSetting := staleSettings[v.Action]
		if w, ok := assignments[v.Action]; ok {
			stale = workActionStale(w, wanted)
		} else if !isSetting {
			continue
		}
		if !stale {
			continue
		}
		if _, err := journal.Cancel(ctx, plan.Spec.ID(), v.Action); err != nil {
			return err
		}
	}
	return nil
}

func workActionStale(w domain.WorkAssignment, wanted map[domain.PawnID]domain.WorkAssignment) bool {
	now, ok := wanted[w.Pawn()]
	if !ok {
		return true
	}
	if !slices.Equal(w.FoodAllow(), now.FoodAllow()) {
		return true
	}
	values := map[string]int32{}
	for _, setting := range now.Settings() {
		values[setting.Definition] = setting.Priority
	}
	for _, setting := range w.Settings() {
		if want, ok := values[setting.Definition]; !ok || want != setting.Priority {
			return true
		}
	}
	return false
}

// hostilityRows are the work census's hostility inputs (#1299).
func hostilityRows(pawns []policy.WorkPawn) []policy.HostilityPawn {
	out := make([]policy.HostilityPawn, 0, len(pawns))
	for _, p := range pawns {
		out = append(out, p.HostilityPawn())
	}
	return out
}

// selfTendRows are the work census's self-tend inputs (#1305).
func selfTendRows(pawns []policy.WorkPawn) []policy.SelfTendPawn {
	out := make([]policy.SelfTendPawn, 0, len(pawns))
	for _, p := range pawns {
		out = append(out, p.SelfTendPawn())
	}
	return out
}

// selfTendPawns lifts the work census into the review's self-tend rows.
func selfTendPawns(pawns domain.Fact[[]policy.WorkPawn]) domain.Fact[[]policy.SelfTendPawn] {
	rows, known := pawns.Value()
	if !known {
		return domain.Unknown[[]policy.SelfTendPawn]()
	}
	return domain.Known(selfTendRows(rows))
}

// hostilityPawns lifts the work census into the review's hostility rows.
func hostilityPawns(pawns domain.Fact[[]policy.WorkPawn]) domain.Fact[[]policy.HostilityPawn] {
	rows, known := pawns.Value()
	if !known {
		return domain.Unknown[[]policy.HostilityPawn]()
	}
	return domain.Known(hostilityRows(rows))
}

// routineReadingChanges is the reading policy planner's input lift (#1306);
// none while the policy databases or the owned-pawn names are unknown.
func routineReadingChanges(policies domain.Fact[observation.Policies], names domain.Fact[[]policy.OwnedName], pawns []policy.WorkPawn) []policy.ReadingPolicyChange {
	p, ok := policies.Value()
	owned, named := names.Value()
	if !ok || !named || len(p.Books) == 0 {
		return nil
	}
	entries := make([]policy.ReadingPolicyEntry, 0, len(p.Reading))
	for _, e := range p.Reading {
		entries = append(entries, policy.ReadingPolicyEntry{ID: e.ID, Label: e.Label, Pawns: e.Pawns, Allowed: e.Allowed})
	}
	return policy.ReadingPolicyChanges(pawns, owned, entries, p.Books)
}

// routineDrugChanges is the drug policy planner's input lift (#1537), with
// the colony item census as drug stock (#1538); none
// while the policy databases or the owned-pawn names are unknown.
func routineDrugChanges(policies domain.Fact[observation.Policies], names domain.Fact[[]policy.OwnedName], pawns []policy.WorkPawn, stock domain.Fact[[]policy.Amount], squad policy.SoldierSquad) []policy.DrugPolicyChange {
	p, ok := policies.Value()
	owned, named := names.Value()
	if !ok || !named {
		return nil
	}
	entries := make([]policy.DrugPolicyEntry, 0, len(p.Drug))
	for _, e := range p.Drug {
		entries = append(entries, policy.DrugPolicyEntry{ID: e.ID, Label: e.Label, Pawns: e.Pawns, Entries: e.Drugs})
	}
	return policy.DrugPolicyChanges(pawns, owned, entries, stock, p.BiomeDiseases, squad)
}
