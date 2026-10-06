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

type RoundsAcquisitionPlanner struct {
	reviewer *Rounder
	need     policy.ConcernID
}

type RoundsAcquisitionResult struct {
	Verdict
	Plan domain.PlanID
}

// NewRoundsAcquisitionPlanner plans one acquisition goal: EnsureFoodSupply
// harvests and hunts toward its food plan; ClearPests
// (#247) hunts every recognised pest the wild-animal census reports, one
// hunt method per admission, until none remain; MaintainResource chops,
// forages and hunts toward its ranked floors through the acquisition
// catalog, beside RoundsResourcePlanner's bills and mines.
func NewRoundsAcquisitionPlanner(reviewer *Rounder, need policy.ConcernID) (*RoundsAcquisitionPlanner, error) {
	if reviewer == nil || (need != policy.EnsureFoodSupply && need != policy.ClearPests && need != policy.MaintainResource) {
		return nil, fmt.Errorf("%w: NewRoundsAcquisitionPlanner: reviewer == nil || (need != policy.EnsureFoodSupply && need != policy.ClearPests && need != policy.Maintain", ErrControl)
	}
	return &RoundsAcquisitionPlanner{reviewer: reviewer, need: need}, nil
}
func (r *RoundsAcquisitionPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsAcquisitionResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsAcquisitionResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoundsAcquisitionResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsAcquisitionResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsAcquisitionResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, r.need)
	if err != nil {
		return RoundsAcquisitionResult{}, err
	}
	if !workable {
		return RoundsAcquisitionResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	if goal.Standard.Priority >= 3 {
		selected := false
		for _, row := range review.Development.Rows {
			selected = selected || row.Concern == r.need && row.Selected
		}
		if !selected {
			return RoundsAcquisitionResult{Verdict: awaitingSlot(string(r.need))}, nil
		}
	}
	expected, projection, err := r.reviewer.acquisitionReading(call, state, review)
	if err != nil {
		return RoundsAcquisitionResult{}, err
	}
	pest := r.need == policy.ClearPests
	stockGoal := r.need == policy.MaintainResource
	food := r.need == policy.EnsureFoodSupply
	huntOnly := false
	pests := policy.PestCensus(projection.Facts.AnimalUpkeep.WildAnimals)
	stalledSources := map[string]bool{}
	// A source a stall rotated away from stays keyed out for its contract's
	// bounded cooldown (#629): the goal's progress record carries the key,
	// and RecordProgressCooldown adds one under this review's revision.
	progress, _ := review.ConcernProgress(r.need)
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
			return RoundsAcquisitionResult{}, err
		}
		if pest {
			// An unissued pest hunt follows its animal wherever the census
			// reports it; one whose animal died to the defense or left the
			// map would be held forever and the goal behind it, so it is
			// cancelled at once.
			for _, action := range gonePestHunts(plan.Progress, projection.Facts.AnimalUpkeep.WildAnimals) {
				if _, err = p.journal.Cancel(call, method.Plan, action); err != nil {
					return RoundsAcquisitionResult{}, err
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
				return RoundsAcquisitionResult{}, err
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
					return RoundsAcquisitionResult{}, err
				}
				if _, err = p.journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, method); err == nil {
					continue
				} else if !errors.Is(err, store.ErrNotFound) {
					return RoundsAcquisitionResult{}, err
				}
				if err = cool(contract, row.ID); err != nil {
					return RoundsAcquisitionResult{}, err
				}
				if err = p.current(call, epoch); err != nil {
					return RoundsAcquisitionResult{}, err
				}
				if p.session.State() != state {
					return RoundsAcquisitionResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
				}
				if _, err = p.journal.CommitMethodReason(call, goal.Standard.ID, goal.Revision, method, "withdraw stalled "+row.Resource, plan); err != nil {
					return RoundsAcquisitionResult{}, err
				}
				return RoundsAcquisitionResult{Verdict: BuildingReasonAdmitted, Plan: plan.ID()}, nil
			}
		}
	}
	held := map[string]bool{}
	if !pest {
		held = cooledSources(projection.Acquisition, r.reviewer.policy.ChopMinGrowth, func(id string) bool {
			return cooled[id] || acquisitionCooled(progress, r.reviewer.policy, id, expected.Tick)
		})
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
			return RoundsAcquisitionResult{Verdict: BuildingReasonExistingWork}, nil
		}
		huntOnly = plants
	}
	// A designated or undispatched source is held: it is not planned again
	// (#1045). Its resource is busy for MaintainResource unless a cooldown
	// held it.
	busy := holdWorked(projection.Acquisition, undispatched, held)
	pending := withoutStalled(projection.PendingFoodNutrition, projection.Acquisition, stalledSources, food)
	deficit := domain.Unknown[float64]()
	runway := domain.Unknown[float64]()
	if food {
		plan, known := projection.Facts.FoodPlan.Value()
		if !known {
			return RoundsAcquisitionResult{Verdict: fieldUnavailable("food_plan")}, nil
		}
		runway = plan.Forecast.RunwayDays
		projection.Acquisition, deficit = foodPlanAcquisition(plan, projection.Acquisition)
	}
	slots, noHunter := huntSlots(projection)
	var selected []policy.AcquisitionSource
	existing := false
	if stockGoal {
		supply, err := r.reviewer.resourceSupply(call, state, review, goal)
		if err != nil {
			return RoundsAcquisitionResult{}, err
		}
		selected, existing = resourceSelection(supply, busy)
	} else if pest {
		selected, err = policy.SelectPestAcquisition(projection.Acquisition, pests, held, slots)
	} else {
		sources := projection.Acquisition
		if huntOnly {
			sources = huntRows(sources)
		}
		selected, err = policy.SelectAcquisition(sources, deficit, pending, food, held, slots)
	}
	if err != nil {
		return RoundsAcquisitionResult{Verdict: fieldUnavailable(unreadAcquisitionFact(projection.Acquisition, deficit, pending, pest || stockGoal))}, nil
	}
	if len(selected) == 0 && existing {
		return RoundsAcquisitionResult{Verdict: BuildingReasonExistingWork}, nil
	}
	if len(selected) == 0 {
		return RoundsAcquisitionResult{Verdict: noAcquisition(r.need, noHunter, deficit, pending)}, nil
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
	if _, err = p.journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, method); err == nil {
		return RoundsAcquisitionResult{Verdict: waitFor(WaitMethodUsed, "acquisition_method")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsAcquisitionResult{}, err
	}
	id := domain.MintPlanID()
	var actions []domain.Action
	for i, row := range selected {
		value, err := domain.NewAcquisition(row.ID, row.Resource, row.Cell)
		if err != nil {
			return RoundsAcquisitionResult{}, err
		}
		action, err := domain.NewAcquisitionAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), value)
		if err != nil {
			return RoundsAcquisitionResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsAcquisitionResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsAcquisitionResult{}, err
	}
	if p.session.State() != state {
		return RoundsAcquisitionResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
	}
	if _, err = p.journal.CommitMethodReason(call, goal.Standard.ID, goal.Revision, method, acquisitionReason(food, pest, runway, selected), plan); err != nil {
		return RoundsAcquisitionResult{}, err
	}
	return RoundsAcquisitionResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// resourceSelection is MaintainResource's census selection: the chop, harvest
// and hunt sources the Round's supply plan opened, for the first floor (worst
// covered first) that has some and no designated work in flight. existing
// reports a floor passed over for its designated work (busy).
func resourceSelection(supply *resourceSupply, busy map[string]bool) (_ []policy.AcquisitionSource, existing bool) {
	for _, resource := range supply.order {
		// A designated resource is its own existing work; the goal's
		// other targets still plan (#1045).
		if busy[string(resource)] {
			existing = true
			continue
		}
		picked := supply.acquisitions(resource)
		if len(picked) == 0 {
			continue
		}
		return picked, false
	}
	return nil, existing
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
		// A formation hunt executes as a HuntRequest, never as designations.
		if (entry.Channel.Kind == policy.CandidateForage || entry.Channel.Kind == policy.CandidateHunt && entry.Channel.Mode() == policy.HuntLone) && entry.Decision == policy.FoodPlanOpen {
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

// acquisitionCooled reports whether a source sits under a stall cooldown of
// either contract (#629).
func acquisitionCooled(progress policy.ConcernProgress, p policy.RoundsPolicy, thing string, tick domain.Tick) bool {
	return progress.Cooled(policy.CooldownKey(p.HuntProgress().Method, thing), tick) || progress.Cooled(policy.CooldownKey(p.AcquisitionProgress().Method, thing), tick)
}

// cooledSources are the census rows cooled reports: passed over like held
// ones, the cooldown lifting by itself at its bound, never a permanent ban.
func cooledSources(sources domain.Fact[[]policy.AcquisitionSource], minGrowth float64, cooled func(string) bool) map[string]bool {
	held := map[string]bool{}
	if rows, known := sources.Value(); known {
		for _, row := range rows {
			// An undesignated plantation tree under the chop gate is not offered (#2292).
			if cooled(row.ID) || !row.Designated && row.BelowChopGate(minGrowth) {
				held[row.ID] = true
			}
		}
	}
	return held
}

// holdWorked holds, in held, every designated or undispatched source, and
// returns the resources with such work not on cooldown: those are busy.
func holdWorked(sources domain.Fact[[]policy.AcquisitionSource], undispatched, held map[string]bool) map[string]bool {
	busy := map[string]bool{}
	if rows, known := sources.Value(); known {
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
	return busy
}

// acquisitionReading is the owned review reading an acquisition step plans
// on, its census without the sources a field plan reserves.
func (r *Rounder) acquisitionReading(call context.Context, state ControlState, review store.Rounds) (observation.Identity, observation.ColonyProjection, error) {
	p := r.player
	plans, err := p.journal.LoadPlans(call, 256)
	if err != nil {
		return observation.Identity{}, observation.ColonyProjection{}, err
	}
	playerPlans, err := p.journal.PlayerPlans(call, playerWorld(state.Snapshot))
	if err != nil {
		return observation.Identity{}, observation.ColonyProjection{}, err
	}
	definitions := roundsProjectDefinitions(plans, state.Snapshot, playerPlans)
	crops, err := r.sowableCrops(call, state.Snapshot)
	if err != nil {
		return observation.Identity{}, observation.ColonyProjection{}, err
	}
	definitions = uniqueFieldDefinitions(append(definitions, crops...))
	expected, err := stepScope(call, r.native)
	if err != nil {
		return observation.Identity{}, observation.ColonyProjection{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return observation.Identity{}, observation.ColonyProjection{}, fmt.Errorf("%w: acquisitionReading: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return observation.Identity{}, observation.ColonyProjection{}, err
	}
	read, err := r.observeOwned(call, r.native, expected, claims, definitions...)
	if err != nil {
		return observation.Identity{}, observation.ColonyProjection{}, err
	}
	projection := read.Projection
	projection.Acquisition = withoutFieldSources(projection.Acquisition, projection, plans, state.Snapshot)
	return expected, projection, nil
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
// RoundsPolicy.AcquisitionProgress (#291), hunts on HuntProgress. A
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
func noAcquisition(need policy.ConcernID, noHunter bool, deficit, pending domain.Fact[float64]) Verdict {
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
