package buildingruntime

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// RoundsWorkBenchSource is the optional fresh bench census a work planner
// resolves open production bills against; without it bills contribute no
// work requirement (Construction alone, the pre-bill behaviour).
type RoundsWorkBenchSource interface {
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
}

type RoundsWorkPlanner struct {
	reviewer *Rounder
	benches  RoundsWorkBenchSource
}
type RoundsWorkResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsWorkPlanner(reviewer *Rounder) (*RoundsWorkPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoundsWorkPlanner: reviewer == nil", ErrControl)
	}
	benches := reviewer.native.(RoundsWorkBenchSource)
	return &RoundsWorkPlanner{reviewer: reviewer, benches: benches}, nil
}
func (r *RoundsWorkPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsWorkResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsWorkResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoundsWorkResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsWorkResult{Verdict: BuildingReasonNoReview}, nil
	}
	deficit := false
	for _, binding := range review.Goals {
		if binding.Need != policy.MaintainResource {
			continue
		}
		// A resource deficit needs its bench work type covered before
		// the bill can be admitted natively (roundsDeficitWork).
		resource, err := p.journal.LoadStandard(call, binding.Goal)
		if err != nil {
			return RoundsWorkResult{}, err
		}
		deficit = resource.OwnerDeficit()
	}
	goal, deficitProject, err := p.journal.WorkableProject(call, review, policy.EnsureWorkAssignments)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	if !deficitProject {
		return RoundsWorkResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// Open work no longer gates the fresh decision outright: an undispatched
	// action whose premise moved (what the policy now wants for the pawn) is cancelled below so the goal can
	// re-plan instead of holding the stale plan forever (#305).
	var open []store.PlanState
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsWorkResult{}, err
		}
		if store.PlanOpen(plan) {
			open = append(open, plan)
		}
	}
	existing := func(unknown RoundsWorkResult) RoundsWorkResult {
		if len(open) > 0 {
			clockSchedulerLog("Work.step: open plan kept, fresh decision unknown reason=%v", unknown.Verdict)
			return RoundsWorkResult{Verdict: BuildingReasonExistingWork}
		}
		return unknown
	}
	plans, err := p.journal.LoadPlans(call, 256)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	playerPlans, err := p.journal.PlayerPlans(call, playerWorld(state.Snapshot))
	if err != nil {
		return RoundsWorkResult{}, err
	}
	definitions := roundsProjectDefinitions(plans, state.Snapshot, playerPlans)
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsWorkResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims, definitions...)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	pawns, known := read.Projection.WorkPawns.Value()
	if !known {
		return existing(RoundsWorkResult{Verdict: fieldUnavailable("work_pawns")}), nil
	}
	squad, err := reviewSoldierSquad(call, p.journal, state.Snapshot, pawns)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	required, known := roundsProjectWork(definitions, read.Projection.Definitions).Value()
	if !known {
		return existing(RoundsWorkResult{Verdict: fieldUnavailable("project_work")}), nil
	}
	resourceTargets, err := r.reviewer.resourceTargets(call, state.Snapshot, read.Projection.Facts.Resources)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	targets, err := roundsDeficitTargets(resourceTargets, deficit, read.Projection.Facts.Gear)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	benchWork, err := roundsBenchWork(call, r.reviewer.benchSource(r.benches, expected, false), state.Snapshot, plans, playerPlans, targets, len(targets) > 0, read.Projection.Facts.Items.Wort)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	if rows, known := benchWork.Value(); !known {
		return existing(RoundsWorkResult{Verdict: fieldUnavailable("bench_work")}), nil
	} else {
		required = mergeWorkRequirements(required, rows)
	}
	needs, err := roundsResearchNeeds(call, p.journal, r.reviewer.policy, read.Projection.Facts.Items, state.Snapshot)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	needs = r.reviewer.censusResearchNeeds(needs)
	required = mergeWorkRequirements(required, roundsResearchWork(policy.ArmorResearchPolicy(r.reviewer.policy, review.Latches.Soldiers), needs, read.Projection.Facts.Research))
	required = mergeWorkRequirements(required, fishingWork(read.Projection))
	study, known := studyWork(call, read.Projection).Value()
	if !known {
		return existing(RoundsWorkResult{Verdict: fieldUnavailable("study_work")}), nil
	}
	required = mergeWorkRequirements(required, study)
	demand, err := roundsDiseaseDemand(read.Projection, definitions, review, state.Snapshot)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	decision, err := policy.PlanWork(pawns, required, demand)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	_, known = decision.Capacity.Value()
	if !known {
		return existing(RoundsWorkResult{Verdict: fieldUnavailable("work_capacity")}), nil
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
	for _, row := range policy.PlanSchedulesHeld(pawns, read.Projection.Facts.Comfort, meditate, policy.HeldOffSleep(read.Projection.Facts)).Schedules {
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
		changed, ok := policy.WorkChanges(pawn, assignment)
		if !ok {
			return RoundsWorkResult{}, fmt.Errorf("%w: step: !ok", ErrControl)
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
			return RoundsWorkResult{}, err
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
		return RoundsWorkResult{}, err
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
	// Mech control groups and work modes (#1736).
	mechs, err := roundsMechSettings(read)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	settings = append(settings, mechs...)
	// Per-pawn reading policies (#1306): the contents first, then the
	// assignment, at most eight pawns per plan.
	var reading []domain.ReadingPolicy
	if changes := roundsReadingChanges(read.Projection.Policies, read.Projection.Facts.OwnedNames, pawns); len(changes) > 0 {
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
	if changes := roundsDrugChanges(read.Projection.Facts.Items, read.Projection.Policies, read.Projection.Facts.OwnedNames, pawns, read.Projection.Facts.Prisoners, read.Projection.Facts.Resources, squad); len(changes) > 0 {
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
	// Per-pawn food policies (#1541) likewise.
	var food []domain.FoodPolicy
	if changes := roundsDietChanges(read.Projection.Facts.Ideology, read.Projection.Policies, read.Projection.Facts.OwnedNames, pawns, read.Projection.Facts.FoodReserve); len(changes) > 0 {
		if len(changes) > 8 {
			changes = changes[:8]
		}
		for _, c := range changes {
			if c.Write != nil {
				food = append(food, *c.Write)
			}
			if c.Assign != nil {
				settings = append(settings, *c.Assign)
			}
		}
	}
	for _, plan := range open {
		if err := cancelStaleWorkActions(call, p.journal, plan, work, settings); err != nil {
			return RoundsWorkResult{}, err
		}
		if plan, err = p.journal.LoadPlan(call, plan.Spec.ID()); err != nil {
			return RoundsWorkResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsWorkResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	if len(work) == 0 && len(settings) == 0 && len(reading) == 0 && len(drugs) == 0 && len(food) == 0 {
		return RoundsWorkResult{Verdict: fieldUnavailable("work_assignments")}, nil
	}
	// Staleness above judged every pawn; the plan itself carries at most eight.
	if len(work) > 8 {
		work = work[:8]
	}
	hash := sha256.New()
	for _, w := range work {
		data, _ := json.Marshal(w.Settings())
		fmt.Fprintf(hash, "%s/%s\n", w.Pawn(), data)
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
		diet, _ := s.FoodPolicy()
		mode, _ := s.MechWorkMode()
		group, _ := s.MechControlGroup()
		fmt.Fprintf(hash, "%s/%s/%s/%t/%q/%d/%s/%q/%q/%q/%q/%d/%d\n", s.Kind(), s.Pawn(), s.Hostility(), s.SelfTend(), s.LeaveName(), carry, s.MedicalCare(), book, drug, diet, mode, group, len(goal.Methods))
	}
	for _, d := range drugs {
		data, _ := json.Marshal(d.Entries())
		fmt.Fprintf(hash, "drugs/%q/%s/%d\n", d.Name(), data, len(goal.Methods))
	}
	for _, r := range reading {
		fmt.Fprintf(hash, "reading/%q/%q/%d\n", r.Name(), r.Definitions(), len(goal.Methods))
	}
	for _, f := range food {
		fmt.Fprintf(hash, "food/%q/%q/%d\n", f.Name(), f.Definitions(), len(goal.Methods))
	}
	method := domain.MethodID(fmt.Sprintf("work-%x", hash.Sum(nil)[:16]))
	if _, err = p.journal.LoadOwnerMethod(call, goal, method); err == nil {
		return RoundsWorkResult{Verdict: waitFor(WaitMethodUsed, "work_method")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsWorkResult{}, err
	}
	id := domain.MintPlanID()
	var actions []domain.Action
	for i, w := range work {
		action, err := domain.NewWorkAssignmentAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), w)
		if err != nil {
			return RoundsWorkResult{}, err
		}
		actions = append(actions, action)
	}
	for _, r := range reading {
		action, err := domain.NewReadingPolicyAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), r)
		if err != nil {
			return RoundsWorkResult{}, err
		}
		actions = append(actions, action)
	}
	for _, d := range drugs {
		action, err := domain.NewDrugPolicyAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), d)
		if err != nil {
			return RoundsWorkResult{}, err
		}
		actions = append(actions, action)
	}
	for _, f := range food {
		action, err := domain.NewFoodPolicyAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), f)
		if err != nil {
			return RoundsWorkResult{}, err
		}
		actions = append(actions, action)
	}
	for _, s := range settings {
		action, err := domain.NewPawnSettingsAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), s)
		if err != nil {
			return RoundsWorkResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsWorkResult{}, err
	}
	if p.session.State() != state {
		return RoundsWorkResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	if _, err = p.journal.CommitProjectMethod(call, goal.Project.ID, goal.Revision, method, "", plan); err != nil {
		return RoundsWorkResult{}, err
	}
	return RoundsWorkResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
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

// roundsReadingChanges is the reading policy planner's input lift (#1306);
// none while the policy databases or the owned-pawn names are unknown.
func roundsReadingChanges(policies domain.Fact[observation.Policies], names domain.Fact[[]policy.OwnedName], pawns []policy.WorkPawn) []policy.ReadingPolicyChange {
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

// roundsDrugChanges is the drug policy planner's input lift (#1537), with
// the colony item census as drug stock (#1538); none
// while the policy databases or the owned-pawn names are unknown.
func roundsDrugChanges(items policy.ItemFacts, policies domain.Fact[observation.Policies], names domain.Fact[[]policy.OwnedName], pawns []policy.WorkPawn, prisoners domain.Fact[[]policy.PrisonerFacts], stock domain.Fact[[]policy.Amount], squad policy.SoldierSquad) []policy.DrugPolicyChange {
	p, ok := policies.Value()
	owned, named := names.Value()
	if !ok || !named {
		return nil
	}
	entries := make([]policy.DrugPolicyEntry, 0, len(p.Drug))
	for _, e := range p.Drug {
		entries = append(entries, policy.DrugPolicyEntry{ID: e.ID, Label: e.Label, Pawns: e.Pawns, Entries: e.Drugs})
	}
	held, _ := prisoners.Value()
	return policy.DrugPolicyChanges(items, pawns, held, owned, entries, stock, p.BiomeDiseases, squad)
}

// roundsDietChanges is the food policy planner's input lift (#1541); none
// while the policy databases or the owned-pawn names are unknown.
func roundsDietChanges(ideology domain.Fact[policy.Ideoligion], policies domain.Fact[observation.Policies], names domain.Fact[[]policy.OwnedName], pawns []policy.WorkPawn, reserve domain.Fact[policy.FoodReserveReview]) []policy.FoodPolicyChange {
	p, ok := policies.Value()
	owned, named := names.Value()
	if !ok || !named || len(p.Foods) == 0 {
		return nil
	}
	entries := make([]policy.FoodPolicyEntry, 0, len(p.Food))
	for _, e := range p.Food {
		entries = append(entries, policy.FoodPolicyEntry{ID: e.ID, Label: e.Label, Pawns: e.Pawns, Allowed: e.Allowed})
	}
	stock, _ := reserve.Value()
	return policy.DietPolicyChanges(ideology, pawns, p.FoodEaters, owned, entries, p.Foods, stock.Short)
}
