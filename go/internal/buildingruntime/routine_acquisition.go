package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type RoutineAcquisitionPlanner struct {
	reviewer *RoutineReviewer
	need     policy.GoalID
}

type RoutineAcquisitionResult struct {
	Verdict
	Plan domain.PlanID
}

// NewRoutineAcquisitionPlanner plans one acquisition goal: EnsureFoodSupply
// harvests and hunts toward its food plan; ClearPests
// (#247) hunts every recognised pest the wild-animal census reports, one
// hunt method per admission, until none remain; MaintainResource chops,
// forages and hunts toward its ranked floors through the acquisition
// catalog (#728), beside RoutineResourcePlanner's bills and mines.
func NewRoutineAcquisitionPlanner(reviewer *RoutineReviewer, need policy.GoalID) (*RoutineAcquisitionPlanner, error) {
	if reviewer == nil || (need != policy.EnsureFoodSupply && need != policy.ClearPests && need != policy.MaintainResource) {
		return nil, fmt.Errorf("%w: NewRoutineAcquisitionPlanner: reviewer == nil || (need != policy.EnsureFoodSupply && need != policy.ClearPests && need != policy.Maintain", ErrControl)
	}
	return &RoutineAcquisitionPlanner{reviewer: reviewer, need: need}, nil
}
func (r *RoutineAcquisitionPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineAcquisitionResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineAcquisitionResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineAcquisitionResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineAcquisitionResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, r.need)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	if !workable {
		return RoutineAcquisitionResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	if goal.Goal.Priority >= 3 {
		selected := false
		for _, row := range review.Development.Rows {
			selected = selected || row.Goal == r.need && row.Selected
		}
		if !selected {
			return RoutineAcquisitionResult{Verdict: awaitingSlot(string(r.need))}, nil
		}
	}
	plans, err := p.journal.LoadPlans(call, 256)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	playerPlans, err := p.journal.PlayerPlans(call, playerWorld(state.Snapshot))
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	definitions := routineProjectDefinitions(plans, state.Snapshot, playerPlans)
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineAcquisitionResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims, definitions...)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	projection := read.Projection
	projection.Acquisition = withoutFieldSources(projection.Acquisition, projection, plans, state.Snapshot)
	pest := r.need == policy.ClearPests
	stockGoal := r.need == policy.MaintainResource
	food := r.need == policy.EnsureFoodSupply
	huntOnly := false
	pests := policy.PestCensus(projection.Facts.AnimalUpkeep.WildAnimals)
	stalledSources := map[string]bool{}
	// A source a stall rotated away from stays keyed out for its contract's
	// bounded cooldown (#629): the goal's progress record carries the key,
	// and RecordProgressCooldown adds one under this review's revision.
	progress, _ := review.GoalProgress(r.need)
	cooled := map[string]bool{}
	// undispatched holds the goal's admitted acquisitions native has not
	// designated yet (Pending/Prepared): they block and hold their source
	// like a designated census row until dispatch, when the census takes
	// over (#1045). Without it the gap re-admits the same source.
	undispatched := map[string]bool{}
	cool := func(contract policy.ProgressContract, thing string) error {
		key := policy.CooldownKey(contract.Method, thing)
		cooled[thing] = true
		updated, err := p.journal.RecordProgressCooldown(call, review.Revision, r.need, key, contract.CooldownUntil(expected.Tick))
		if errors.Is(err, store.ErrConflict) {
			return nil
		}
		if err == nil {
			review = updated
		}
		return err
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineAcquisitionResult{}, err
		}
		if pest {
			// An unissued pest hunt follows its animal wherever the census
			// reports it; one whose animal died to the defense or left the
			// map would be held forever and the goal behind it, so it is
			// cancelled at once.
			for _, action := range gonePestHunts(plan.Progress, projection.Facts.AnimalUpkeep.WildAnimals) {
				if _, err = p.journal.Cancel(call, method.Plan, action); err != nil {
					return RoutineAcquisitionResult{}, err
				}
			}
		}
		for thing := range undispatchedAcquisitions(plan.Progress) {
			undispatched[thing] = true
		}
	}
	// Stalls are read from the census alone (#1044, #1046): any designated
	// row of the goal's kind, untaken past its contract since native first
	// saw it, the player's own included (#719). Each is withdrawn by its own
	// one-action method, admitted before re-selection and using up this
	// step's admission. A pest hunt follows its animal (#321) and native
	// settles it on the animal's death or departure, so the hunt-stall rule
	// is not its exit (#455): the pest goal withdraws nothing.
	if !pest {
		mine := func(row policy.AcquisitionSource) bool { return food && row.Food }
		if stockGoal {
			targets, err := r.reviewer.resourceTargets(call, state.Snapshot, projection.Facts.Resources)
			if err != nil {
				return RoutineAcquisitionResult{}, err
			}
			mine = func(row policy.AcquisitionSource) bool { _, ok := targets[policy.Resource(row.Resource)]; return ok }
		}
		for _, hunt := range []bool{false, true} {
			contract := r.reviewer.policy.AcquisitionProgress()
			if hunt {
				contract = r.reviewer.policy.HuntProgress()
			}
			for _, row := range stalledDesignations(projection.Acquisition, hunt, expected.Tick, contract, mine) {
				stalledSources[row.ID] = true
				method, plan, err := stallWithdraw(row)
				if err != nil {
					return RoutineAcquisitionResult{}, err
				}
				if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
					continue
				} else if !errors.Is(err, store.ErrNotFound) {
					return RoutineAcquisitionResult{}, err
				}
				if err = cool(contract, row.ID); err != nil {
					return RoutineAcquisitionResult{}, err
				}
				if err = p.current(call, epoch); err != nil {
					return RoutineAcquisitionResult{}, err
				}
				if p.session.State() != state {
					return RoutineAcquisitionResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
				}
				if _, err = p.journal.CommitGoalMethodReason(call, goal.Goal.ID, goal.Revision, method, "withdraw stalled "+row.Resource, plan); err != nil {
					return RoutineAcquisitionResult{}, err
				}
				return RoutineAcquisitionResult{Verdict: BuildingReasonAdmitted, Plan: plan.ID()}, nil
			}
		}
	}
	// Sources under a cooldown are passed over like held ones: the cooldown
	// lifts by itself at its bound, never a permanent ban (#629).
	held := map[string]bool{}
	if rows, known := projection.Acquisition.Value(); known && !pest {
		for _, row := range rows {
			if cooled[row.ID] || progress.Cooled(policy.CooldownKey(r.reviewer.policy.HuntProgress().Method, row.ID), expected.Tick) || progress.Cooled(policy.CooldownKey(r.reviewer.policy.AcquisitionProgress().Method, row.ID), expected.Tick) {
				held[row.ID] = true
			}
		}
	}
	// Blocking reads the census, not the journal (#1045): a designated row
	// the goal would plan is its ExistingWork. A source this step cancelled
	// or cooled still reads designated until native withdraws it, and never
	// blocks. ClearPests is never blocked: a hunt already designated holds
	// its own animal (held, below) and counts against the two outstanding
	// hunts (slots), but does not stop the next animal being planned (run 9
	// of #247). EnsureFoodSupply's hunts are the exception (#260): a forage
	// batch harvests one bush at a time for days while the hunt rows the
	// butcher spot and bill were placed for wait behind it, so designated
	// plant harvests leave the hunt slots plannable and only hunts are
	// admitted over them. MaintainResource skips a designated resource in
	// resourceSelection instead; its other targets still plan.
	if !pest && !stockGoal {
		block, plants := censusBlocking(projection.Acquisition, food, held, undispatched)
		if block {
			return RoutineAcquisitionResult{Verdict: BuildingReasonExistingWork}, nil
		}
		huntOnly = plants
	}
	// A designated or undispatched source is held: it is not planned again
	// (#1045). Its resource is busy for MaintainResource unless a cooldown
	// held it.
	busy := map[string]bool{}
	if rows, known := projection.Acquisition.Value(); known {
		for _, row := range rows {
			if row.Designated || undispatched[row.ID] {
				busy[row.Resource] = busy[row.Resource] || !held[row.ID]
				held[row.ID] = true
			}
		}
	}
	for thing := range undispatched {
		held[thing] = true
	}
	pending := withoutStalled(projection.PendingFoodNutrition, projection.Acquisition, stalledSources, food)
	deficit := domain.Unknown[float64]()
	runway := domain.Unknown[float64]()
	if food {
		plan, known := projection.Facts.FoodPlan.Value()
		if !known {
			return RoutineAcquisitionResult{Verdict: fieldUnavailable("food_plan")}, nil
		}
		runway = plan.Forecast.RunwayDays
		projection.Acquisition, deficit = foodPlanAcquisition(plan, projection.Acquisition)
		clockSchedulerLog("Food acquisition: %s", plan.Explain())
	}
	slots := domain.Unknown[int]()
	if n, known := projection.PendingHunts.Value(); known {
		slots = domain.Known(max(0, 2-n))
	}
	// A hunt needs a hunter: with the roster known and HunterFor (Shooting,
	// a ranged primary, never a Brawler) finding nobody, the hunting budget
	// is zero and only gathering is proposed, instead of a designation
	// native's hunt preview would refuse for want of a free ranged hunter.
	noHunter := false
	if pawns, known := projection.WorkPawns.Value(); known {
		if _, ok := policy.HunterFor(policy.Profiles(pawns)); !ok {
			slots = domain.Known(0)
			noHunter = true
		}
	}
	var selected []policy.AcquisitionSource
	existing := false
	if stockGoal {
		selected, existing, err = r.resourceSelection(call, state.Snapshot, expected.Tick, projection, held, busy, slots)
	} else if pest {
		selected, err = policy.SelectPestAcquisition(projection.Acquisition, pests, held, slots)
	} else {
		sources := projection.Acquisition
		if huntOnly {
			sources = huntRows(sources)
		}
		selected, err = policy.SelectAcquisition(sources, deficit, pending, food, held, slots)
	}
	if rows, known := projection.Acquisition.Value(); known && !pest && !stockGoal {
		hunts := 0
		for _, row := range rows {
			if row.Hunt {
				hunts++
			}
		}
		clockSchedulerLog("%s: acquisition select rows=%d hunts=%d deficit=%v pending=%v slots=%v held=%d selected=%d err=%v", goal.Goal.ID, len(rows), hunts, deficit, pending, slots, len(held), len(selected), err)
	}
	if err != nil {
		return RoutineAcquisitionResult{Verdict: fieldUnavailable(unreadAcquisitionFact(projection.Acquisition, deficit, pending, pest || stockGoal))}, nil
	}
	if len(selected) == 0 && existing {
		return RoutineAcquisitionResult{Verdict: BuildingReasonExistingWork}, nil
	}
	if len(selected) == 0 {
		return RoutineAcquisitionResult{Verdict: noAcquisition(r.need, noHunter, deficit, pending)}, nil
	}
	hash := sha256.New()
	for _, row := range selected {
		fmt.Fprintf(hash, "%s/%s/%s/%d/%d\n", row.ID, row.Resource, row.Token, row.Cell.X, row.Cell.Z)
	}
	prefix := "acquire"
	if pest {
		// Every admission the goal ever made salts the pest method: a
		// cancelled hunt of an animal that came back to the same cell in
		// the same state rehashes to a fresh method instead of reading as
		// already used (#214).
		fmt.Fprintf(hash, "#%d\n", goal.Admitted)
		prefix = "pest-hunt"
	}
	if stockGoal {
		prefix = "resource-acquire"
	}
	method := domain.MethodID(fmt.Sprintf("%s-%x", prefix, hash.Sum(nil)[:16]))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineAcquisitionResult{Verdict: waitFor(WaitMethodUsed, "acquisition_method")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineAcquisitionResult{}, err
	}
	id := domain.MintPlanID()
	var actions []domain.Action
	for i, row := range selected {
		value, err := domain.NewAcquisition(row.ID, row.Resource, row.Cell)
		if err != nil {
			return RoutineAcquisitionResult{}, err
		}
		action, err := domain.NewAcquisitionAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), value)
		if err != nil {
			return RoutineAcquisitionResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineAcquisitionResult{}, err
	}
	if p.session.State() != state {
		return RoutineAcquisitionResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethodReason(call, goal.Goal.ID, goal.Revision, method, acquisitionReason(food, pest, runway, selected), plan); err != nil {
		return RoutineAcquisitionResult{}, err
	}
	return RoutineAcquisitionResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// resourceSelection is MaintainResource's census selection: the floors
// worst-covered first, and for the first with chop, harvest or hunt
// sources the bill and mine planner does not outbid,
// policy.SelectCatalogAcquisition against its deficit. existing reports a
// target passed over for its designated work (busy).
func (r *RoutineAcquisitionPlanner) resourceSelection(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, projection observation.ColonyProjection, held, busy map[string]bool, slots domain.Fact[int]) (_ []policy.AcquisitionSource, existing bool, err error) {
	rows, known := projection.Acquisition.Value()
	if !known {
		return nil, false, errors.New("acquisition census unknown")
	}
	stock := projection.Facts.Resources
	targets, err := r.reviewer.resourceTargets(ctx, snapshot, stock)
	if err != nil {
		return nil, false, err
	}
	ranked, err := policy.RankResourceTargets(targets, stock)
	if err != nil {
		return nil, false, err
	}
	hunts, _ := slots.Value()
	for _, row := range ranked {
		// A designated resource is its own existing work; the goal's
		// other targets still plan (#1045).
		if busy[string(row.Resource)] {
			clockSchedulerLog("%s: catalog %s designated, existing work", r.need, row.Resource)
			existing = true
			continue
		}
		picked, best, err := policy.SelectCatalogAcquisition(rows, row.Resource, row.Target-resourceCount(stock, row.Resource), projection.Center, held, hunts)
		if err != nil {
			return nil, false, err
		}
		clockSchedulerLog("%s: catalog %s target=%d selected=%d", r.need, row.Resource, row.Target, len(picked))
		// Joint ranking with the bill and mine planner (#728): a resource
		// its fresh bid scores higher is left to it.
		if rival, yield := r.reviewer.bids.bid(snapshot, row.Resource, bidAcquisition, best.Score, best.Kind, tick); yield {
			clockSchedulerLog("%s: %s %s %.3f yields to %s %.3f", r.need, row.Resource, best.Kind, best.Score, rival.kind, rival.score)
			continue
		}
		if len(picked) > 0 {
			return picked, false, nil
		}
	}
	return nil, existing, nil
}

// Only admitted sources can start new work. Pending designations retain their
// native credit in SelectAcquisition; the ledger never calls them delivered.
func foodPlanAcquisition(plan policy.FoodPlan, sources domain.Fact[[]policy.AcquisitionSource]) (domain.Fact[[]policy.AcquisitionSource], domain.Fact[float64]) {
	rows, known := sources.Value()
	if !known {
		return domain.Unknown[[]policy.AcquisitionSource](), domain.Unknown[float64]()
	}
	open := map[string]bool{}
	for _, entry := range plan.Portfolio {
		if (entry.Channel.Kind == policy.FoodForage || entry.Channel.Kind == policy.FoodHunt) && entry.Decision == policy.FoodPlanOpen {
			open[entry.Channel.ID] = true
		}
	}
	selected, nutrition := []policy.AcquisitionSource{}, 0.0
	for _, row := range rows {
		if open[row.ID] {
			selected = append(selected, row)
			nutrition += row.NutritionYield
		}
	}
	return domain.Known(selected), domain.Known(nutrition)
}

// gonePestHunts finds the unissued (pending or prepared) hunt actions of a
// pest plan whose animal the wild-animal census no longer lists (dead or
// off the map). The census has to be known: an unknown census is not
// evidence the pack is gone. A pest that is merely ineligible this step
// (no free hunter) or has wandered off its planned cell keeps its row in
// the wild census and its hunt, which follows it (#321).
func gonePestHunts(progress []domain.Progress, wild domain.Fact[[]policy.UpkeepAnimal]) (gone []domain.ActionID) {
	animals, known := wild.Value()
	if !known {
		return nil
	}
	alive := map[string]bool{}
	for _, animal := range animals {
		alive[string(animal.ID)] = true
	}
	for _, p := range progress {
		acquisition, ok := p.Action().Acquisition()
		v := p.View()
		if ok && (v.Stage == domain.Pending || v.Stage == domain.Prepared) && !alive[acquisition.Thing()] {
			gone = append(gone, v.Action)
		}
	}
	return gone
}

// censusBlocking reads the goal's designated census rows, and the rows of
// its undispatched acquisitions, skipping held ones: block when a designated row is one the goal would plan, except that
// for food a designated plant harvest only restricts the goal to hunts
// (plants, #260). The wood goal counts designated trees yielding wood.
func censusBlocking(sources domain.Fact[[]policy.AcquisitionSource], food bool, held, undispatched map[string]bool) (block, plants bool) {
	rows, _ := sources.Value()
	for _, row := range rows {
		if !(row.Designated || undispatched[row.ID]) || held[row.ID] {
			continue
		}
		switch {
		case food && row.Food && !row.Hunt:
			plants = true
		case food && row.Food, !food && row.Tree && row.Resource == "WoodLog":
			return true, plants
		}
	}
	return false, plants
}

// undispatchedAcquisitions is the source things of the plan's acquisition
// actions not yet dispatched (Pending or Prepared).
func undispatchedAcquisitions(progress []domain.Progress) map[string]bool {
	things := map[string]bool{}
	for _, p := range progress {
		acquisition, ok := p.Action().Acquisition()
		if stage := p.View().Stage; ok && (stage == domain.Pending || stage == domain.Prepared) {
			things[acquisition.Thing()] = true
		}
	}
	return things
}

// huntRows keeps the census's hunt rows.
func huntRows(sources domain.Fact[[]policy.AcquisitionSource]) domain.Fact[[]policy.AcquisitionSource] {
	rows, known := sources.Value()
	if !known {
		return sources
	}
	var hunts []policy.AcquisitionSource
	for _, row := range rows {
		if row.Hunt {
			hunts = append(hunts, row)
		}
	}
	return domain.Known(hunts)
}

// stalledDesignations selects, from the acquisition census, the designated
// rows of one kind (hunt or not) the goal owns (mine) that no colonist has
// taken for at least the contract's deadline since native first saw the
// designation (#1044). No action pairing (#1046): a player's designation of
// the goal's kind stalls too (#719). The planner withdraws each so the goal
// re-plans from another source. A taken row (reserved, or a colonist's job
// targets it) is never stalled. Harvests run on
// RoutinePolicy.AcquisitionProgress (#291), hunts on HuntProgress. A
// contract without a deadline, or an unknown census, stalls nothing.
func stalledDesignations(sources domain.Fact[[]policy.AcquisitionSource], hunt bool, now domain.Tick, contract policy.ProgressContract, mine func(policy.AcquisitionSource) bool) []policy.AcquisitionSource {
	rows, known := sources.Value()
	if contract.Deadline <= 0 || !known {
		return nil
	}
	var stalled []policy.AcquisitionSource
	for _, row := range rows {
		if row.Designated && !row.Taken && row.Hunt == hunt && mine(row) && contract.Expired(row.DesignatedTick, now) {
			stalled = append(stalled, row)
		}
	}
	return stalled
}

// stallWithdraw is the one-action withdraw method of one stalled row
// (#1046). Its method id hashes the source and the designation's first-seen
// tick: a withdraw not yet read back is not filed twice, and a later
// re-designation of the same source rehashes.
func stallWithdraw(row policy.AcquisitionSource) (domain.MethodID, domain.PlanSpec, error) {
	hash := sha256.New()
	fmt.Fprintf(hash, "%s/%s/%d/%d/%d\n", row.ID, row.Resource, row.Cell.X, row.Cell.Z, row.DesignatedTick)
	method := domain.MethodID(fmt.Sprintf("withdraw-%x", hash.Sum(nil)[:16]))
	value, err := domain.NewAcquisition(row.ID, row.Resource, row.Cell)
	if err != nil {
		return "", domain.PlanSpec{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewAcquisitionWithdrawAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return "", domain.PlanSpec{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	return method, plan, err
}

// withoutStalled takes the census rows this step withdrew as stalled off
// native's pending total (nutrition for food, yield otherwise): a
// designation nobody took still counts there, and without this the re-plan
// would see its own stalled yield as covering the deficit and propose
// nothing.
func withoutStalled(pending domain.Fact[float64], sources domain.Fact[[]policy.AcquisitionSource], stalled map[string]bool, food bool) domain.Fact[float64] {
	rows, known := sources.Value()
	outstanding, pk := pending.Value()
	if !known || !pk || len(stalled) == 0 {
		return pending
	}
	for _, row := range rows {
		if !stalled[row.ID] || !row.Designated {
			continue
		}
		if food {
			outstanding -= row.NutritionYield
		} else {
			outstanding -= row.Yield
		}
	}
	return domain.Known(max(0, outstanding))
}

// acquisitionReason is the admitted method's short why for Operation.intent
// (#846): the food runway the plan budgets against, the animal a pest hunt
// targets, or the stock a resource method gathers.
func acquisitionReason(food, pest bool, runway domain.Fact[float64], selected []policy.AcquisitionSource) string {
	if len(selected) == 0 {
		return ""
	}
	switch {
	case pest:
		return "pest " + selected[0].Definition
	case food:
		if days, known := runway.Value(); known {
			return fmt.Sprintf("food runway %.1fd", days)
		}
		return ""
	}
	return selected[0].Resource + " low"
}

// noAcquisition says why the selection chose no source although the goal is
// owed: a pest hunt waits for a ranged hunter or has every pest already
// hunted, food in flight already covers the deficit, or no source stands.
func noAcquisition(need policy.GoalID, noHunter bool, deficit, pending domain.Fact[float64]) Verdict {
	if need == policy.ClearPests {
		if noHunter {
			return noWorker("hunter")
		}
		return BuildingReasonNoDeficit
	}
	owed, owedKnown := deficit.Value()
	inFlight, inFlightKnown := pending.Value()
	if owedKnown && inFlightKnown && inFlight >= owed {
		return BuildingReasonExistingWork
	}
	return awaitingPlan("acquisition_source", string(need))
}

// unreadAcquisitionFact names what the source selection lacked when it could
// not choose: the source census, then (for goals sized by a deficit) the
// deficit and the pending yield. A census that is read but holds an unusable
// row reads as the census.
func unreadAcquisitionFact(sources domain.Fact[[]policy.AcquisitionSource], deficit, pending domain.Fact[float64], withoutDeficit bool) string {
	if _, known := sources.Value(); !known {
		return "acquisition_sources"
	}
	if !withoutDeficit {
		if _, known := deficit.Value(); !known {
			return "acquisition_deficit"
		}
		if _, known := pending.Value(); !known {
			return "pending_acquisition"
		}
	}
	return "acquisition_sources"
}
