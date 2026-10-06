package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoundsSleepingUpkeepPlanner answers MaintainHousing's bedroom phase: it transfers
// ownership of a vacant suitable bed to a colonist without one (a one-shot
// AssignIntent native checks for roof, access, allowed area and the
// pawn's comfortable band), and when no bed can be assigned it stages one
// through the same building ladder the hospital walks (furnish a
// Bedroom-hosting room warm enough for the waiting colonists, else a starter
// shell first), assigning it on a later review. Neither the assignment nor
// the construction receipt recovers the goal; ReviewSleeping does, on the
// assigned pawn's observed use of that bed.
type RoundsSleepingUpkeepPlanner struct {
	reviewer *Rounder
	native   RoundsBuildingSource
	building *RoundsBuildingPlanner
}

func NewRoundsSleepingUpkeepPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsSleepingUpkeepPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsSleepingUpkeepPlanner: reviewer == nil || native == nil", ErrControl)
	}
	if _, ok := native.(observation.RoundsSource); !ok {
		return nil, fmt.Errorf("%w: NewRoundsSleepingUpkeepPlanner: !ok", ErrControl)
	}
	building := &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.MaintainHousing, phase: policy.HousingSleeping}
	return &RoundsSleepingUpkeepPlanner{reviewer: reviewer, native: native, building: building}, nil
}

// sleepingRequest re-derives the sleeping targets from this step's census
// against the review's retained use history, so the choice and the build
// site share one observation.
func sleepingRequest(facts observation.ColonyProjection, review store.Rounds) (policy.SleepingRequest, error) {
	sleeping, err := policy.ReviewSleeping(facts.Facts.Sleeping, review.Sleeping, facts.Identity.Tick)
	if err != nil {
		return policy.SleepingRequest{}, err
	}
	definitions := make([]policy.BenchDefinition, 0, len(facts.Definitions))
	for _, d := range facts.Definitions {
		definitions = append(definitions, policy.BenchDefinition{Name: d.Name, Available: d.Available, NeedsPower: d.NeedsPower, ConstructionSkill: d.ConstructionSkill})
	}
	stocked := map[string]bool{}
	for _, name := range []string{policy.SleepingBedrollDefinition, policy.SleepingCoupleBedrollDefinition} {
		_, _, stocked[name] = facts.StockedStuff(name)
	}
	request := policy.SleepingRequest{Targets: sleeping.Targets, Sleeping: facts.Facts.Sleeping, Rooms: facts.Rooms, Definitions: definitions, Furniture: facts.Shapes.Furniture, Stocked: stocked, Traits: sleepingTraits(facts)}
	if obs, known := facts.Facts.Sleeping.Value(); known {
		tier, _ := facts.BuildTier.Value()
		request.RoomTargets = policy.RoomQualityTargets(obs, request.Traits, tier, facts.Impressiveness)
	}
	return request, nil
}

// sleepingTraits is each colonist's trait effects from the work census,
// nil while it is unknown (#813).
func sleepingTraits(facts observation.ColonyProjection) map[policy.PawnID]policy.TraitEffects {
	pawns, known := facts.WorkPawns.Value()
	if !known {
		return nil
	}
	out := make(map[policy.PawnID]policy.TraitEffects, len(pawns))
	for _, p := range pawns {
		out[p.ID] = policy.BuildProfile(p).Effects
	}
	return out
}

// bedroomSwap is the next room quality swap (#813): a jealous colonist into
// the best solo bedroom, an ascetic into the plainest.
func bedroomSwap(facts observation.ColonyProjection) (policy.BedroomSwap, bool) {
	obs, known := facts.Facts.Sleeping.Value()
	traits := sleepingTraits(facts)
	if !known || traits == nil {
		return policy.BedroomSwap{}, false
	}
	plan, _ := facts.LayoutPlan.Value()
	rooms, _ := facts.Rooms.Value()
	return policy.NextBedroomSwap(obs, traits, policy.SuiteRoomIDs(plan, rooms))
}

// bedroomsFirst reports whether the bedroom ladder answers a sleeping
// choice: with no demand, and ahead of a barracks bed while colonists own
// only spots or no bed is buildable (#1182, bedrooms before bedrolls).
// A colonist owning no bed is unhoused there too (#1197).
func bedroomsFirst(method policy.SleepingMethod) bool {
	return method == policy.SleepingNoDemand || method == policy.SleepingBuild || method == policy.SleepingUnavailable
}

// selectSleeping resolves the building ladder's definition and site from
// the same census the sleeping planner chose from: only a SleepingBuild
// choice furnishes; every other outcome is reported, never built around.
func (r *RoundsBuildingPlanner) selectSleeping(facts observation.ColonyProjection, review store.Rounds) (*RoundsBuildingPlanner, Verdict, error) {
	request, err := sleepingRequest(facts, review)
	if err != nil {
		return nil, Verdict{}, err
	}
	choice, err := policy.SelectSleepingMethod(request)
	if err != nil {
		return nil, Verdict{}, err
	}
	switch choice.Method {
	case policy.SleepingUnknown:
		return nil, fieldUnavailable(choice.Missing), nil
	case policy.SleepingNoDemand:
		return nil, BuildingSleepingUseNeeded, nil
	case policy.SleepingAssign:
		return nil, awaitingPlan("bed_assignment", ""), nil
	case policy.SleepingUnavailable:
		return nil, BuildingSleepingUnavailable, nil
	}
	return r.resolveSleeping(facts, choice)
}

// resolveSleeping is the building ladder resolved for a SleepingBuild choice.
func (r *RoundsBuildingPlanner) resolveSleeping(facts observation.ColonyProjection, choice policy.SleepingChoice) (*RoundsBuildingPlanner, Verdict, error) {
	if len(choice.Cells) == 0 {
		return nil, noSpace("sleeping_cells"), nil
	}
	facility, err := policy.Facility(policy.RoomRoleBedroom)
	if err != nil {
		return nil, Verdict{}, err
	}
	resolved := *r
	resolved.definition = choice.Definition
	resolved.environment = policy.PlacementIndoors
	resolved.facility = &facility
	resolved.cells = choice.Cells
	resolved.sleeping = &choice
	resolved.stuff = bedStuff(facts, resolved.definition)
	return &resolved, Verdict{}, nil
}

// bedStuff is the stuff a bed step builds definition from: a bedroll's
// first stuff option in stock (#1181), else the cheapest the rows allow.
func bedStuff(facts observation.ColonyProjection, definition string) string {
	if definition == policy.SleepingBedrollDefinition || definition == policy.SleepingCoupleBedrollDefinition {
		if stuff, _, ok := facts.StockedStuff(definition); ok {
			return stuff
		}
	}
	return facts.BuildStuff(definition)
}

func (r *RoundsSleepingUpkeepPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsBuildingResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoundsBuildingResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsBuildingResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainHousing)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if !workable || review.Latches.Housing != policy.HousingSleeping {
		return RoundsBuildingResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, m := range goal.OwnerMethods() {
		plan, err := p.journal.LoadPlan(call, m.Plan)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsBuildingResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	// A completed assignment is retired from the goal's active methods once
	// its epoch is cleaned, so the epoch's full method history is what
	// still earns the observation window.
	var observe uint32
	methods, err := p.journal.LoadOwnerMethods(call, goal)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	for _, m := range methods {
		// Only a bed assignment is followed by observed sleep.
		if strings.HasPrefix(string(m.Method), "throne-") {
			continue
		}
		plan, err := p.journal.LoadPlan(call, m.Plan)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		observe = max(observe, sleepingNativeWorkTicks(plan, state.Snapshot, review.Tick))
	}
	result, err := r.decide(call, epoch, arbiter, state, review, goal)
	if err == nil && result.Verdict != BuildingReasonAdmitted && result.NativeWorkTicks == 0 {
		result.NativeWorkTicks = observe
	}
	return result, err
}

// sleepingObservationBudget bounds the game time the planner asks for after
// an assignment completed so the colonist's sleep in that bed can be
// observed: one full day covers the next rest period, and the budget is not
// renewed by later observations.
const sleepingObservationBudget = domain.TicksPerDay

// sleepingObservationSlice is one clock window inside that budget.
const sleepingObservationSlice = 600

// sleepingNativeWorkTicks is the clock window a completed assignment of this
// epoch still earns; only observed use recovers the goal, and without ticks
// nobody sleeps.
func sleepingNativeWorkTicks(plan store.PlanState, current domain.GenerationSnapshot, tick domain.Tick) uint32 {
	if len(plan.Progress) != 1 {
		return 0
	}
	p := plan.Progress[0]
	if _, ok := p.Action().Assign(); !ok {
		return 0
	}
	// The assignment's observation is taken at the root plan's scope, so
	// only the world (colony, load, map) is compared, not the plan.
	v := p.View()
	effect, known := v.Effect.Value()
	if v.Stage != domain.Completed || v.Unresolved || !known || effect != domain.EffectCompleted || !boundary.World(v.Snapshot, current) || tick < v.Tick || tick-v.Tick >= sleepingObservationBudget {
		return 0
	}
	return min(uint32(sleepingObservationSlice), uint32(sleepingObservationBudget-(tick-v.Tick)))
}

func (r *RoundsSleepingUpkeepPlanner) decide(call, epoch context.Context, arbiter *stepArbiter, state ControlState, review store.Rounds, goal store.WorkOwner) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	started := r.reviewer.clock.Now()
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsBuildingResult{}, fmt.Errorf("%w: decide: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	reading, err := r.reviewer.observeRooms(call, r.native.(observation.RoundsSource), expected, domain.Unknown[[]policy.ConstructionClaim](), append(append(append([]string{"Wall", "Door"}, policy.RoomBeautyDefinitions()...), r.reviewer.rememberedThrones()...), r.reviewer.census.rememberedWorship()...)...)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	facts := reading.Projection
	// One packed read per pass, shared by every owner of stored pieces.
	stock := newPackedStock(r.native, boundary.Identity(state.Snapshot))
	request, err := sleepingRequest(facts, review)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	choice, err := policy.SelectSleepingMethod(request)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	// swapping flags a bedroom swap: native evicts the bed's owner (#1243).
	swapping := false
	// A couple's double bed comes before any other bed change (#843).
	if choice.Method != policy.SleepingUnknown {
		if result, due, err := r.coupleBed(call, epoch, stock, state, review, goal, reading); due || err != nil {
			return result, err
		}
	}
	if bedroomsFirst(choice.Method) && bedroomStep(facts, r.reviewer.stage).Kind != policy.BedroomNone {
		choice.Method = policy.SleepingNoDemand
	}
	switch choice.Method {
	case policy.SleepingUnknown:
		return RoundsBuildingResult{Verdict: fieldUnavailable(choice.Missing)}, nil
	case policy.SleepingNoDemand:
		// A bed replacement under way finishes first (#829): its new bed
		// stands unowned until the owner moves.
		if rep, due := bedReplacement(facts, r.reviewer.stage); due && rep.Step == policy.BedReplaceAssign {
			choice = policy.SleepingChoice{Method: policy.SleepingAssign, Pawn: rep.Pawn, Bed: rep.Bed, PreviousBed: rep.PreviousBed}
			break
		} else if due && rep.Step == policy.BedReplaceRemove {
			return r.removeOldBed(call, epoch, state, review, goal, reading, rep)
		}
		// Everyone owns a bed: walk them into planned bedrooms (#786).
		step := bedroomStep(facts, r.reviewer.stage)
		if step.Kind == policy.BedroomNone {
			// Then pawns leave a Retiring wing, one per step (#1219).
			step = migrateStep(facts)
		}
		switch step.Kind {
		case policy.BedroomMove:
			choice = policy.SleepingChoice{Method: policy.SleepingAssign, Pawn: step.Pawn, Bed: step.Bed, PreviousBed: step.PreviousBed}
		case policy.BedroomReconcile:
			// The room's ring, floor and bed through the shared build side; a
			// bed left empty in the starter shell is packed and reinstalled
			// rather than built again (#2115).
			return r.reconcileBedroom(call, epoch, stock, state, review, goal, reading, step)
		case policy.BedroomClear:
			return r.removeOldBed(call, epoch, state, review, goal, reading, policy.BedReplacement{Room: "shell", Bed: step.Bed, Def: policy.SleepingSpotDefinition, Cell: step.Cells[0]})
		default:
			// The title's throne room: reconciled to the plan and its template,
			// then furnished through the room upgrade below (#1601, #2109).
			if throne := throneStep(facts); throne.Owed() {
				return r.stageThrone(call, epoch, arbiter, stock, state, review, goal, reading, throne)
			} else if throne.Failed() {
				// A requirement without an available definition is a named
				// failure (#1874); forbidden buildings are packed (#2109).
				return RoundsBuildingResult{Verdict: siteBlocked("throne room", throne.Detail())}, nil
			}
			// The Biotech child rooms, worship room and containment cell:
			// reconciled to the plan and the role's template (#1680, #2112).
			if child := childRoomStep(facts); child.Owed() {
				return r.stageChildRoom(call, epoch, stock, state, review, goal, reading, child)
			}
			swap, ok := bedroomSwap(facts)
			if !ok {
				if upgrade, due := titleFurniture(facts); due {
					return r.upgradeBedroom(call, epoch, state, review, goal, reading, upgrade)
				}
				if upgrade, due := companionBed(facts); due {
					return r.upgradeBedroom(call, epoch, state, review, goal, reading, upgrade)
				}
				if upgrade, due := roomUpgrade(facts, r.reviewer.stage); due {
					return r.upgradeBedroom(call, epoch, state, review, goal, reading, upgrade)
				}
				if rep, due := bedReplacement(facts, r.reviewer.stage); due && rep.Step == policy.BedReplaceBuild {
					return r.upgradeBedroom(call, epoch, state, review, goal, reading, policy.RoomUpgrade{Room: rep.Room, Slot: "bed", Def: rep.Def, Stuff: rep.Stuff, Anchor: rep.Cell, Rot: rep.Rot})
				}
				if upgrade, due := beautyUpgrade(facts, r.reviewer.stage); due {
					return r.upgradeBedroom(call, epoch, state, review, goal, reading, upgrade)
				}
				if result, due, err := r.sculptBedroom(call, epoch, stock, state, goal, reading); due || err != nil {
					return result, err
				}
				return RoundsBuildingResult{Verdict: BuildingSleepingUseNeeded}, nil
			}
			choice = policy.SleepingChoice{Method: policy.SleepingAssign, Pawn: swap.Pawn, Bed: swap.Bed, PreviousBed: swap.PreviousBed}
			swapping = true
		}
	case policy.SleepingUnavailable:
		return RoundsBuildingResult{Verdict: BuildingSleepingUnavailable}, nil
	case policy.SleepingMarkSlaves:
		return r.markSlaveBed(call, epoch, state, goal, choice.Bed)
	case policy.SleepingBuild:
		// A stored bed is reinstalled before a new one is built (#843).
		if result, due, err := r.reinstallStoredBed(call, epoch, stock, state, goal, reading, choice); due || err != nil {
			return result, err
		}
		return r.building.step(call, epoch, arbiter)
	}
	// Assign: one pawn, one bed, once per Episode. A method that already
	// ran this epoch (the native side refused it, or the player undid it) is
	// not retried; the next epoch reconsiders. The one exception is an
	// intent native refused: that leaves no effect behind, so a bounded
	// number of fresh attempts follow.
	method, err := r.assignMethod(call, goal, fmt.Sprintf("sleeping-assign-%s-%s", choice.Pawn, choice.Bed))
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if method == "" {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "bed_assignment")}, nil
	}
	previous := domain.ClearPrevious()
	if choice.PreviousBed != "" {
		if previous, err = domain.KnownPrevious(choice.PreviousBed); err != nil {
			return RoundsBuildingResult{}, err
		}
	}
	assign, err := domain.NewAssign(domain.PawnID(choice.Pawn), choice.Bed, previous)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if swapping {
		assign = assign.AsSwap()
	}
	if !arbiter.tryClaim(nil, "bed:"+choice.Bed) {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "bed_claim")}, nil
	}
	id := domain.MintPlanID()
	action, err := domain.NewAssignAction(domain.ActionID(fmt.Sprintf("%s-0", id)), assign)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsBuildingResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsBuildingResult{}, fmt.Errorf("%w: decide: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	latest, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if latest.Revision != review.Revision || !latest.Enabled {
		return RoundsBuildingResult{}, fmt.Errorf("%w: decide: latest.Revision != review.Revision || !latest.Enabled", ErrControl)
	}
	if err = p.journal.CommitOwnerMethod(call, goal, method, "", plan); err != nil {
		return RoundsBuildingResult{}, err
	}
	return RoundsBuildingResult{Verdict: BuildingReasonAdmitted}, nil
}

// sleepingAssignAttempts bounds the refused assignment attempts one goal
// epoch may make for the same pawn and bed.
const sleepingAssignAttempts = 3

// assignMethod returns the method ID for the next assignment attempt of this
// epoch under base, or "" when the pair was already applied (or the
// attempt bound is spent).
func (r *RoundsSleepingUpkeepPlanner) assignMethod(call context.Context, goal store.WorkOwner, base string) (domain.MethodID, error) {
	p := r.reviewer.player
	for try := 0; try < sleepingAssignAttempts; try++ {
		method := domain.MethodID(base)
		if try > 0 {
			method = domain.MethodID(fmt.Sprintf("%s-retry%d", base, try))
		}
		existing, err := p.journal.LoadOwnerMethod(call, goal, method)
		if errors.Is(err, store.ErrNotFound) {
			return method, nil
		}
		if err != nil {
			return "", err
		}
		plan, err := p.journal.LoadPlan(call, existing.Plan)
		if err != nil {
			return "", err
		}
		if !sleepingAssignUnadmitted(plan.Progress) {
			return "", nil
		}
	}
	return "", nil
}

// sleepingAssignUnadmitted reports a settled plan whose every action ended
// absent: native refused it, so nothing changed.
func sleepingAssignUnadmitted(progress []domain.Progress) bool {
	if len(progress) == 0 || domain.StandardWorkOpen(progress) {
		return false
	}
	for _, pr := range progress {
		effect, known := pr.View().Effect.Value()
		if !known || effect != domain.EffectAbsent {
			return false
		}
	}
	return true
}

// markSlaveBed commits one patch setting bed for slaves (#1036), once per
// bed per Episode; the next review assigns the waiting slave to it.
func (r *RoundsSleepingUpkeepPlanner) markSlaveBed(call, epoch context.Context, state ControlState, goal store.WorkOwner, bed string) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	method := domain.MethodID("sleeping-slave-bed-" + bed)
	if _, err := p.journal.LoadOwnerMethod(call, goal, method); err == nil {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "slave_bed_marking")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsBuildingResult{}, err
	}
	patch, err := domain.NewBedSlaves(bed)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewBedUseAction(domain.ActionID(fmt.Sprintf("%s-0", id)), patch)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsBuildingResult{}, err
	}
	if p.session.State() != state {
		return RoundsBuildingResult{}, fmt.Errorf("%w: markSlaveBed: p.session.State() != state", ErrControl)
	}
	if err = p.journal.CommitOwnerMethod(call, goal, method, "", plan); err != nil {
		return RoundsBuildingResult{}, err
	}
	return RoundsBuildingResult{Verdict: BuildingReasonAdmitted}, nil
}
